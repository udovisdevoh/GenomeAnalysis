using System;
using System.Collections.Generic;
using System.Linq;
using GenomeAnalysis.Core.Genome;

namespace GenomeAnalysis.Core.Annotations
{
    /// <summary>
    /// Combines what several sources say about the same variant.
    /// </summary>
    /// <remarks>
    /// <para>
    /// No single source covers everything: Ensembl has the allele set and strand,
    /// ClinVar has the clinical classification, gnomAD has the frequency, and only
    /// SNPedia has stabilized orientation and readable genotype text. A usable
    /// record is the union.
    /// </para>
    /// <para>
    /// Merging is per-field and never averages or invents. Where two sources
    /// disagree on a clinical classification the better-reviewed one wins, and a
    /// genuine conflict stays a conflict rather than being resolved silently.
    /// </para>
    /// </remarks>
    public static class AnnotationMerge
    {
        /// <summary>
        /// Merges annotations for one variant. Order matters only as a tie-break:
        /// for any given field, a source that reports it beats one that does not.
        /// </summary>
        public static VariantAnnotation? Combine(IEnumerable<VariantAnnotation?> annotations)
        {
            var present = (annotations ?? Enumerable.Empty<VariantAnnotation?>())
                .Where(a => a != null)
                .Select(a => a!)
                .ToList();

            if (present.Count == 0)
            {
                return null;
            }

            if (present.Count == 1)
            {
                return present[0];
            }

            var alleles = present
                .Select(a => a.KnownAlleles)
                .FirstOrDefault(set => set != null && set.Count > 0);

            return new VariantAnnotation(
                present[0].RsId,

                // An orientation that was actually reported beats an absent one.
                // Unknown is never overwritten onto a known value, and never
                // fabricated where nothing reported it.
                FirstKnownStrand(present.Select(a => a.Orientation)),
                FirstKnownStrand(present.Select(a => a.StabilizedOrientation)),

                present.SelectMany(a => a.Genotypes).ToList(),
                present.Select(a => a.Summary).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)),

                // The reference-data source owns the top-level attribution. Claims
                // that carry their own — clinical findings, genotype text — keep
                // theirs, so every displayed statement stays traceable.
                present[0].Attribution,

                present.Select(a => a.GeneSymbol).FirstOrDefault(g => !string.IsNullOrWhiteSpace(g)),
                BestClinical(present.Select(a => a.Clinical)),
                NormaliseFrequency(present.Select(a => a.MinorAlleleFrequency).FirstOrDefault(f => f.HasValue)),
                alleles,
                present.SelectMany(a => a.MergedRsIds)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                present.Select(a => a.MostSevereConsequence)
                    .FirstOrDefault(c => !string.IsNullOrWhiteSpace(c)),

                // Union across sources, deduplicated by trait and publication, then
                // ordered so the strongest evidence leads.
                present.SelectMany(a => a.TraitAssociations)
                    .GroupBy(t => (t.Trait, t.PubMedId), TraitKeyComparer.Instance)
                    .Select(g => g.First())
                    .OrderByDescending(t => t.IsGenomeWideSignificant)
                    .ThenBy(t => t.PValue ?? double.MaxValue)
                    .ToList(),

                // Whichever source states it; sources that do not simply pass null.
                present.Select(a => a.ReferenceAllele).FirstOrDefault(r => r.HasValue));
        }

        /// <summary>
        /// Treats trait plus publication as the identity of an association, so the
        /// same finding arriving from two sources is not counted twice.
        /// </summary>
        private sealed class TraitKeyComparer : IEqualityComparer<(string Trait, string? PubMedId)>
        {
            public static readonly TraitKeyComparer Instance = new TraitKeyComparer();

            public bool Equals((string Trait, string? PubMedId) x, (string Trait, string? PubMedId) y) =>
                string.Equals(x.Trait, y.Trait, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.PubMedId, y.PubMedId, StringComparison.OrdinalIgnoreCase);

            public int GetHashCode((string Trait, string? PubMedId) key)
            {
                unchecked
                {
                    var hash = StringComparer.OrdinalIgnoreCase.GetHashCode(key.Trait ?? string.Empty);
                    return (hash * 397) ^
                           StringComparer.OrdinalIgnoreCase.GetHashCode(key.PubMedId ?? string.Empty);
                }
            }
        }

        /// <summary>
        /// Guards against a source reporting the major allele's frequency in a field
        /// meant for the minor one.
        /// </summary>
        /// <remarks>
        /// A minor allele frequency above 0.5 is a contradiction in terms, yet
        /// Ensembl's <c>MAF</c> field returns 0.98 for rs6025 (factor V Leiden),
        /// whose risk allele sits near 2% in Europeans. Taken at face value it would
        /// mark a rare pathogenic variant as common, and the report would play it
        /// down for being unremarkable. Reading it as the complementary allele
        /// recovers the right figure.
        /// </remarks>
        private static double? NormaliseFrequency(double? frequency)
        {
            if (!frequency.HasValue)
            {
                return null;
            }

            var value = frequency.Value;

            if (value < 0 || value > 1)
            {
                return null;
            }

            return value > 0.5 ? 1 - value : value;
        }

        private static Strand FirstKnownStrand(IEnumerable<Strand> strands)
        {
            foreach (var strand in strands)
            {
                if (strand != Strand.Unknown)
                {
                    return strand;
                }
            }

            return Strand.Unknown;
        }

        /// <summary>
        /// Picks the clinical record with the strongest review. Review status is the
        /// right tie-break rather than the classification itself: a four-star benign
        /// call carries more weight than a one-star pathogenic one, and choosing by
        /// alarm level would systematically overstate.
        /// </summary>
        private static ClinicalAnnotation? BestClinical(IEnumerable<ClinicalAnnotation?> clinicals)
        {
            return clinicals
                .Where(c => c != null)
                .Select(c => c!)
                .OrderByDescending(c => c.ReviewStatus.ToStarRating())
                .ThenByDescending(c => c.Conditions.Count)
                .FirstOrDefault();
        }
    }
}
