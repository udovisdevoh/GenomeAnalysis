using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using GenomeAnalysis.Annotations.Http;
using GenomeAnalysis.Core.Annotations;
using Newtonsoft.Json.Linq;

namespace GenomeAnalysis.Annotations.MyVariant
{
    /// <summary>
    /// Reads variant annotations from MyVariant.info, which aggregates dbSNP,
    /// ClinVar, gnomAD and others behind one query.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the workhorse source: one batch request covers up to a thousand
    /// variants and returns clinical significance, review status and population
    /// frequency together. SNPedia is then only needed for human-readable text.
    /// </para>
    /// <para>
    /// <strong>Batching sharpens the privacy problem rather than softening it.</strong>
    /// A batch is a list of identifiers sent in one request; if that list was
    /// derived by filtering the user's file, the request describes their genome
    /// whether or not any allele is transmitted. Batches must come from the
    /// provider's public chip manifest.
    /// </para>
    /// </remarks>
    public sealed class MyVariantClient : IVariantAnnotationSource, IDisposable
    {
        public static readonly Uri DefaultEndpoint = new Uri("https://myvariant.info/v1/query");

        /// <summary>MyVariant.info accepts up to 1000 identifiers per POST.</summary>
        public const int MaxBatchSize = 1000;

        private readonly ThrottledHttpClient _http;
        private readonly Uri _endpoint;
        private bool _disposed;

        public MyVariantClient(
            ThrottleOptions? options = null,
            HttpClient? httpClient = null,
            Uri? endpoint = null)
        {
            _endpoint = endpoint ?? DefaultEndpoint;
            _http = new ThrottledHttpClient(options ?? ThrottleOptions.ForMyVariant(), httpClient);
        }

        public string SourceName => "MyVariant.info";

        public async Task<VariantAnnotation?> GetAsync(
            string rsId,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(rsId))
            {
                return null;
            }

            var results = await GetManyAsync(new[] { rsId }, cancellationToken).ConfigureAwait(false);
            return results.TryGetValue(rsId.Trim().ToLowerInvariant(), out var annotation) ? annotation : null;
        }

        public async Task<IReadOnlyDictionary<string, VariantAnnotation>> GetManyAsync(
            IEnumerable<string> rsIds,
            CancellationToken cancellationToken = default)
        {
            var results = new Dictionary<string, VariantAnnotation>(StringComparer.OrdinalIgnoreCase);

            if (rsIds == null)
            {
                return results;
            }

            var identifiers = rsIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim().ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var batch in Chunk(identifiers, MaxBatchSize))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var json = await _http.PostFormAsync(
                        _endpoint,
                        new[]
                        {
                            new KeyValuePair<string, string>("q", string.Join(",", batch)),
                            new KeyValuePair<string, string>("scopes", "dbsnp.rsid"),
                            new KeyValuePair<string, string>("fields", MyVariantMapper.RequestedFields)
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                foreach (var pair in ParseBatchResponse(json))
                {
                    // A variant can return several hits when it maps to multiple
                    // genomic representations. Keep the first that carries a
                    // clinical record, otherwise the first at all.
                    if (!results.TryGetValue(pair.Key, out var existing) || existing.Clinical == null)
                    {
                        results[pair.Key] = pair.Value;
                    }
                }
            }

            return results;
        }

        /// <summary>
        /// Parses a batch response body into annotations keyed by rsID. Public so
        /// it can be exercised against recorded fixtures.
        /// </summary>
        public static IEnumerable<KeyValuePair<string, VariantAnnotation>> ParseBatchResponse(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                yield break;
            }

            JToken parsed;

            try
            {
                parsed = JToken.Parse(json);
            }
            catch (Newtonsoft.Json.JsonException)
            {
                yield break;
            }

            var hits = parsed is JArray array
                ? array.OfType<JObject>()
                : new[] { parsed as JObject }.Where(o => o != null).Select(o => o!);

            foreach (var hit in hits)
            {
                var annotation = MyVariantMapper.MapHit(hit);

                if (annotation != null)
                {
                    yield return new KeyValuePair<string, VariantAnnotation>(annotation.RsId, annotation);
                }
            }
        }

        private static IEnumerable<IReadOnlyList<T>> Chunk<T>(IReadOnlyList<T> source, int size)
        {
            for (var offset = 0; offset < source.Count; offset += size)
            {
                yield return source.Skip(offset).Take(size).ToList();
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _http.Dispose();
        }
    }
}
