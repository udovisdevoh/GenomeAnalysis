using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GenomeAnalysis.Annotations.Ensembl;
using GenomeAnalysis.Annotations.Local;
using GenomeAnalysis.Annotations.MyVariant;
using GenomeAnalysis.Core.Annotations;
using GenomeAnalysis.Core.Genome;
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
            var seedPath = args.Length > 0 ? args[0] : Path.Combine(repositoryRoot, "data", "seed-variants.json");
            var outputPath = args.Length > 1 ? args[1] : Path.Combine(repositoryRoot, "data", "variant-database.json");

            if (!File.Exists(seedPath))
            {
                Console.Error.WriteLine("Seed list not found: " + seedPath);
                return 1;
            }

            var seed = ReadSeedList(seedPath);
            Console.WriteLine("Seed list: " + seed.Count + " variants from " + seedPath);
            Console.WriteLine();

            var cancellation = new CancellationTokenSource();
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
                SourceAttribution.DbSnp()
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
