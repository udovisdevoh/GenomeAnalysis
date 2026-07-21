using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GenomeAnalysis.Annotations.Local;
using GenomeAnalysis.Core.Analysis;
using GenomeAnalysis.Core.Annotations;
using GenomeAnalysis.Core.Genome;
using GenomeAnalysis.Core.Parsing;

namespace GenomeAnalysis.Cli
{
    /// <summary>
    /// Reads a raw DNA file and reports what the local variant database knows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Runs entirely offline: the genome file is read from disk and matched against
    /// the committed variant database. No network call is made, so nothing about
    /// the file — not even the pattern of which variants it contains — can leave
    /// the machine.
    /// </para>
    /// <para>
    /// Output is French, per the project convention that the domain speaks English
    /// in code and the user interface speaks French.
    /// </para>
    /// </remarks>
    public static class Program
    {
        public static int Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;

            if (args.Length == 0)
            {
                Console.WriteLine("Usage : GenomeAnalysis.Cli <fichier-genome.txt> [base-variants.json]");
                Console.WriteLine();
                Console.WriteLine("Analyse un fichier ADN brut (23andMe, AncestryDNA, MyHeritage) hors ligne,");
                Console.WriteLine("sans aucun appel réseau.");
                return 1;
            }

            var genomePath = args[0];

            if (!File.Exists(genomePath))
            {
                Console.Error.WriteLine("Fichier introuvable : " + genomePath);
                return 1;
            }

            var databasePath = args.Length > 1
                ? args[1]
                : Path.Combine(FindRepositoryRoot(), "data", "variant-database.json");

            if (!File.Exists(databasePath))
            {
                Console.Error.WriteLine("Base de variants introuvable : " + databasePath);
                Console.Error.WriteLine("Lancez GenomeAnalysis.Harvester pour la générer.");
                return 1;
            }

            var database = VariantDatabase.Load(databasePath);

            if (database.Count == 0)
            {
                // Distinguish the causes: "empty or unreadable" leaves the user with
                // nothing to act on, and a schema bump is the likeliest reason.
                Console.Error.WriteLine("La base de variants n'a pu être chargée : " + databasePath);
                Console.Error.WriteLine();
                Console.Error.WriteLine("Cause probable : le fichier a été écrit par une version antérieure du");
                Console.Error.WriteLine("format (schéma attendu : v" +
                                        GenomeAnalysis.Annotations.Cache.AnnotationSerializer.SchemaVersion + ").");
                Console.Error.WriteLine("Les enregistrements d'un schéma incompatible sont ignorés plutôt que");
                Console.Error.WriteLine("relus de travers.");
                Console.Error.WriteLine();
                Console.Error.WriteLine("Régénérez-la : GenomeAnalysis.Harvester.exe");
                return 1;
            }

            using (var reader = GenomeFileReader.Open(genomePath))
            {
                PrintFileSection(reader.Header, genomePath, database);

                var analyzer = new GenomeAnalyzer(database);
                var findings = GenomeAnalyzer.Prioritise(analyzer.Analyze(reader.ReadCalls()));
                var summary = new AnalysisSummary(findings);

                PrintReadingSection(reader.Statistics, database);
                PrintFindings(findings);
                PrintIndeterminate(findings);
                PrintSummary(summary);
                PrintDisclaimer();
            }

            return 0;
        }

        private static void PrintFileSection(GenomeFileHeader header, string path, VariantDatabase database)
        {
            Console.WriteLine();
            Console.WriteLine("═══ FICHIER ═══");
            Console.WriteLine();
            Console.WriteLine("  Chemin       : " + Path.GetFileName(path));
            Console.WriteLine("  Fournisseur  : " + ProviderName(header.Provider));
            Console.WriteLine("  Build        : " + BuildName(header.Build));
            Console.WriteLine("  Base locale  : " + database.Count + " variants, " +
                              "générée le " + (database.GeneratedAt?.ToString("yyyy-MM-dd") ?? "?"));

            if (header.Warnings.Count > 0)
            {
                Console.WriteLine();

                foreach (var warning in header.Warnings)
                {
                    Console.WriteLine("  ⚠ " + Wrap(warning, 4));
                }
            }
        }

        private static void PrintReadingSection(ParseStatistics statistics, VariantDatabase database)
        {
            Console.WriteLine();
            Console.WriteLine("═══ LECTURE ═══");
            Console.WriteLine();
            Console.WriteLine("  Marqueurs lus            : " + statistics.TotalRows);
            Console.WriteLine("  Génotypes exploitables   : " + statistics.CalledGenotypes);
            Console.WriteLine("  Absences de lecture      : " + statistics.NoCalls);
            Console.WriteLine("  Appels hémizygotes       : " + statistics.HemizygousCalls + "  (X, Y, MT)");
            Console.WriteLine("  Identifiants internes    : " + statistics.ProviderInternalIds +
                              "  (sans équivalent public)");

            if (statistics.MalformedRows > 0)
            {
                Console.WriteLine("  Lignes illisibles        : " + statistics.MalformedRows);
            }
        }

        private static void PrintFindings(IReadOnlyList<Finding> findings)
        {
            var determinate = findings
                .Where(f => f.Status == FindingStatus.Determinate && f.PriorityScore > 0)
                .ToList();

            Console.WriteLine();
            Console.WriteLine("═══ RÉSULTATS ═══");

            if (determinate.Count == 0)
            {
                Console.WriteLine();
                Console.WriteLine("  Aucune des positions testées et couvertes par la base locale ne porte");
                Console.WriteLine("  de variant documenté. Cela ne signifie pas une absence de risque : voir");
                Console.WriteLine("  l'avertissement en fin de rapport.");
            }
            else
            {
                foreach (var finding in determinate)
                {
                    PrintFinding(finding);
                }
            }

            PrintReferenceGenotypes(findings);
        }

        /// <summary>
        /// Positions that were tested and carry the ordinary allele.
        /// </summary>
        /// <remarks>
        /// Listed deliberately: "this position was examined and is unremarkable" is
        /// a different statement from "this position was never looked at", and the
        /// reader cannot distinguish them otherwise.
        /// </remarks>
        private static void PrintReferenceGenotypes(IReadOnlyList<Finding> findings)
        {
            var reference = findings
                .Where(f => f.Status == FindingStatus.Determinate && f.CarriesVariant == false)
                .ToList();

            if (reference.Count == 0)
            {
                return;
            }

            Console.WriteLine();
            Console.WriteLine("  ── Positions testées ne portant pas le variant ──");
            Console.WriteLine();
            Console.WriteLine("  Ces marqueurs ont été lus et correspondent à l'allèle de référence.");
            Console.WriteLine("  La classification clinique du variant ne s'applique donc pas ici.");
            Console.WriteLine();

            foreach (var finding in reference.Take(20))
            {
                var label = finding.Call.MarkerId;
                var gene = finding.Annotation?.GeneSymbol;
                var significance = finding.ClinicalForVariant;

                Console.WriteLine("    " + label.PadRight(14) +
                                  (gene ?? string.Empty).PadRight(12) +
                                  "génotype " + finding.StrandMatch!.ResolvedGenotype!.Value +
                                  (significance == null
                                      ? string.Empty
                                      : "   (variant classé " + SignificanceName(significance.Significance) + ")"));
            }

            if (reference.Count > 20)
            {
                Console.WriteLine("    ... et " + (reference.Count - 20) + " autres.");
            }
        }

        private static void PrintFinding(Finding finding)
        {
            var annotation = finding.Annotation!;
            var genotype = finding.StrandMatch!.ResolvedGenotype!.Value;

            Console.WriteLine();
            Console.WriteLine("  ──────────────────────────────────────────────────────────────");
            Console.Write("  " + finding.Call.MarkerId);

            if (!string.IsNullOrWhiteSpace(annotation.GeneSymbol))
            {
                Console.Write("  [" + annotation.GeneSymbol + "]");
            }

            Console.WriteLine("  génotype " + genotype);

            if (finding.StrandMatch.WasComplemented)
            {
                Console.WriteLine("    ↻ allèles complémentés (la source lit le brin opposé)");
            }

            if (finding.CarriesVariant == null)
            {
                Console.WriteLine("    ⚠ Allèle de référence inconnu pour ce marqueur : impossible de");
                Console.WriteLine("      déterminer si ce génotype porte le variant. Les informations");
                Console.WriteLine("      ci-dessous décrivent le variant, pas nécessairement ce résultat.");
            }

            var clinical = finding.Clinical;

            if (clinical != null)
            {
                Console.WriteLine("    Classification : " + SignificanceName(clinical.Significance) +
                                  "   " + Stars(clinical.ReviewStatus.ToStarRating()) +
                                  " (" + clinical.ReviewStatus.ToStarRating() + "/4 — niveau de revue ClinVar)");

                if (clinical.Conditions.Count > 0)
                {
                    Console.WriteLine("    Associé à      : " +
                                      string.Join(", ", clinical.Conditions.Take(3)) +
                                      (clinical.Conditions.Count > 3
                                          ? " (+" + (clinical.Conditions.Count - 3) + " autres)"
                                          : string.Empty));
                }
            }

            if (annotation.MinorAlleleFrequency.HasValue)
            {
                var percent = annotation.MinorAlleleFrequency.Value * 100;
                Console.WriteLine("    Fréquence      : " + percent.ToString("0.##") + " % en population générale" +
                                  (annotation.IsCommon ? "  (variant commun)" : string.Empty));
            }

            var traits = annotation.TraitAssociations
                .Where(t => t.IsGenomeWideSignificant && !t.IsNegligibleEffect)
                .Take(3)
                .ToList();

            foreach (var trait in traits)
            {
                Console.Write("    Association    : " + trait.Trait);

                if (trait.OddsRatio.HasValue)
                {
                    Console.Write("  (OR " + trait.OddsRatio.Value.ToString("0.##"));

                    if (trait.RiskAllele != null)
                    {
                        Console.Write(" par copie de " + trait.RiskAllele);
                    }

                    Console.Write(")");
                }

                Console.WriteLine();
            }

            if (finding.RequiresConfirmatoryTesting)
            {
                Console.WriteLine();
                Console.WriteLine("    ⚑ RÉSULTAT À FORT IMPACT");
                Console.WriteLine("      Les données de puce brutes ne sont pas de qualité clinique : une part");
                Console.WriteLine("      importante des variants ainsi rapportés se révèle être des faux");
                Console.WriteLine("      positifs en laboratoire accrédité. Ce résultat exige une confirmation");
                Console.WriteLine("      par test clinique et un conseil génétique professionnel avant toute");
                Console.WriteLine("      interprétation.");
            }

            Console.WriteLine("    Source         : " + annotation.Attribution.SourceName +
                              (annotation.Attribution.RecordUrl == null
                                  ? string.Empty
                                  : "  " + annotation.Attribution.RecordUrl));
        }

        private static void PrintIndeterminate(IReadOnlyList<Finding> findings)
        {
            var indeterminate = findings.Where(f => f.Status == FindingStatus.Indeterminate).ToList();

            if (indeterminate.Count == 0)
            {
                return;
            }

            Console.WriteLine();
            Console.WriteLine("═══ INDÉTERMINÉS ═══");
            Console.WriteLine();
            Console.WriteLine("  Ces marqueurs ont été lus mais ne peuvent pas être interprétés. C'est une");
            Console.WriteLine("  information utile, pas un échec : deviner sur ces positions pourrait inverser");
            Console.WriteLine("  le sens d'une association.");
            Console.WriteLine();

            foreach (var finding in indeterminate.Take(15))
            {
                Console.WriteLine("  " + finding.Call.MarkerId.PadRight(14) + Wrap(finding.Reason, 16));
            }

            if (indeterminate.Count > 15)
            {
                Console.WriteLine("  ... et " + (indeterminate.Count - 15) + " autres.");
            }
        }

        private static void PrintSummary(AnalysisSummary summary)
        {
            Console.WriteLine();
            Console.WriteLine("═══ BILAN ═══");
            Console.WriteLine();
            Console.WriteLine("  Interprétables           : " + summary.Determinate);
            Console.WriteLine("  Indéterminés             : " + summary.Indeterminate);
            Console.WriteLine("  Hors périmètre           : " + summary.NotApplicable);
            Console.WriteLine("  Dont flips ambigus       : " + summary.AmbiguousFlips +
                              "  (SNP palindromiques A/T ou C/G)");
            Console.WriteLine("  Brins complémentés       : " + summary.Complemented);
            Console.WriteLine("  Avec dossier clinique    : " + summary.WithClinicalRecord);
            Console.WriteLine("  Exigeant confirmation    : " + summary.RequiringConfirmation);
        }

        private static void PrintDisclaimer()
        {
            Console.WriteLine();
            Console.WriteLine("═══ AVERTISSEMENT ═══");
            Console.WriteLine();
            Console.WriteLine("  Information éducative. Ceci n'est pas un diagnostic et ne remplace pas");
            Console.WriteLine("  l'avis d'un professionnel de santé.");
            Console.WriteLine();
            Console.WriteLine("  Une puce ADN n'interroge qu'un ensemble fixe de positions prédéfinies.");
            Console.WriteLine("  L'absence de résultat signifie qu'aucune des positions testées ne porte");
            Console.WriteLine("  de variant connu — jamais qu'un risque est écarté. La grande majorité");
            Console.WriteLine("  des variants pathogènes connus ne figurent sur aucune puce grand public.");
            Console.WriteLine();
            Console.WriteLine("  Les associations sont statistiques et populationnelles. Un odds ratio");
            Console.WriteLine("  décrit une différence de fréquence entre groupes, pas une probabilité");
            Console.WriteLine("  individuelle, et ces valeurs ne se multiplient pas entre elles.");
            Console.WriteLine();
        }

        private static string ProviderName(GenomeFileProvider provider)
        {
            switch (provider)
            {
                case GenomeFileProvider.TwentyThreeAndMe: return "23andMe";
                case GenomeFileProvider.AncestryDna: return "AncestryDNA";
                case GenomeFileProvider.MyHeritage: return "MyHeritage";
                case GenomeFileProvider.FamilyTreeDna: return "Family Tree DNA";
                default: return "non reconnu";
            }
        }

        private static string BuildName(GenomeBuild build)
        {
            switch (build)
            {
                case GenomeBuild.GRCh37: return "GRCh37 (build 37)";
                case GenomeBuild.GRCh38: return "GRCh38 (build 38)";
                default: return "non déclaré";
            }
        }

        private static string SignificanceName(ClinicalSignificance significance)
        {
            switch (significance)
            {
                case ClinicalSignificance.Pathogenic: return "pathogène";
                case ClinicalSignificance.LikelyPathogenic: return "probablement pathogène";
                case ClinicalSignificance.UncertainSignificance: return "signification incertaine (VUS)";
                case ClinicalSignificance.LikelyBenign: return "probablement bénin";
                case ClinicalSignificance.Benign: return "bénin";
                case ClinicalSignificance.ConflictingInterpretations: return "interprétations divergentes";
                case ClinicalSignificance.DrugResponse: return "réponse médicamenteuse";
                case ClinicalSignificance.RiskFactor: return "facteur de risque";
                case ClinicalSignificance.Protective: return "protecteur";
                case ClinicalSignificance.Association: return "association";
                default: return "non renseigné";
            }
        }

        private static string Stars(int rating) =>
            new string('★', rating) + new string('☆', 4 - rating);

        private static string Wrap(string text, int indent)
        {
            const int width = 74;

            if (text.Length <= width - indent)
            {
                return text;
            }

            var words = text.Split(' ');
            var lines = new List<string>();
            var current = string.Empty;

            foreach (var word in words)
            {
                if (current.Length + word.Length + 1 > width - indent)
                {
                    lines.Add(current);
                    current = word;
                }
                else
                {
                    current = current.Length == 0 ? word : current + " " + word;
                }
            }

            if (current.Length > 0)
            {
                lines.Add(current);
            }

            return string.Join(Environment.NewLine + new string(' ', indent), lines);
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
