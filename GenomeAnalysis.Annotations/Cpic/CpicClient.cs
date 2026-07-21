using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using GenomeAnalysis.Annotations.Http;
using Newtonsoft.Json.Linq;

namespace GenomeAnalysis.Annotations.Cpic
{
    /// <summary>
    /// Reads pharmacogenomic knowledge from the CPIC API.
    /// </summary>
    /// <remarks>
    /// <para>
    /// CPIC publishes the two tables the star-allele case actually needs and that
    /// no per-variant source provides: which positions define each numbered allele
    /// (<c>*1</c>, <c>*2</c>…), and which metabolizer phenotype each diplotype
    /// implies. A genotype at a single position says nothing on its own here — the
    /// diplotype is the unit of meaning.
    /// </para>
    /// <para>
    /// The API is PostgREST, so filters use <c>column=eq.value</c> form. Guidelines
    /// are CC0; the recommendations are written for clinicians, and this tool
    /// surfaces the phenotype rather than restating dosing advice.
    /// </para>
    /// </remarks>
    public sealed class CpicClient : IDisposable
    {
        public static readonly Uri DefaultBaseUri = new Uri("https://api.cpicpgx.org/v1/");

        private readonly ThrottledHttpClient _http;
        private readonly Uri _baseUri;
        private bool _disposed;

        public CpicClient(ThrottleOptions? options = null, HttpClient? httpClient = null, Uri? baseUri = null)
        {
            _baseUri = baseUri ?? DefaultBaseUri;
            _http = new ThrottledHttpClient(options ?? ForCpic(), httpClient);
        }

        public static ThrottleOptions ForCpic() => new ThrottleOptions
        {
            MinimumInterval = TimeSpan.FromMilliseconds(300),
            RequestTimeout = TimeSpan.FromMinutes(2)
        };

        /// <summary>
        /// Genes CPIC rates level A: the evidence supports acting on them. Anything
        /// below that is not worth surfacing to a lay reader as actionable.
        /// </summary>
        public async Task<IReadOnlyList<string>> GetActionableGenesAsync(
            CancellationToken cancellationToken = default)
        {
            var json = await GetAsync("pair?cpiclevel=eq.A&select=genesymbol", cancellationToken)
                .ConfigureAwait(false);

            return ParseArray(json)
                .Select(o => o["genesymbol"]?.Value<string>())
                .Where(g => !string.IsNullOrWhiteSpace(g))
                .Select(g => g!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// The rsIDs CPIC uses to define a gene's alleles. These are exactly the
        /// positions a chip must cover for a diplotype call to be possible; any one
        /// of them missing makes the result indeterminate rather than approximate.
        /// </summary>
        public async Task<IReadOnlyList<string>> GetDefiningRsIdsAsync(
            string geneSymbol,
            CancellationToken cancellationToken = default)
        {
            var json = await GetAsync(
                    "sequence_location?genesymbol=eq." + Uri.EscapeDataString(geneSymbol) + "&select=dbsnpid",
                    cancellationToken)
                .ConfigureAwait(false);

            return ParseArray(json)
                .Select(o => o["dbsnpid"]?.Value<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!.Trim().ToLowerInvariant())
                .Where(id => id.StartsWith("rs", StringComparison.Ordinal))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Diplotype-to-phenotype rows for a gene: <c>*1/*17</c> maps to "Rapid
        /// Metabolizer" and so on.
        /// </summary>
        public async Task<IReadOnlyList<CpicDiplotype>> GetDiplotypesAsync(
            string geneSymbol,
            CancellationToken cancellationToken = default)
        {
            var json = await GetAsync(
                    "diplotype?genesymbol=eq." + Uri.EscapeDataString(geneSymbol) +
                    "&select=genesymbol,diplotype,generesult,function1,function2,totalactivityscore,description",
                    cancellationToken)
                .ConfigureAwait(false);

            return ParseArray(json)
                .Select(o => new CpicDiplotype(
                    o["genesymbol"]?.Value<string>() ?? geneSymbol,
                    o["diplotype"]?.Value<string>() ?? string.Empty,
                    o["generesult"]?.Value<string>(),
                    o["function1"]?.Value<string>(),
                    o["function2"]?.Value<string>(),
                    o["totalactivityscore"]?.Value<string>(),
                    o["description"]?.Value<string>()))
                .Where(d => !string.IsNullOrWhiteSpace(d.Diplotype))
                .ToList();
        }

        /// <summary>
        /// The star alleles of a gene, each with the alleles it carries at its
        /// defining positions, and the function CPIC assigns it.
        /// </summary>
        /// <remarks>
        /// This is what turns genotypes into a diplotype. A star allele is a
        /// haplotype: <c>*2</c> means specific alleles at specific positions, and a
        /// position not listed for an allele carries the reference base.
        /// </remarks>
        public async Task<IReadOnlyList<CpicStarAllele>> GetStarAllelesAsync(
            string geneSymbol,
            CancellationToken cancellationToken = default)
        {
            var definitionsJson = await GetAsync(
                    "allele_definition?genesymbol=eq." + Uri.EscapeDataString(geneSymbol) +
                    "&select=name,allele_location_value(variantallele,sequence_location(dbsnpid))",
                    cancellationToken)
                .ConfigureAwait(false);

            var functionsJson = await GetAsync(
                    "allele?genesymbol=eq." + Uri.EscapeDataString(geneSymbol) +
                    "&select=name,clinicalfunctionalstatus",
                    cancellationToken)
                .ConfigureAwait(false);

            var functions = ParseArray(functionsJson)
                .Where(o => !string.IsNullOrWhiteSpace(o["name"]?.Value<string>()))
                .GroupBy(o => o["name"]!.Value<string>()!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => g.First()["clinicalfunctionalstatus"]?.Value<string>(),
                    StringComparer.OrdinalIgnoreCase);

            var alleles = new List<CpicStarAllele>();

            foreach (var entry in ParseArray(definitionsJson))
            {
                var name = entry["name"]?.Value<string>();

                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var definitions = new Dictionary<string, char>(StringComparer.OrdinalIgnoreCase);

                foreach (var value in (entry["allele_location_value"] as JArray)?.OfType<JObject>()
                                      ?? Enumerable.Empty<JObject>())
                {
                    var rsId = value.SelectToken("sequence_location.dbsnpid")?.Value<string>();
                    var allele = value["variantallele"]?.Value<string>();

                    // Single-nucleotide definitions only: this tool does not
                    // interpret the insertions, deletions and gene conversions that
                    // define some star alleles, and a partial definition would
                    // produce a confident wrong call.
                    if (!string.IsNullOrWhiteSpace(rsId) &&
                        !string.IsNullOrWhiteSpace(allele) &&
                        allele!.Length == 1 &&
                        rsId!.StartsWith("rs", StringComparison.OrdinalIgnoreCase))
                    {
                        definitions[rsId.Trim().ToLowerInvariant()] = char.ToUpperInvariant(allele[0]);
                    }
                }

                if (definitions.Count == 0)
                {
                    continue;
                }

                functions.TryGetValue(name!, out var function);
                alleles.Add(new CpicStarAllele(name!, function, definitions));
            }

            return alleles;
        }

        private async Task<string> GetAsync(string relative, CancellationToken cancellationToken)
        {
            var uri = new Uri(_baseUri, relative);

            return await _http
                .GetStringAsync(() => new HttpRequestMessage(HttpMethod.Get, uri), cancellationToken)
                .ConfigureAwait(false);
        }

        private static IEnumerable<JObject> ParseArray(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return Enumerable.Empty<JObject>();
            }

            try
            {
                return JToken.Parse(json) is JArray array
                    ? array.OfType<JObject>().ToList()
                    : Enumerable.Empty<JObject>();
            }
            catch (Newtonsoft.Json.JsonException)
            {
                return Enumerable.Empty<JObject>();
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

    /// <summary>One star allele: what it carries where, and what it does.</summary>
    public sealed class CpicStarAllele
    {
        public CpicStarAllele(string name, string? function, IReadOnlyDictionary<string, char> definitions)
        {
            Name = name;
            Function = function;
            Definitions = definitions;
        }

        /// <summary>e.g. <c>*2</c>.</summary>
        public string Name { get; }

        /// <summary>e.g. "No function". Null when CPIC does not state one.</summary>
        public string? Function { get; }

        /// <summary>rsID to the allele this star allele carries there.</summary>
        public IReadOnlyDictionary<string, char> Definitions { get; }
    }

    /// <summary>One diplotype and the phenotype CPIC assigns it.</summary>
    public sealed class CpicDiplotype
    {
        public CpicDiplotype(
            string geneSymbol,
            string diplotype,
            string? phenotype,
            string? function1,
            string? function2,
            string? activityScore,
            string? description)
        {
            GeneSymbol = geneSymbol;
            Diplotype = diplotype;
            Phenotype = phenotype;
            Function1 = function1;
            Function2 = function2;
            ActivityScore = activityScore;
            Description = description;
        }

        public string GeneSymbol { get; }

        /// <summary>e.g. <c>*1/*17</c>.</summary>
        public string Diplotype { get; }

        /// <summary>e.g. "Rapid Metabolizer".</summary>
        public string? Phenotype { get; }

        public string? Function1 { get; }

        public string? Function2 { get; }

        public string? ActivityScore { get; }

        public string? Description { get; }
    }
}
