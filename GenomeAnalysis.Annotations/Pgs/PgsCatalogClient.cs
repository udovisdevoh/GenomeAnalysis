using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GenomeAnalysis.Annotations.Http;
using GenomeAnalysis.Core.Rules;
using Newtonsoft.Json.Linq;

namespace GenomeAnalysis.Annotations.Pgs
{
    /// <summary>
    /// Reads published polygenic scores from the PGS Catalog (EBI/NHGRI).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what makes polygenic scoring defensible: the variants and their
    /// weights come pre-specified from a registered, cited study, harmonized to a
    /// named build. The tool computes a published score; it never assembles one by
    /// multiplying odds ratios.
    /// </para>
    /// <para>
    /// Metadata (trait, development ancestry, citation) comes from the REST API;
    /// the weighted variant list comes from the harmonized scoring file, which
    /// pins every variant to the GRCh37 plus strand and so removes a layer of the
    /// strand ambiguity the rest of the pipeline has to fight.
    /// </para>
    /// </remarks>
    public sealed class PgsCatalogClient : IDisposable
    {
        public static readonly Uri RestBase = new Uri("https://www.pgscatalog.org/rest/");

        private readonly ThrottledHttpClient _http;
        private bool _disposed;

        public PgsCatalogClient(ThrottleOptions? options = null, HttpClient? httpClient = null)
        {
            _http = new ThrottledHttpClient(options ?? ForPgs(), httpClient);
        }

        public static ThrottleOptions ForPgs() => new ThrottleOptions
        {
            MinimumInterval = TimeSpan.FromMilliseconds(500),
            RequestTimeout = TimeSpan.FromMinutes(3)
        };

        public async Task<PolygenicScore?> GetScoreAsync(
            string pgsId,
            CancellationToken cancellationToken = default)
        {
            var metadataJson = await _http
                .GetStringAsync(
                    () => new HttpRequestMessage(HttpMethod.Get, new Uri(RestBase, "score/" + Uri.EscapeDataString(pgsId))),
                    cancellationToken)
                .ConfigureAwait(false);

            var metadata = JObject.Parse(metadataJson);

            var name = metadata["name"]?.Value<string>() ?? pgsId;
            var trait = metadata["trait_reported"]?.Value<string>() ?? "trait non précisé";
            var citation = metadata.SelectToken("publication.title")?.Value<string>()
                           ?? metadata["citation"]?.Value<string>()
                           ?? pgsId;
            var ancestry = metadata.SelectToken("ancestry_distribution.gwas.dist")?.Children<JProperty>()
                               .OrderByDescending(p => p.Value.Value<double?>() ?? 0)
                               .Select(p => p.Name)
                               .FirstOrDefault()
                           ?? "non précisée";

            // Harmonized to GRCh37: same build the sample fixtures and most vintage
            // consumer files use. A build mismatch is exactly what harmonization
            // exists to prevent.
            var scoringUri = new Uri(
                "https://ftp.ebi.ac.uk/pub/databases/spot/pgs/scores/" + pgsId +
                "/ScoringFiles/Harmonized/" + pgsId + "_hmPOS_GRCh37.txt.gz");

            var gzip = await _http.GetBytesAsync(scoringUri, cancellationToken).ConfigureAwait(false);
            var variants = ParseScoringFile(Decompress(gzip));

            return variants.Count == 0
                ? null
                : new PolygenicScore(
                    pgsId, name, trait, ancestry, citation, "GRCh37", variants);
        }

        /// <summary>
        /// Parses a harmonized scoring file. Public so it can be exercised against a
        /// recorded fixture without a download.
        /// </summary>
        public static IReadOnlyList<ScoreVariant> ParseScoringFile(string content)
        {
            var variants = new List<ScoreVariant>();

            if (string.IsNullOrWhiteSpace(content))
            {
                return variants;
            }

            string[]? header = null;

            foreach (var raw in content.Split('\n'))
            {
                var line = raw.TrimEnd('\r');

                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }

                var fields = line.Split('\t');

                if (header == null)
                {
                    header = fields.Select(f => f.Trim().ToLowerInvariant()).ToArray();
                    continue;
                }

                var variant = ParseVariant(header, fields);

                if (variant != null)
                {
                    variants.Add(variant);
                }
            }

            return variants;
        }

        private static ScoreVariant? ParseVariant(string[] header, string[] fields)
        {
            string? Get(params string[] names)
            {
                foreach (var name in names)
                {
                    var index = Array.IndexOf(header, name);

                    if (index >= 0 && index < fields.Length)
                    {
                        var value = fields[index].Trim();

                        if (value.Length > 0)
                        {
                            return value;
                        }
                    }
                }

                return null;
            }

            var rsId = Get("hm_rsid", "rsid");
            var effect = Get("effect_allele");
            var other = Get("other_allele", "hm_inferotherallele");
            var weightText = Get("effect_weight");

            if (string.IsNullOrWhiteSpace(rsId) ||
                !rsId!.StartsWith("rs", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(effect) || effect!.Length != 1 ||
                string.IsNullOrWhiteSpace(other) || other!.Length != 1 ||
                !double.TryParse(weightText, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var weight))
            {
                // Single-nucleotide, rs-identified, weighted entries only. Indels and
                // unmapped rows are dropped rather than half-scored.
                return null;
            }

            double? frequency = null;

            if (double.TryParse(Get("allelefrequency_effect", "hm_effectalleefrequency"),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var f))
            {
                frequency = f;
            }

            return new ScoreVariant(
                rsId!.Trim().ToLowerInvariant(),
                char.ToUpperInvariant(effect[0]),
                char.ToUpperInvariant(other[0]),
                weight,
                frequency);
        }

        private static string Decompress(byte[] gzip)
        {
            using (var input = new MemoryStream(gzip))
            using (var gz = new GZipStream(input, CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                gz.CopyTo(output);
                return Encoding.UTF8.GetString(output.ToArray());
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
