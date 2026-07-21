using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace GenomeAnalysis.Annotations.Http
{
    /// <summary>
    /// Serialises requests to one host, spaces them out, and backs off
    /// exponentially when the source pushes back.
    /// </summary>
    /// <remarks>
    /// All calls to a given source go through one instance, so the rate limit
    /// holds across the whole application rather than per call site. Requests are
    /// strictly serial: no parallel fan-out, no burst.
    /// </remarks>
    public sealed class ThrottledHttpClient : IDisposable
    {
        static ThrottledHttpClient()
        {
            // .NET Framework 4.8 negotiates whatever ServicePointManager was left
            // set to, which on many machines excludes TLS 1.2. Public APIs refuse
            // anything older, and the failure surfaces as a request timeout rather
            // than a handshake error, which is thoroughly misleading.
            System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;

            // .NET Framework sends "Expect: 100-continue" on POST by default and
            // then waits for a go-ahead many servers never send. The request hangs
            // until the client timeout, which reads as a network problem rather
            // than a protocol default. GETs are unaffected, which makes it look
            // stranger still.
            System.Net.ServicePointManager.Expect100Continue = false;
        }

        private readonly HttpClient _httpClient;
        private readonly ThrottleOptions _options;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private readonly bool _ownsHttpClient;
        private DateTimeOffset _nextAllowedRequest = DateTimeOffset.MinValue;
        private bool _disposed;

        public ThrottledHttpClient(ThrottleOptions options, HttpClient? httpClient = null)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _ownsHttpClient = httpClient == null;
            _httpClient = httpClient ?? new HttpClient();
            _httpClient.Timeout = _options.RequestTimeout;

            if (!_httpClient.DefaultRequestHeaders.UserAgent.TryParseAdd(_options.UserAgent))
            {
                _httpClient.DefaultRequestHeaders.Add("User-Agent", _options.UserAgent);
            }
        }

        /// <summary>
        /// Sends a request, waiting for the rate limit and retrying retryable
        /// failures with exponential backoff.
        /// </summary>
        /// <param name="requestFactory">
        /// Builds the request. Called afresh for each attempt, because an
        /// <see cref="HttpRequestMessage"/> cannot be sent twice.
        /// </param>
        public async Task<string> GetStringAsync(
            Func<HttpRequestMessage> requestFactory,
            CancellationToken cancellationToken = default)
        {
            if (requestFactory == null)
            {
                throw new ArgumentNullException(nameof(requestFactory));
            }

            var backoff = _options.InitialBackoff;
            HttpRequestException? lastError = null;

            for (var attempt = 0; attempt <= _options.MaxRetries; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                await WaitForSlotAsync(cancellationToken).ConfigureAwait(false);

                HttpResponseMessage? response = null;

                try
                {
                    using (var request = requestFactory())
                    {
                        response = await _httpClient
                            .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                            .ConfigureAwait(false);
                    }

                    if (response.IsSuccessStatusCode)
                    {
                        return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    }

                    if (!IsRetryable(response.StatusCode))
                    {
                        throw new HttpRequestException(
                            "Request failed with status " + (int)response.StatusCode + " " + response.StatusCode + ".");
                    }

                    var retryAfter = GetRetryAfter(response);
                    lastError = new HttpRequestException(
                        "Request failed with retryable status " + (int)response.StatusCode + " " + response.StatusCode + ".");

                    if (attempt == _options.MaxRetries)
                    {
                        break;
                    }

                    var delay = retryAfter ?? backoff;
                    await Task.Delay(Min(delay, _options.MaximumBackoff), cancellationToken).ConfigureAwait(false);
                    backoff = Min(TimeSpan.FromTicks(backoff.Ticks * 2), _options.MaximumBackoff);
                }
                catch (HttpRequestException ex) when (attempt < _options.MaxRetries && lastError == null)
                {
                    // Transport-level failure: DNS, connection reset, TLS.
                    lastError = ex;
                    await Task.Delay(Min(backoff, _options.MaximumBackoff), cancellationToken).ConfigureAwait(false);
                    backoff = Min(TimeSpan.FromTicks(backoff.Ticks * 2), _options.MaximumBackoff);
                }
                finally
                {
                    response?.Dispose();
                }
            }

            throw lastError ?? new HttpRequestException("Request failed after " + _options.MaxRetries + " retries.");
        }

        public async Task<string> PostFormAsync(
            Uri uri,
            System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<string, string>> fields,
            CancellationToken cancellationToken = default)
        {
            var buffered = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, string>>(fields);

            return await GetStringAsync(
                    () => new HttpRequestMessage(HttpMethod.Post, uri)
                    {
                        Content = new FormUrlEncodedContent(buffered)
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task<string> PostJsonAsync(
            Uri uri,
            string json,
            CancellationToken cancellationToken = default)
        {
            return await GetStringAsync(
                    () =>
                    {
                        var request = new HttpRequestMessage(HttpMethod.Post, uri)
                        {
                            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
                        };

                        request.Headers.Accept.Add(
                            new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

                        return request;
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task WaitForSlotAsync(CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                var now = DateTimeOffset.UtcNow;
                var wait = _nextAllowedRequest - now;

                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                }

                _nextAllowedRequest = DateTimeOffset.UtcNow + _options.MinimumInterval;
            }
            finally
            {
                _gate.Release();
            }
        }

        private static bool IsRetryable(HttpStatusCode status)
        {
            if ((int)status >= 500)
            {
                return true;
            }

            return status == (HttpStatusCode)429 || status == HttpStatusCode.RequestTimeout;
        }

        private static TimeSpan? GetRetryAfter(HttpResponseMessage response)
        {
            var retryAfter = response.Headers.RetryAfter;

            if (retryAfter == null)
            {
                return null;
            }

            if (retryAfter.Delta.HasValue)
            {
                return retryAfter.Delta.Value;
            }

            if (retryAfter.Date.HasValue)
            {
                var delta = retryAfter.Date.Value - DateTimeOffset.UtcNow;
                return delta > TimeSpan.Zero ? delta : TimeSpan.Zero;
            }

            return null;
        }

        private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _gate.Dispose();

            if (_ownsHttpClient)
            {
                _httpClient.Dispose();
            }
        }
    }
}
