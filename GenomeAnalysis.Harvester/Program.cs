using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GenomeAnalysis.Annotations.Cpic;
using GenomeAnalysis.Annotations.Ensembl;
using GenomeAnalysis.Annotations.Gwas;
using GenomeAnalysis.Annotations.Local;
using GenomeAnalysis.Annotations.MyVariant;
using GenomeAnalysis.Annotations.Pgs;
using GenomeAnalysis.Core.Annotations;
using GenomeAnalysis.Core.Genome;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GenomeAnalysis.Harvester
{
    /// <summary>
    /// Builds the local variant database from public annotation sources.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Run offline of any user data. The input is a committed seed list of public
    /// variant identifiers; the output is a JSON file that the application then
    /// reads with no network access at all. That ordering is the whole point: it
    /// means no request the tool makes can be shaped by someone's genome.
    /// </para>
    /// <para>
    /// Usage: <c>GenomeAnalysis.Harvester [seed.json] [output.json]</c>
    /// </para>
    /// </remarks>
    public static class Program
    {
        public static int Main(string[] args)
        {
            try
            {
                return RunAsync(args).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                // Print the whole chain: the outer message on a failed HTTP call is
                // usually "A task was canceled", which says nothing about the cause.
                Console.Error.WriteLine("Harvest failed.");

                for (var current = ex; current != null; current = current.InnerException)
                {
                    Console.Error.WriteLine("  " + current.GetType().Name + ": " + current.Message);
                }

                Console.Error.WriteLine();
                Console.Error.WriteLine(ex.StackTrace);
                return 1;
            }
        }

        private static async Task<int> RunAsync(string[] args)
        {
            var repositoryRoot = FindRepositoryRoot();

            // --cpic-only rebuilds just the pharmacogenomics tables, which take
            // seconds, instead of re-querying every variant, which takes twenty
            // minutes. Useful whenever that file's shape changes.
            var cpicOnly = args.Any(a => string.Equals(a, "--cpic-only", StringComparison.OrdinalIgnoreCase));
            var pgsOnly = args.Any(a => string.Equals(a, "--pgs-only", StringComparison.OrdinalIgnoreCase));
            var positional = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();

            var seedPath = positional.Length > 0 ? positional[0] : Path.Combine(repositoryRoot, "data", "seed-variants.json");
            var outputPath = positional.Length > 1 ? positional[1] : Path.Combine(repositoryRoot, "data", "variant-database.json");

            if (!File.Exists(seedPath))
            {
                Console.Error.WriteLine("Seed list not found: " + seedPath);
                return 1;
            }

            var seed = ReadSeedList(seedPath).ToList();
            Console.WriteLine("Seed list: " + seed.Count + " variants from " + seedPath);

            var cancellationSource = new CancellationTokenSource();

            // Expand the seed with every position CPIC uses to define a star allele
            // in an actionable gene. These are the positions a chip has to cover for
            // a diplotype call to be possible at all, so they belong in the database
            // whether or not anyone hand-picked them.
            Console.WriteLine();
            Console.WriteLine("Expanding seed from CPIC (level A pharmacogenes)...");

            var pharmacogenomics = new List<GenePharmacogenomics>();

            using (var cpic = new CpicClient())
            {
                var genes = await cpic.GetActionableGenesAsync(cancellationSource.Token).ConfigureAwait(false);
                Console.WriteLine("  " + genes.Count + " genes: " + string.Join(", ", genes));

                foreach (var gene in genes)
                {
                    var rsIds = await cpic.GetDefiningRsIdsAsync(gene, cancellationSource.Token).ConfigureAwait(false);
                    var diplotypes = await cpic.GetDiplotypesAsync(gene, cancellationSource.Token).ConfigureAwait(false);
                    var starAlleles = await cpic.GetStarAllelesAsync(gene, cancellationSource.Token).ConfigureAwait(false);

                    pharmacogenomics.Add(new GenePharmacogenomics(gene, rsIds, diplotypes, starAlleles));
                    seed.AddRange(rsIds);

                    Console.WriteLine("  " + gene.PadRight(10) +
                                      rsIds.Count.ToString().PadLeft(4) + " positions, " +
                                      starAlleles.Count.ToString().PadLeft(4) + " star alleles, " +
                                      diplotypes.Count.ToString().PadLeft(5) + " diplotypes");
                }
            }

            var pharmacogenomicsPath = Path.Combine(
                Path.GetDirectoryName(outputPath) ?? ".", "pharmacogenomics.json");

            SavePharmacogenomics(pharmacogenomicsPath, pharmacogenomics);

            // Published polygenic scores from the PGS Catalog. A short curated list
            // of small, classic scores: the point is a correct, honest computation
            // and its coverage caveats, not breadth.
            var pgsIds = new[] { "PGS000001", "PGS000778" };
            var scoresPath = Path.Combine(Path.GetDirectoryName(outputPath) ?? ".", "polygenic-scores.json");

            Console.WriteLine();
            Console.WriteLine("Fetching polygenic scores from the PGS Catalog...");

            var scores = new List<GenomeAnalysis.Core.Rules.PolygenicScore>();

            using (var pgs = new PgsCatalogClient())
            {
                foreach (var pgsId in pgsIds)
                {
                    try
                    {
                        var score = await pgs.GetScoreAsync(pgsId, cancellationSource.Token).ConfigureAwait(false);

                        if (score != null)
                        {
                            scores.Add(score);
                            seed.AddRange(score.Variants.Select(v => v.RsId));
                            Console.WriteLine("  " + pgsId.PadRight(12) + score.Variants.Count.ToString().PadLeft(5) +
                                              " variants   " + score.Trait);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("  " + pgsId + " skipped: " + ex.Message);
                    }
                }
            }

            SavePolygenicScores(scoresPath, scores);

            if (pgsOnly)
            {
                Console.WriteLine();
                Console.WriteLine("--pgs-only: skipping the rest.");
                return 0;
            }

            if (cpicOnly)
            {
                Console.WriteLine();
                Console.WriteLine("--cpic-only: skipping variant harvest.");
                return 0;
            }

            seed = seed.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            Console.WriteLine();
            Console.WriteLine("Harvesting " + seed.Count + " variants.");
            Console.WriteLine();

            var cancellation = cancellationSource;
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cancellation.Cancel();
                Console.WriteLine("Cancelling after the current batch...");
            };

            var byRsId = new Dictionary<string, List<VariantAnnotation>>(StringComparer.OrdinalIgnoreCase);

            // Ensembl first: it supplies the allele set and strand, without which
            // strand resolution refuses to map anything at all.
            Console.WriteLine("Querying Ensembl (alleles, strand, merged rsIDs)...");

            using (var ensembl = new EnsemblClient())
            {
                var results = await ensembl.GetManyAsync(seed, cancellation.Token).ConfigureAwait(false);
                Collect(byRsId, results.Values);
                Console.WriteLine("  " + results.Count + " of " + seed.Count + " resolved.");
            }

            Console.WriteLine("Querying MyVariant.info (ClinVar significance, gnomAD frequency)...");

            using (var myVariant = new MyVariantClient())
            {
                var results = await myVariant.GetManyAsync(seed, cancellation.Token).ConfigureAwait(false);
                Collect(byRsId, results.Values);
                Console.WriteLine("  " + results.Count + " of " + seed.Count + " resolved.");
            }

            Console.WriteLine("Querying GWAS Catalog (trait associations, effect sizes, citations)...");

            using (var gwas = new GwasCatalogClient())
            {
                var found = 0;
                var processed = 0;

                foreach (var rsId in seed)
                {
                    if (cancellation.Token.IsCancellationRequested)
                    {
                        break;
                    }

                    var annotation = await gwas.GetAsync(rsId, cancellation.Token).ConfigureAwait(false);

                    if (annotation != null)
                    {
                        Collect(byRsId, new[] { annotation });
                        found++;
                    }

                    if (++processed % 50 == 0)
                    {
                        Console.WriteLine("  " + processed + " / " + seed.Count + " checked, " + found + " with associations");
                    }
                }

                Console.WriteLine("  " + found + " variants carry published associations.");
            }

            Console.WriteLine();

            var merged = byRsId.Values
                .Select(AnnotationMerge.Combine)
                .Where(a => a != null)
                .Select(a => a!)
                .ToList();

            var sources = new List<SourceAttribution>
            {
                SourceAttribution.Ensembl(),
                SourceAttribution.ClinVar(),
                SourceAttribution.GnomAd(),
                SourceAttribution.DbSnp(),
                new SourceAttribution("GWAS Catalog (EBI/NHGRI)", "EMBL-EBI terms of use; open data",
                    "https://www.ebi.ac.uk/about/terms-of-use", "https://www.ebi.ac.uk/gwas/"),
                new SourceAttribution("CPIC", "CC0", "https://creativecommons.org/publicdomain/zero/1.0/",
                    "https://cpicpgx.org/")
            };

            VariantDatabase.Save(outputPath, merged, sources);

            Report(merged, seed, outputPath);
            return 0;
        }

        private static void Collect(
            Dictionary<string, List<VariantAnnotation>> accumulator,
            IEnumerable<VariantAnnotation> annotations)
        {
            foreach (var annotation in annotations)
            {
                if (!accumulator.TryGetValue(annotation.RsId, out var list))
                {
                    list = new List<VariantAnnotation>();
                    accumulator[annotation.RsId] = list;
                }

                list.Add(annotation);
            }
        }

        private static void Report(
            IReadOnlyList<VariantAnnotation> merged,
            IReadOnlyList<string> seed,
            string outputPath)
        {
            Console.WriteLine("Wrote " + merged.Count + " variants to " + outputPath);
            Console.WriteLine();

            var missing = seed
                .Where(rs => !merged.Any(m =>
                    string.Equals(m.RsId, rs, StringComparison.OrdinalIgnoreCase) ||
                    m.MergedRsIds.Contains(rs, StringComparer.OrdinalIgnoreCase)))
                .ToList();

            var withAlleles = merged.Count(m => m.KnownAlleles.Count > 0);
            var palindromic = merged.Where(m => m.IsPalindromic).ToList();
            var withClinical = merged.Where(m => m.Clinical != null).ToList();
            var withFrequency = merged.Count(m => m.MinorAlleleFrequency.HasValue);
            var withMerges = merged.Where(m => m.MergedRsIds.Count > 0).ToList();

            Console.WriteLine("Coverage");
            Console.WriteLine("  allele set known : " + withAlleles + " / " + merged.Count +
                              "   (strand resolution needs this)");
            Console.WriteLine("  clinical record  : " + withClinical.Count);
            Console.WriteLine("  frequency known  : " + withFrequency);
            Console.WriteLine("  carries merged   : " + withMerges.Count);

            if (missing.Count > 0)
            {
                Console.WriteLine("  unresolved       : " + string.Join(", ", missing));
            }

            Console.WriteLine();
            Console.WriteLine("Palindromic variants (A/T or C/G) — homozygous calls on these can never");
            Console.WriteLine("be strand-resolved and must be reported as indeterminate:");

            if (palindromic.Count == 0)
            {
                Console.WriteLine("  none in this seed list");
            }
            else
            {
                foreach (var variant in palindromic.OrderBy(v => v.RsId, StringComparer.OrdinalIgnoreCase))
                {
                    Console.WriteLine("  " + variant.RsId.PadRight(14) +
                                      string.Join("/", variant.KnownAlleles.Select(a => a.ToChar())) +
                                      (variant.GeneSymbol == null ? "" : "   " + variant.GeneSymbol));
                }
            }

            Console.WriteLine();
            Console.WriteLine("Highest-review clinical findings:");

            foreach (var variant in withClinical
                         .OrderByDescending(v => v.Clinical!.ReviewStatus.ToStarRating())
                         .ThenBy(v => v.RsId, StringComparer.OrdinalIgnoreCase)
                         .Take(12))
            {
                var clinical = variant.Clinical!;
                Console.WriteLine("  " + variant.RsId.PadRight(14) +
                                  new string('*', clinical.ReviewStatus.ToStarRating()).PadRight(5) +
                                  clinical.Significance.ToString().PadRight(28) +
                                  (clinical.Conditions.FirstOrDefault() ?? ""));
            }
        }

        /// <summary>
        /// Everything CPIC knows about one pharmacogene: the positions that define
        /// its alleles, and what each diplotype implies.
        /// </summary>
        private sealed class GenePharmacogenomics
        {
            public GenePharmacogenomics(
                string gene,
                IReadOnlyList<string> definingRsIds,
                IReadOnlyList<CpicDiplotype> diplotypes,
                IReadOnlyList<CpicStarAllele> starAlleles)
            {
                Gene = gene;
                DefiningRsIds = definingRsIds;
                Diplotypes = diplotypes;
                StarAlleles = starAlleles;
            }

            public IReadOnlyList<CpicStarAllele> StarAlleles { get; }

            public string Gene { get; }

            public IReadOnlyList<string> DefiningRsIds { get; }

            public IReadOnlyList<CpicDiplotype> Diplotypes { get; }
        }

        /// <summary>
        /// Recovers each allele's function from the diplotype table: in a row for
        /// <c>*1/*17</c>, <c>function1</c> describes <c>*1</c> and <c>function2</c>
        /// describes <c>*17</c>.
        /// </summary>
        private static IReadOnlyDictionary<string, string> ExtractAlleleFunctions(
            IReadOnlyList<CpicDiplotype> diplotypes)
        {
            var functions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var diplotype in diplotypes)
            {
                var parts = diplotype.Diplotype.Split('/');

                if (parts.Length != 2)
                {
                    continue;
                }

                Record(parts[0].Trim(), diplotype.Function1);
                Record(parts[1].Trim(), diplotype.Function2);
            }

            return functions;

            void Record(string allele, string? function)
            {
                if (string.IsNullOrWhiteSpace(allele) || string.IsNullOrWhiteSpace(function))
                {
                    return;
                }

                if (!functions.ContainsKey(allele))
                {
                    functions[allele] = function!;
                }
            }
        }

        /// <summary>
        /// The distinct function-pair to phenotype rules. Pairs are order-normalised,
        /// since a diplotype is unordered — there is no phase information that would
        /// make <c>*1/*4</c> differ from <c>*4/*1</c>.
        /// </summary>
        private static IReadOnlyList<(string First, string Second, string Phenotype)> ExtractPhenotypeRules(
            IReadOnlyList<CpicDiplotype> diplotypes)
        {
            var rules = new Dictionary<string, (string, string, string)>(StringComparer.OrdinalIgnoreCase);

            foreach (var diplotype in diplotypes)
            {
                if (string.IsNullOrWhiteSpace(diplotype.Function1) ||
                    string.IsNullOrWhiteSpace(diplotype.Function2) ||
                    string.IsNullOrWhiteSpace(diplotype.Phenotype))
                {
                    continue;
                }

                var first = diplotype.Function1!;
                var second = diplotype.Function2!;

                if (string.Compare(first, second, StringComparison.OrdinalIgnoreCase) > 0)
                {
                    var swap = first;
                    first = second;
                    second = swap;
                }

                var key = first + "|" + second + "|" + diplotype.Phenotype;

                if (!rules.ContainsKey(key))
                {
                    rules[key] = (first, second, diplotype.Phenotype!);
                }
            }

            return rules.Values
                .OrderBy(r => r.Item1, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.Item2, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static void SavePolygenicScores(
            string path,
            IReadOnlyList<GenomeAnalysis.Core.Rules.PolygenicScore> scores)
        {
            var scoreArray = new JArray();

            foreach (var score in scores)
            {
                scoreArray.Add(new JObject
                {
                    ["id"] = score.Id,
                    ["name"] = score.Name,
                    ["trait"] = score.Trait,
                    ["ancestry"] = score.Ancestry,
                    ["citation"] = score.Citation,
                    ["genomeBuild"] = score.GenomeBuild,
                    ["variantCount"] = score.Variants.Count,
                    ["variants"] = new JArray(score.Variants.Select(v =>
                    {
                        var o = new JObject
                        {
                            ["rsId"] = v.RsId,
                            ["effect"] = v.EffectAllele.ToString(),
                            ["other"] = v.OtherAllele.ToString(),
                            ["weight"] = v.Weight
                        };

                        if (v.EffectAlleleFrequency.HasValue)
                        {
                            o["freq"] = v.EffectAlleleFrequency.Value;
                        }

                        return o;
                    }))
                });
            }

            var root = new JObject
            {
                ["schemaVersion"] = 1,
                ["generatedAt"] = DateTimeOffset.UtcNow.ToString("O"),
                ["notice"] =
                    "Published polygenic scores from the PGS Catalog (EBI/NHGRI, CC BY 4.0). Each score is a " +
                    "pre-specified, cited variant list with per-allele weights, harmonized to GRCh37. The tool " +
                    "computes the published score over the variants a file covers and reports that coverage; it " +
                    "never composes a score from odds ratios, and withholds any percentile it cannot justify.",
                ["source"] = new JObject
                {
                    ["name"] = "PGS Catalog",
                    ["licence"] = "CC BY 4.0",
                    ["url"] = "https://www.pgscatalog.org/"
                },
                ["scoreCount"] = scoreArray.Count,
                ["scores"] = scoreArray
            };

            File.WriteAllText(path, root.ToString(Formatting.Indented), new System.Text.UTF8Encoding(false));
            Console.WriteLine("Wrote " + scoreArray.Count + " polygenic scores to " + path);
        }

        private static void SavePharmacogenomics(string path, IReadOnlyList<GenePharmacogenomics> genes)
        {
            var geneObject = new JObject();

            foreach (var gene in genes.OrderBy(g => g.Gene, StringComparer.OrdinalIgnoreCase))
            {
                // Store the rule, not its expansion.
                //
                // CPIC publishes every diplotype explicitly, which is the Cartesian
                // product of a gene's alleles: RYR1 alone yields 60 378 rows. But
                // those rows encode only two things — what function each allele has,
                // and what phenotype a pair of functions produces. Keeping those two
                // tables reconstructs the whole set from 6 rules instead of 60 378
                // rows, and it is the form the engine can actually reason with.
                var alleleFunctions = new JObject();

                foreach (var pair in ExtractAlleleFunctions(gene.Diplotypes)
                             .OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
                {
                    alleleFunctions[pair.Key] = pair.Value;
                }

                geneObject[gene.Gene] = new JObject
                {
                    ["definingRsIds"] = new JArray(gene.DefiningRsIds),
                    ["definingPositionCount"] = gene.DefiningRsIds.Count,
                    ["alleleFunctions"] = alleleFunctions,
                    ["starAlleles"] = new JArray(gene.StarAlleles.Select(a =>
                    {
                        var definitions = new JObject();

                        foreach (var pair in a.Definitions.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
                        {
                            definitions[pair.Key] = pair.Value.ToString();
                        }

                        return new JObject
                        {
                            ["name"] = a.Name,
                            ["function"] = a.Function,
                            ["definitions"] = definitions
                        };
                    })),
                    ["phenotypeRules"] = new JArray(ExtractPhenotypeRules(gene.Diplotypes)
                        .Select(r => new JObject
                        {
                            ["function1"] = r.First,
                            ["function2"] = r.Second,
                            ["phenotype"] = r.Phenotype
                        })),
                    ["diplotypeCount"] = gene.Diplotypes.Count
                };
            }

            var root = new JObject
            {
                ["schemaVersion"] = 1,
                ["generatedAt"] = DateTimeOffset.UtcNow.ToString("O"),
                ["notice"] =
                    "Pharmacogenomic reference tables from CPIC (CC0), for genes CPIC rates " +
                    "level A. Stored as allele functions plus function-pair rules rather than " +
                    "the full diplotype expansion, which is combinatorial and derivable. " +
                    "A diplotype call requires every defining position listed here; any missing " +
                    "position makes the result indeterminate, never approximate. Phenotypes are " +
                    "surfaced as such — this tool does not restate CPIC's dosing recommendations.",
                ["source"] = new JObject
                {
                    ["name"] = "CPIC",
                    ["licence"] = "CC0",
                    ["url"] = "https://cpicpgx.org/"
                },
                ["geneCount"] = geneObject.Count,
                ["genes"] = geneObject
            };

            File.WriteAllText(path, root.ToString(Formatting.Indented), new System.Text.UTF8Encoding(false));
            Console.WriteLine("Wrote " + geneObject.Count + " pharmacogenes to " + path);
        }

        private static IReadOnlyList<string> ReadSeedList(string path)
        {
            var root = JObject.Parse(File.ReadAllText(path));

            return (root["groups"] as JArray)
                ?.OfType<JObject>()
                .SelectMany(g => (g["rsIds"] as JArray)?.Select(t => t.Value<string>()) ?? Enumerable.Empty<string>())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s!.Trim().ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>();
        }

        private static string FindRepositoryRoot()
        {
            var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);

            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "GenomeAnalysis.sln")))
            {
                directory = directory.Parent;
            }

            return directory?.FullName ?? Directory.GetCurrentDirectory();
        }
    }
}
