using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using GenomeAnalysis.Annotations.Http;
using GenomeAnalysis.Core.Annotations;
using GenomeAnalysis.Core.Genome;
using Newtonsoft.Json.Linq;

namespace GenomeAnalysis.Annotations.Gwas
{
    /// <summary>
    /// Reads curated trait associations from the EBI GWAS Catalog.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The GWAS Catalog is manually curated from published literature, so an entry
    /// carries an effect size, a p-value and a PubMed identifier. That is what
    /// makes ranking by evidence possible instead of by whichever finding sounds
    /// most dramatic.
    /// </para>
    /// <para>
    /// One request per variant — there is no batch form — so this runs as part of
    /// the offline harvest, never during analysis.
    /// </para>
    /// </remarks>
    public sealed class GwasCatalogClient : IVariantAnnotationSource, IDisposable
    {
        public static readonly Uri DefaultBaseUri = new Uri("https://www.ebi.ac.uk/gwas/rest/api/");

        private readonly ThrottledHttpClient _http;
        private readonly Uri _baseUri;
        private bool _disposed;

        public GwasCatalogClient(
            ThrottleOptions? options = null,
            HttpClient? httpClient = null,
            Uri? baseUri = null)
        {
            _baseUri = baseUri ?? DefaultBaseUri;
            _http = new ThrottledHttpClient(options ?? ForGwas(), httpClient);
        }

        public static ThrottleOptions ForGwas() => new ThrottleOptions
        {
            MinimumInterval = TimeSpan.FromMilliseconds(250),
            RequestTimeout = TimeSpan.FromMinutes(2)
        };

        public string SourceName => "GWAS Catalog";

        public async Task<VariantAnnotation?> GetAsync(
            string rsId,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(rsId))
            {
                return null;
            }

            var normalized = rsId.Trim().ToLowerInvariant();

            var uri = new Uri(
                _baseUri,
                "singleNucleotidePolymorphisms/" + Uri.EscapeDataString(normalized) +
                "/associations?projection=associationBySnp");

            string json;

            try
            {
                json = await _http
                    .GetStringAsync(() => new HttpRequestMessage(HttpMethod.Get, uri), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
                // The catalog answers 404 for a variant it has never studied, which
                // is the common case and not an error worth propagating.
                return null;
            }

            var associations = ParseAssociations(json, normalized);

            return associations.Count == 0
                ? null
                : new VariantAnnotation(
                    normalized,
                    Strand.Unknown,
                    Strand.Unknown,
                    genotypes: null,
                    summary: null,
                    attribution: Attribution(normalized),
                    geneSymbol: null,
                    clinical: null,
                    minorAlleleFrequency: null,
                    knownAlleles: null,
                    mergedRsIds: null,
                    mostSevereConsequence: null,
                    traitAssociations: associations);
        }

        public async Task<IReadOnlyDictionary<string, VariantAnnotation>> GetManyAsync(
            IEnumerable<string> rsIds,
            CancellationToken cancellationToken = default)
        {
            var results = new Dictionary<string, VariantAnnotation>(StringComparer.OrdinalIgnoreCase);

            foreach (var rsId in rsIds ?? Enumerable.Empty<string>())
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

        /// <summary>
        /// Parses an associations response. Public so it can be exercised against
        /// recorded fixtures.
        /// </summary>
        public static IReadOnlyList<TraitAssociation> ParseAssociations(string json, string rsId)
        {
            var associations = new List<TraitAssociation>();

            if (string.IsNullOrWhiteSpace(json))
            {
                return associations;
            }

            JObject root;

            try
            {
                root = JObject.Parse(json);
            }
            catch (Newtonsoft.Json.JsonException)
            {
                return associations;
            }

            var entries = root.SelectToken("_embedded.associations") as JArray;

            if (entries == null)
            {
                return associations;
            }

            foreach (var entry in entries.OfType<JObject>())
            {
                foreach (var trait in ExtractTraits(entry))
                {
                    associations.Add(new TraitAssociation(
                        trait.Name,
                        entry["orPerCopyNum"]?.Value<double?>(),
                        entry["betaNum"]?.Value<double?>(),
                        entry["betaUnit"]?.Value<string>(),
                        entry["pvalue"]?.Value<double?>(),
                        ExtractRiskAllele(entry),
                        ExtractPubMedId(entry),
                        null,
                        AssociationAttribution(rsId, entry),
                        trait.Uri));
                }
            }

            // Strongest evidence first, so a caller that truncates keeps the part
            // that matters.
            return associations
                .OrderByDescending(a => a.IsGenomeWideSignificant)
                .ThenBy(a => a.PValue ?? double.MaxValue)
                .ToList();
        }

        private static IEnumerable<(string Name, string? Uri)> ExtractTraits(JObject entry)
        {
            var traits = entry["efoTraits"] as JArray;

            if (traits == null)
            {
                yield break;
            }

            foreach (var trait in traits.OfType<JObject>())
            {
                var name = trait["trait"]?.Value<string>();

                if (!string.IsNullOrWhiteSpace(name))
                {
                    yield return (name!.Trim(), trait["uri"]?.Value<string>());
                }
            }
        }

        private static string? ExtractRiskAllele(JObject entry)
        {
            var allele = entry.SelectToken("loci[0].strongestRiskAlleles[0].riskAlleleName")?.Value<string>();

            if (string.IsNullOrWhiteSpace(allele))
            {
                return null;
            }

            // Reported as "rs7903146-T"; only the allele part is useful.
            var dash = allele!.LastIndexOf('-');
            return dash >= 0 && dash < allele.Length - 1 ? allele.Substring(dash + 1) : allele;
        }

        /// <summary>
        /// PubMed identifiers are not available from this endpoint.
        /// </summary>
        /// <remarks>
        /// The <c>associationBySnp</c> projection embeds no study object, only a
        /// link to one — so a citation costs an extra request per association, and
        /// there are thousands. The GWAS Catalog's downloadable association TSV
        /// carries <c>PUBMEDID</c> for every row in one file, and that is the right
        /// way to fill this in. Until then the per-association record URL below
        /// provides the drill-down, and this returns null rather than pretending.
        /// </remarks>
        private static string? ExtractPubMedId(JObject entry) =>
            entry.SelectToken("study.publicationInfo.pubmedId")?.ToString();

        /// <summary>
        /// Attribution pointing at the individual curated association record rather
        /// than the variant page, so a displayed claim links to the exact row it
        /// came from.
        /// </summary>
        private static SourceAttribution AssociationAttribution(string rsId, JObject entry)
        {
            var self = entry.SelectToken("_links.self.href")?.Value<string>();

            if (string.IsNullOrWhiteSpace(self))
            {
                return Attribution(rsId);
            }

            return new SourceAttribution(
                "GWAS Catalog (EBI/NHGRI)",
                "EMBL-EBI terms of use; open data",
                "https://www.ebi.ac.uk/about/terms-of-use",
                self);
        }

        private static SourceAttribution Attribution(string rsId) =>
            new SourceAttribution(
                "GWAS Catalog (EBI/NHGRI)",
                "EMBL-EBI terms of use; open data",
                "https://www.ebi.ac.uk/about/terms-of-use",
                "https://www.ebi.ac.uk/gwas/variants/" + rsId);

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
