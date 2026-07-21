using System;
using System.Collections.Generic;
using System.Linq;
using GenomeAnalysis.Core.Analysis;
using GenomeAnalysis.Core.Genome;

namespace GenomeAnalysis.Core.Rules
{
    /// <summary>
    /// A star allele: the alleles it carries at its defining positions, and the
    /// function CPIC assigns it.
    /// </summary>
    public sealed class StarAllele
    {
        public StarAllele(string name, string? function, IReadOnlyDictionary<string, char> definitions)
        {
            Name = name;
            Function = function;
            Definitions = definitions;
        }

        public string Name { get; }

        /// <summary>e.g. "No function", "Normal function".</summary>
        public string? Function { get; }

        /// <summary>rsID to the allele carried there.</summary>
        public IReadOnlyDictionary<string, char> Definitions { get; }
    }

    /// <summary>
    /// One pharmacogene: its star alleles and the rules turning a pair of allele
    /// functions into a metabolizer phenotype.
    /// </summary>
    public sealed class Pharmacogene
    {
        public Pharmacogene(
            string gene,
            IReadOnlyList<StarAllele> alleles,
            IReadOnlyList<(string First, string Second, string Phenotype)> phenotypeRules)
        {
            Gene = gene;
            Alleles = alleles;
            PhenotypeRules = phenotypeRules;
        }

        public string Gene { get; }

        public IReadOnlyList<StarAllele> Alleles { get; }

        /// <summary>
        /// Function pair to phenotype, order-independent. Stored as rules rather
        /// than as the full diplotype table, which is their combinatorial expansion.
        /// </summary>
        public IReadOnlyList<(string First, string Second, string Phenotype)> PhenotypeRules { get; }

        public string? Phenotype(string? firstFunction, string? secondFunction)
        {
            if (string.IsNullOrWhiteSpace(firstFunction) || string.IsNullOrWhiteSpace(secondFunction))
            {
                return null;
            }

            foreach (var rule in PhenotypeRules)
            {
                var matches =
                    (Equals(rule.First, firstFunction) && Equals(rule.Second, secondFunction)) ||
                    (Equals(rule.First, secondFunction) && Equals(rule.Second, firstFunction));

                if (matches)
                {
                    return rule.Phenotype;
                }
            }

            return null;

            bool Equals(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Calls pharmacogenomic diplotypes from the positions a file actually covers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A star allele is a haplotype, so calling one is the same problem as deriving
    /// APOE: find the pairs of alleles consistent with the observed genotypes. What
    /// makes pharmacogenes harder is coverage. CYP2D6 has 146 defining positions
    /// and a consumer array carries a handful, so most star alleles simply cannot
    /// be assessed.
    /// </para>
    /// <para>
    /// That is reported rather than glossed over. A call of <c>*1/*2</c> made from
    /// three tested positions means "consistent with *1/*2 among the alleles that
    /// could be assessed" — the untested alleles are not excluded, and an untested
    /// no-function allele hiding behind an apparent <c>*1</c> would change the
    /// phenotype entirely.
    /// </para>
    /// </remarks>
    public sealed class PharmacogenomicsEngine
    {
        private readonly IReadOnlyList<Pharmacogene> _genes;

        public PharmacogenomicsEngine(IReadOnlyList<Pharmacogene> genes)
        {
            _genes = genes ?? throw new ArgumentNullException(nameof(genes));
        }

        public IReadOnlyList<RuleResult> Evaluate(IEnumerable<Finding> findings)
        {
            var genotypes = (findings ?? Enumerable.Empty<Finding>())
                .Where(f => f.Status == FindingStatus.Determinate && f.StrandMatch?.ResolvedGenotype != null)
                .GroupBy(f => f.Call.MarkerId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().StrandMatch!.ResolvedGenotype!.Value,
                    StringComparer.OrdinalIgnoreCase);

            return _genes.Select(gene => Evaluate(gene, genotypes)).ToList();
        }

        private static RuleResult Evaluate(Pharmacogene gene, IReadOnlyDictionary<string, Genotype> genotypes)
        {
            var ruleId = "pgx-" + gene.Gene.ToLowerInvariant();
            var ruleName = "Diplotype " + gene.Gene;

            // An allele can only be considered when every position defining it was
            // read. Assessing it from a subset would be guessing at the rest.
            var assessable = gene.Alleles
                .Where(a => a.Definitions.Keys.All(genotypes.ContainsKey))
                .ToList();

            var covered = gene.Alleles
                .SelectMany(a => a.Definitions.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(genotypes.ContainsKey)
                .ToList();

            if (covered.Count == 0)
            {
                return RuleResult.NotApplicable(ruleId, ruleName, gene.Gene);
            }

            if (assessable.Count < 1)
            {
                return RuleResult.Ambiguous(
                    ruleId, ruleName, gene.Gene,
                    Array.Empty<string>(),
                    "Aucun allèle de ce gène n'a toutes ses positions définitoires couvertes par ce fichier " +
                    "(" + covered.Count + " position(s) lue(s) sur " +
                    gene.Alleles.SelectMany(a => a.Definitions.Keys).Distinct(StringComparer.OrdinalIgnoreCase).Count() +
                    "). Aucun diplotype n'est déduit.",
                    covered,
                    phaseLimited: false);
            }

            var matches = new List<string>();

            for (var i = 0; i < assessable.Count; i++)
            {
                for (var j = i; j < assessable.Count; j++)
                {
                    if (IsConsistent(assessable[i], assessable[j], genotypes, out var checkedPositions) &&
                        checkedPositions > 0)
                    {
                        matches.Add(Diplotype(assessable[i].Name, assessable[j].Name));
                    }
                }
            }

            matches = matches.Distinct(StringComparer.Ordinal).OrderBy(m => m, StringComparer.Ordinal).ToList();

            var notAssessed = gene.Alleles.Count - assessable.Count;
            var caveat = notAssessed <= 0
                ? null
                : notAssessed + " des " + gene.Alleles.Count + " allèles connus de ce gène n'ont pas pu être " +
                  "évalués, faute de positions testées sur cette puce. Ils ne sont donc pas exclus : un allèle " +
                  "non testé peut se cacher derrière un allèle apparemment normal et changer entièrement le " +
                  "phénotype. Un test pharmacogénomique dédié est la seule façon de trancher.";

            if (matches.Count == 0)
            {
                return RuleResult.Ambiguous(
                    ruleId, ruleName, gene.Gene,
                    Array.Empty<string>(),
                    "Les génotypes lus ne correspondent à aucune paire d'allèles évaluables. " +
                    (caveat ?? string.Empty),
                    covered,
                    phaseLimited: false);
            }

            if (matches.Count > 1)
            {
                return RuleResult.Ambiguous(
                    ruleId, ruleName, gene.Gene,
                    matches,
                    "Plusieurs diplotypes restent compatibles avec les positions lues : les données de puce " +
                    "n'étant pas phasées, rien ne permet de les départager. " + (caveat ?? string.Empty),
                    covered);
            }

            var pair = matches[0].Split('/');
            var first = assessable.FirstOrDefault(a => a.Name == pair[0]);
            var second = assessable.FirstOrDefault(a => a.Name == pair[pair.Length - 1]);
            var phenotype = gene.Phenotype(first?.Function, second?.Function);

            var conclusion = matches[0] + (phenotype == null ? string.Empty : "  —  " + phenotype);

            return RuleResult.Determinate(
                ruleId, ruleName, gene.Gene,
                conclusion,
                "Une seule paire d'allèles évaluables reproduit les génotypes observés aux " +
                covered.Count + " position(s) lue(s) de ce gène." +
                (first?.Function == null || second?.Function == null
                    ? " La fonction d'au moins un allèle n'est pas renseignée, donc aucun phénotype n'est déduit."
                    : " Fonctions : " + first.Function + " + " + second.Function + "."),
                covered,
                phaseLimited: false,
                interpretation: caveat);
        }

        /// <summary>
        /// Checks a pair of alleles against the genotypes, at the positions where
        /// both alleles state what they carry.
        /// </summary>
        /// <remarks>
        /// Positions defined by only one of the two are skipped rather than assumed
        /// to carry the reference base: CPIC does not list a reference allele for
        /// every position of every star allele, and inventing one would turn a gap
        /// into a confident call.
        /// </remarks>
        private static bool IsConsistent(
            StarAllele first,
            StarAllele second,
            IReadOnlyDictionary<string, Genotype> genotypes,
            out int checkedPositions)
        {
            checkedPositions = 0;

            foreach (var position in first.Definitions.Keys)
            {
                if (!second.Definitions.TryGetValue(position, out var secondAllele) ||
                    !genotypes.TryGetValue(position, out var observed))
                {
                    continue;
                }

                var expected = new[] { first.Definitions[position], secondAllele }.OrderBy(c => c).ToArray();
                var actual = observed.Alleles.Select(n => n.ToChar()).OrderBy(c => c).ToArray();

                if (expected.Length != actual.Length || !expected.SequenceEqual(actual))
                {
                    return false;
                }

                checkedPositions++;
            }

            return true;
        }

        private static string Diplotype(string first, string second) =>
            string.Compare(first, second, StringComparison.Ordinal) <= 0
                ? first + "/" + second
                : second + "/" + first;
    }
}
