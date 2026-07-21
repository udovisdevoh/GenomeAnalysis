using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using GenomeAnalysis.Annotations.Http;
using GenomeAnalysis.Core.Annotations;
using GenomeAnalysis.Core.Genome;
using Newtonsoft.Json.Linq;

namespace GenomeAnalysis.Annotations.Ensembl
{
    /// <summary>
    /// Reads variant reference data from the Ensembl REST API.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ensembl supplies two things no other source here provides cleanly, and both
    /// are load-bearing:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <c>allele_string</c> — the variant's known alleles. Strand resolution cannot
    /// detect a palindromic variant without this, and refuses to map anything when
    /// it is missing.
    /// </description></item>
    /// <item><description>
    /// <c>synonyms</c> — old identifiers dbSNP has merged into the current rsID,
    /// which is what makes a 2013 file resolve at all.
    /// </description></item>
    /// </list>
    /// <para>
    /// No key required. The published allowance is generous (55 000 requests per
    /// hour), and the POST endpoint takes many identifiers at once.
    /// </para>
    /// </remarks>
    public sealed class EnsemblClient : IVariantAnnotationSource, IDisposable
    {
        public static readonly Uri DefaultEndpoint = new Uri("https://rest.ensembl.org/variation/homo_sapiens");

        /// <summary>
        /// Ensembl accepts 200 per POST, but the endpoint takes seconds per handful
        /// and a large batch is one long opaque wait. Smaller batches keep progress
        /// visible and a failure cheap to retry.
        /// </summary>
        public const int MaxBatchSize = 25;

        private static readonly Regex RsIdPattern = new Regex(@"^rs\d+$", RegexOptions.Compiled);

        private readonly ThrottledHttpClient _http;
        private readonly Uri _endpoint;
        private bool _disposed;

        public EnsemblClient(
            ThrottleOptions? options = null,
            HttpClient? httpClient = null,
            Uri? endpoint = null)
        {
            _endpoint = endpoint ?? DefaultEndpoint;
            _http = new ThrottledHttpClient(options ?? ThrottleOptions.ForEnsembl(), httpClient);
        }

        public string SourceName => "Ensembl";

        public async Task<VariantAnnotation?> GetAsync(
            string rsId,
            CancellationToken cancellationToken = default)
        {
            var results = await GetManyAsync(new[] { rsId }, cancellationToken).ConfigureAwait(false);
            return results.Values.FirstOrDefault();
        }

        public async Task<IReadOnlyDictionary<string, VariantAnnotation>> GetManyAsync(
            IEnumerable<string> rsIds,
            CancellationToken cancellationToken = default)
        {
            var results = new Dictionary<string, VariantAnnotation>(StringComparer.OrdinalIgnoreCase);

            var identifiers = (rsIds ?? Enumerable.Empty<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim().ToLowerInvariant())
                .Where(id => RsIdPattern.IsMatch(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var batch in Chunk(identifiers, MaxBatchSize))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var body = new JObject { ["ids"] = new JArray(batch) }.ToString(Newtonsoft.Json.Formatting.None);
                var json = await _http.PostJsonAsync(_endpoint, body, cancellationToken).ConfigureAwait(false);

                foreach (var pair in ParseBatchResponse(json))
                {
                    results[pair.Key] = pair.Value;
                }
            }

            return results;
        }

        /// <summary>
        /// Parses a batch response, which is a JSON object keyed by rsID. Public so
        /// it can be exercised against recorded fixtures.
        /// </summary>
        public static IEnumerable<KeyValuePair<string, VariantAnnotation>> ParseBatchResponse(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                yield break;
            }

            JObject root;

            try
            {
                root = JObject.Parse(json);
            }
            catch (Newtonsoft.Json.JsonException)
            {
                yield break;
            }

            foreach (var property in root.Properties())
            {
                if (!(property.Value is JObject entry))
                {
                    continue;
                }

                var annotation = MapVariant(property.Name, entry);

                if (annotation != null)
                {
                    yield return new KeyValuePair<string, VariantAnnotation>(annotation.RsId, annotation);
                }
            }
        }

        public static VariantAnnotation? MapVariant(string rsId, JObject entry)
        {
            var name = entry["name"]?.Value<string>() ?? rsId;

            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            // Take the GRCh38 mapping; that is the build Ensembl reports orientation
            // against, and mixing builds is how positions stop meaning anything.
            var mapping = (entry["mappings"] as JArray)
                ?.OfType<JObject>()
                .FirstOrDefault(m => string.Equals(
                    m["assembly_name"]?.Value<string>(), "GRCh38", StringComparison.OrdinalIgnoreCase));

            var alleles = ParseAlleleString(mapping?["allele_string"]?.Value<string>());

            return new VariantAnnotation(
                name,
                ParseStrand(mapping?["strand"]?.Value<int?>()),
                // Ensembl has no equivalent of SNPedia's stabilized orientation, and
                // inventing one would let a caller resolve a strand it cannot.
                Strand.Unknown,
                genotypes: null,
                summary: null,
                attribution: SourceAttribution.Ensembl(name),
                geneSymbol: null,
                clinical: null,
                minorAlleleFrequency: entry["MAF"]?.Value<double?>(),
                knownAlleles: alleles,
                mergedRsIds: ExtractMergedRsIds(entry["synonyms"] as JArray, name),
                mostSevereConsequence: entry["most_severe_consequence"]?.Value<string>());
        }

        /// <summary>
        /// Reads an <c>allele_string</c> such as <c>A/G</c> or <c>C/T/G</c>. Entries
        /// that are not single nucleotides — indels, <c>-</c>, longer sequences —
        /// are dropped, since this tool interprets substitutions only.
        /// </summary>
        public static IReadOnlyCollection<Nucleotide> ParseAlleleString(string? alleleString)
        {
            var alleles = new List<Nucleotide>();

            if (string.IsNullOrWhiteSpace(alleleString))
            {
                return alleles;
            }

            foreach (var part in alleleString!.Split('/'))
            {
                var token = part.Trim();

                if (token.Length == 1 && NucleotideExtensions.TryParse(token[0], out var nucleotide))
                {
                    alleles.Add(nucleotide);
                }
            }

            return alleles.Distinct().ToList();
        }

        /// <summary>
        /// Pulls old rsIDs out of Ensembl's synonym list, which also carries ClinVar
        /// (<c>RCV</c>/<c>VCV</c>) and UniProt (<c>VAR_</c>) identifiers.
        /// </summary>
        public static IReadOnlyList<string> ExtractMergedRsIds(JArray? synonyms, string currentRsId)
        {
            if (synonyms == null)
            {
                return new List<string>();
            }

            return synonyms
                .Select(s => s.Value<string>())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s!.Trim().ToLowerInvariant())
                .Where(s => RsIdPattern.IsMatch(s))
                .Where(s => !string.Equals(s, currentRsId, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static Strand ParseStrand(int? strand)
        {
            if (strand == 1)
            {
                return Strand.Plus;
            }

            return strand == -1 ? Strand.Minus : Strand.Unknown;
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
