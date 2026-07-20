using System.Collections.Generic;
using System.Linq;
using GenomeAnalysis.Core.Genome;

namespace GenomeAnalysis.Core.Strands
{
    /// <summary>
    /// Reconciles a genotype observed in a consumer test file with the strand
    /// orientation an annotation source reports on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the single most dangerous step in the pipeline. Providers and
    /// annotation sources do not always read the same DNA strand, so a genotype
    /// reported <c>AA</c> may correspond to the <c>(T;T)</c> record. Skipping the
    /// reconciliation does not produce missing results — it produces confidently
    /// wrong ones, which is far worse.
    /// </para>
    /// <para>
    /// When matching against SNPedia, the orientation to pass is
    /// <c>StabilizedOrientation</c>, not <c>Orientation</c>. The former is the one
    /// kept consistent with the genotype pages; using the latter yields incorrect
    /// matches.
    /// </para>
    /// <para>
    /// Every path that cannot be decided returns
    /// <see cref="StrandMatchOutcome.AmbiguousFlip"/> or
    /// <see cref="StrandMatchOutcome.Unresolvable"/>. There is deliberately no
    /// fallback that picks a reading, because an arbitrary choice on a palindromic
    /// variant can invert an association — presenting a protective allele as a
    /// risk allele.
    /// </para>
    /// </remarks>
    public static class StrandResolver
    {
        /// <summary>
        /// True when a variant's two alleles are each other's complement (A/T or
        /// C/G). Such a variant reads identically from either strand, so the
        /// alleles alone cannot tell you which strand the data came from.
        /// </summary>
        public static bool IsPalindromicVariant(IReadOnlyCollection<Nucleotide>? variantAlleles)
        {
            if (variantAlleles == null)
            {
                return false;
            }

            var distinct = variantAlleles.Distinct().ToList();

            return distinct.Count == 2 &&
                   NucleotideExtensions.IsPalindromicPair(distinct[0], distinct[1]);
        }

        /// <summary>
        /// Maps <paramref name="observed"/> into the orientation used by the
        /// annotation source.
        /// </summary>
        /// <param name="observed">The genotype as the provider reported it.</param>
        /// <param name="observedStrand">
        /// The strand the provider reports on. Consumer files are conventionally
        /// plus-strand, but pass what the file actually states rather than
        /// assuming.
        /// </param>
        /// <param name="annotationStrand">
        /// The annotation source's orientation — SNPedia's
        /// <c>StabilizedOrientation</c>.
        /// </param>
        /// <param name="variantAlleles">
        /// The variant's possible alleles, derived from the source's known
        /// genotypes. Required: without it, palindromy cannot be ruled out.
        /// </param>
        public static StrandMatch Resolve(
            Genotype observed,
            Strand observedStrand,
            Strand annotationStrand,
            IReadOnlyCollection<Nucleotide>? variantAlleles)
        {
            // A heterozygous palindromic genotype (A;T or C;G) is its own
            // complement, so the strand simply does not matter. This is checked
            // first because it resolves even when orientation is unreported.
            if (observed.IsSelfComplementary())
            {
                return StrandMatch.Resolved(
                    observed.Normalized(),
                    wasComplemented: false,
                    reason: "Genotype is unchanged by complementation, so strand orientation does not affect it.");
            }

            if (variantAlleles == null || variantAlleles.Count == 0)
            {
                return StrandMatch.Unresolvable(
                    "The variant's allele set is unknown, so an ambiguous flip cannot be ruled out.");
            }

            // Palindromic variant with a homozygous or hemizygous call: both
            // readings are valid genotypes of this same variant and nothing in the
            // data distinguishes them.
            if (IsPalindromicVariant(variantAlleles))
            {
                return StrandMatch.AmbiguousFlip(
                    "This is a palindromic (A/T or C/G) variant and the call is not heterozygous, " +
                    "so the reported alleles are valid on either strand and the reading cannot be determined.");
            }

            if (observedStrand == Strand.Unknown || annotationStrand == Strand.Unknown)
            {
                return StrandMatch.Unresolvable(
                    "Strand orientation was not reported by " +
                    (observedStrand == Strand.Unknown ? "the genome file" : "the annotation source") +
                    ", and no orientation is assumed by default.");
            }

            var mustComplement = observedStrand != annotationStrand;
            var candidate = mustComplement ? observed.Complement() : observed;

            // Safety net: the mapped genotype must be built from alleles the
            // variant actually has. A mismatch means the marker, the orientation
            // or the reference build disagree, and the result must not be used.
            var alleleSet = new HashSet<Nucleotide>(variantAlleles);

            if (!candidate.Alleles.All(alleleSet.Contains))
            {
                return StrandMatch.Unresolvable(
                    "The mapped genotype is not consistent with the alleles known for this variant, " +
                    "which points to a marker, orientation or reference build mismatch.");
            }

            return StrandMatch.Resolved(
                candidate.Normalized(),
                mustComplement,
                mustComplement
                    ? "Alleles were complemented because the annotation source reports on the opposite strand."
                    : "Both sources report on the same strand.");
        }
    }
}
