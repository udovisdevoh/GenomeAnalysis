using System;
using System.Collections.Generic;
using System.Linq;
using GenomeAnalysis.Core.Analysis;
using GenomeAnalysis.Core.Genome;

namespace GenomeAnalysis.Core.Rules
{
    /// <summary>
    /// Evaluates declarative multi-marker rules against the findings of an analysis.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Most useful interpretations rest on a combination of markers rather than on
    /// any single one, so this runs after per-marker analysis and consumes its
    /// results — the genotypes it sees have already been strand-resolved.
    /// </para>
    /// <para>
    /// The engine never fills a gap. A missing position, an ambiguous flip, or a
    /// combination the data cannot narrow to one answer all produce
    /// <see cref="RuleOutcome.Indeterminate"/> with the reason attached.
    /// </para>
    /// </remarks>
    public sealed class RuleEngine
    {
        private readonly IReadOnlyList<RuleDefinition> _rules;

        public RuleEngine(IReadOnlyList<RuleDefinition> rules)
        {
            _rules = rules ?? throw new ArgumentNullException(nameof(rules));
        }

        public IReadOnlyList<RuleResult> Evaluate(IEnumerable<Finding> findings)
        {
            // Only determinate findings can feed a rule: a genotype that could not
            // be strand-resolved must not be used as though it had been.
            var byMarker = (findings ?? Enumerable.Empty<Finding>())
                .Where(f => f.Status == FindingStatus.Determinate && f.StrandMatch?.ResolvedGenotype != null)
                .GroupBy(f => f.Call.MarkerId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().StrandMatch!.ResolvedGenotype!.Value,
                    StringComparer.OrdinalIgnoreCase);

            var readMarkers = new HashSet<string>(
                (findings ?? Enumerable.Empty<Finding>()).Select(f => f.Call.MarkerId),
                StringComparer.OrdinalIgnoreCase);

            return _rules.Select(rule => Evaluate(rule, byMarker, readMarkers)).ToList();
        }

        private static RuleResult Evaluate(
            RuleDefinition rule,
            IReadOnlyDictionary<string, Genotype> genotypes,
            ISet<string> readMarkers)
        {
            var present = rule.RequiredMarkers.Where(genotypes.ContainsKey).ToList();
            var missing = rule.RequiredMarkers.Where(m => !genotypes.ContainsKey(m)).ToList();

            if (present.Count == 0)
            {
                return RuleResult.NotApplicable(rule.Id, rule.Name, rule.Gene);
            }

            if (missing.Count > 0)
            {
                return RuleResult.Missing(rule.Id, rule.Name, rule.Gene, missing, present);
            }

            switch (rule.Kind)
            {
                case RuleKind.Haplotype:
                    return EvaluateHaplotype(rule, genotypes);
                case RuleKind.CompoundHeterozygosity:
                    return EvaluateCompoundHeterozygosity(rule, genotypes);
                default:
                    return RuleResult.NotApplicable(rule.Id, rule.Name, rule.Gene);
            }
        }

        /// <summary>
        /// Derives a diplotype by finding every pair of defined haplotypes whose
        /// combined alleles reproduce the observed genotypes.
        /// </summary>
        /// <remarks>
        /// Array data is unphased, so more than one pair can fit. APOE is the
        /// textbook case: a heterozygote at both rs429358 and rs7412 is consistent
        /// with ε1/ε3 and with ε2/ε4, and nothing in the genotypes distinguishes
        /// them. Both are reported; picking one would be a guess with clinically
        /// opposite meanings.
        /// </remarks>
        private static RuleResult EvaluateHaplotype(
            RuleDefinition rule,
            IReadOnlyDictionary<string, Genotype> genotypes)
        {
            var matches = new List<string>();

            for (var i = 0; i < rule.Haplotypes.Count; i++)
            {
                for (var j = i; j < rule.Haplotypes.Count; j++)
                {
                    if (IsConsistent(rule, genotypes, rule.Haplotypes[i], rule.Haplotypes[j]))
                    {
                        matches.Add(Diplotype(rule.Haplotypes[i].Name, rule.Haplotypes[j].Name));
                    }
                }
            }

            matches = matches.Distinct(StringComparer.Ordinal).OrderBy(m => m, StringComparer.Ordinal).ToList();

            if (matches.Count == 0)
            {
                return RuleResult.Ambiguous(
                    rule.Id, rule.Name, rule.Gene,
                    Array.Empty<string>(),
                    "Les génotypes lus ne correspondent à aucune combinaison d'haplotypes définie pour ce gène. " +
                    "Cela peut signaler une discordance de brin, de build ou de marqueur — aucun résultat n'est déduit.",
                    rule.RequiredMarkers,
                    phaseLimited: false);
            }

            if (matches.Count > 1)
            {
                return RuleResult.Ambiguous(
                    rule.Id, rule.Name, rule.Gene,
                    matches,
                    "Les données de puce ne sont pas phasées : on ne sait pas quels allèles siègent sur la même " +
                    "copie du chromosome. " + matches.Count + " diplotypes restent compatibles avec ces génotypes " +
                    "et rien dans les données ne permet de les départager.",
                    rule.RequiredMarkers);
            }

            rule.Interpretations.TryGetValue(matches[0], out var interpretation);

            return RuleResult.Determinate(
                rule.Id, rule.Name, rule.Gene,
                matches[0],
                "Une seule combinaison d'haplotypes reproduit les génotypes observés aux " +
                rule.RequiredMarkers.Count + " positions requises.",
                rule.RequiredMarkers,
                phaseLimited: false,
                interpretation: interpretation);
        }

        private static bool IsConsistent(
            RuleDefinition rule,
            IReadOnlyDictionary<string, Genotype> genotypes,
            HaplotypeDefinition first,
            HaplotypeDefinition second)
        {
            foreach (var marker in rule.RequiredMarkers)
            {
                if (!first.Alleles.TryGetValue(marker, out var a) ||
                    !second.Alleles.TryGetValue(marker, out var b))
                {
                    return false;
                }

                var observed = genotypes[marker];

                // A genotype is an unordered pair, so compare as a multiset.
                var expected = new[] { a, b }.OrderBy(c => c).ToArray();
                var actual = observed.Alleles.Select(n => n.ToChar()).OrderBy(c => c).ToArray();

                if (expected.Length != actual.Length || !expected.SequenceEqual(actual))
                {
                    return false;
                }
            }

            return true;
        }

        private static string Diplotype(string first, string second)
        {
            return string.Compare(first, second, StringComparison.Ordinal) <= 0
                ? first + "/" + second
                : second + "/" + first;
        }

        /// <summary>
        /// Reports whether two impairing variants in the same gene are both present.
        /// </summary>
        /// <remarks>
        /// This is where the phase limitation bites hardest. Two heterozygous
        /// variants are consistent with one damaged copy and one intact copy (in
        /// <em>cis</em>) just as much as with both copies damaged (in
        /// <em>trans</em>) — and only the second is true compound heterozygosity.
        /// The wording stays "consistent with", never "confirmed".
        /// </remarks>
        private static RuleResult EvaluateCompoundHeterozygosity(
            RuleDefinition rule,
            IReadOnlyDictionary<string, Genotype> genotypes)
        {
            var carried = new List<string>();
            var homozygous = new List<string>();

            foreach (var variant in rule.Variants)
            {
                var genotype = genotypes[variant.RsId];
                var copies = genotype.Alleles.Count(n => n.ToChar() == variant.RiskAllele);

                if (copies == 0)
                {
                    continue;
                }

                carried.Add(variant.Label);

                if (copies >= 2)
                {
                    homozygous.Add(variant.Label);
                }
            }

            if (carried.Count == 0)
            {
                return RuleResult.Determinate(
                    rule.Id, rule.Name, rule.Gene,
                    "aucun variant porté",
                    "Aucune des " + rule.Variants.Count + " positions examinées ne porte de variant altérant ce gène.",
                    rule.RequiredMarkers,
                    phaseLimited: false,
                    interpretation: rule.Note);
            }

            if (homozygous.Count > 0)
            {
                // Two copies of the same variant need no phase information: both
                // chromosome copies necessarily carry it.
                return RuleResult.Determinate(
                    rule.Id, rule.Name, rule.Gene,
                    "homozygote " + string.Join(" + ", homozygous),
                    "Deux copies du même variant (" + string.Join(", ", homozygous) + ") : les deux copies du " +
                    "chromosome sont atteintes, ce qui ne demande aucune information de phase.",
                    rule.RequiredMarkers,
                    phaseLimited: false,
                    interpretation: rule.Note);
            }

            if (carried.Count == 1)
            {
                return RuleResult.Determinate(
                    rule.Id, rule.Name, rule.Gene,
                    "hétérozygote " + carried[0],
                    "Une seule copie porte un variant altérant ce gène ; l'autre copie est intacte aux positions examinées.",
                    rule.RequiredMarkers,
                    phaseLimited: false,
                    interpretation: rule.Note);
            }

            return RuleResult.Determinate(
                rule.Id, rule.Name, rule.Gene,
                "compatible avec une hétérozygotie composite (" + string.Join(" + ", carried) + ")",
                "Deux variants altérants différents sont présents à l'état hétérozygote. Les données de puce " +
                "n'étant pas phasées, il est impossible de dire s'ils siègent sur des copies opposées du " +
                "chromosome (les deux copies atteintes) ou sur la même copie (une copie intacte). Ces deux " +
                "situations ont des conséquences très différentes et ne peuvent pas être départagées ici.",
                rule.RequiredMarkers,
                phaseLimited: true,
                interpretation: rule.Note);
        }
    }
}
