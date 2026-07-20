using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using GenomeAnalysis.Annotations.Http;
using GenomeAnalysis.Core.Annotations;

namespace GenomeAnalysis.Annotations.Snpedia
{
    /// <summary>
    /// Reads variant annotations from SNPedia through the MediaWiki API.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Uses <c>bots.snpedia.com/api.php</c>, the access path SNPedia provides for
    /// automated clients. The HTML pages on <c>snpedia.com</c> are not scraped.
    /// </para>
    /// <para>
    /// Content is CC BY-NC-SA 3.0 US: non-commercial, attribution required,
    /// share-alike. Every annotation returned carries its
    /// <see cref="SourceAttribution"/>, and reports must display it.
    /// </para>
    /// <para>
    /// <strong>One variant costs several requests</strong> — one for the SNP page
    /// plus one per genotype page — and requests are spaced a second apart. This
    /// is usable for filling gaps, not for annotating a whole genome. Pre-populate
    /// the cache from SNPedia's bulk export instead; see
    /// <c>snpedia.com/index.php/Bulk</c>.
    /// </para>
    /// </remarks>
    public sealed class SnpediaClient : IVariantAnnotationSource, IDisposable
    {
        public static readonly Uri DefaultEndpoint = new Uri("https://bots.snpedia.com/api.php");

        private readonly ThrottledHttpClient _http;
        private readonly Uri _endpoint;
        private readonly bool _ownsHttpClient;
        private bool _disposed;

        public SnpediaClient(
            ThrottleOptions? options = null,
            HttpClient? httpClient = null,
            Uri? endpoint = null)
        {
            _endpoint = endpoint ?? DefaultEndpoint;
            _http = new ThrottledHttpClient(options ?? ThrottleOptions.ForSnpedia(), httpClient);
            _ownsHttpClient = true;
        }

        public string SourceName => "SNPedia";

        public async Task<VariantAnnotation?> GetAsync(
            string rsId,
            CancellationToken cancellationToken = default)
        {
            if (!SnpediaPageNames.IsSnpPageTitle(rsId))
            {
                // Provider-internal identifiers such as i5000940 have no SNPedia
                // page. Absence here is expected, not a failure.
                return null;
            }

            var pageTitle = SnpediaPageNames.ForSnp(rsId);
            var snpPage = await BrowseBySubjectAsync(pageTitle, cancellationToken).ConfigureAwait(false);

            if (snpPage.IsEmpty)
            {
                return null;
            }

            var genotypePages = new List<KeyValuePair<string, SemanticData>>();

            foreach (var genotypeTitle in SnpediaMapper.DiscoverGenotypePageTitles(snpPage))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var genotypeData = await BrowseBySubjectAsync(genotypeTitle, cancellationToken).ConfigureAwait(false);
                genotypePages.Add(new KeyValuePair<string, SemanticData>(genotypeTitle, genotypeData));
            }

            return SnpediaMapper.MapSnpPage(rsId, snpPage, genotypePages);
        }

        /// <summary>
        /// Fetches several variants one at a time. SNPedia's semantic endpoint has
        /// no batch form, and the rate limit is honoured throughout, so this is
        /// slow by design. Use the cache.
        /// </summary>
        public async Task<IReadOnlyDictionary<string, VariantAnnotation>> GetManyAsync(
            IEnumerable<string> rsIds,
            CancellationToken cancellationToken = default)
        {
            var results = new Dictionary<string, VariantAnnotation>(StringComparer.OrdinalIgnoreCase);

            if (rsIds == null)
            {
                return results;
            }

            foreach (var rsId in rsIds)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var annotation = await GetAsync(rsId, cancellationToken).ConfigureAwait(false);

                if (annotation != null)
                {
                    results[annotation.RsId] = annotation;
                }
            }

            return results;
        }

        private async Task<SemanticData> BrowseBySubjectAsync(
            string pageTitle,
            CancellationToken cancellationToken)
        {
            var uri = new Uri(
                _endpoint +
                "?action=browsebysubject" +
                "&subject=" + Uri.EscapeDataString(pageTitle) +
                "&format=json");

            var json = await _http
                .GetStringAsync(() => new HttpRequestMessage(HttpMethod.Get, uri), cancellationToken)
                .ConfigureAwait(false);

            return SemanticData.Parse(json);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (_ownsHttpClient)
            {
                _http.Dispose();
            }
        }
    }
}
