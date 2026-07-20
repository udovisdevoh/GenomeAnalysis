using System.Collections.Generic;
using GenomeAnalysis.Core.Genome;
using GenomeAnalysis.Core.Strands;
using Xunit;

namespace GenomeAnalysis.Tests
{
    /// <summary>
    /// Strand reconciliation is the step where a mistake produces a confidently
    /// wrong answer rather than a missing one, so these cases are the ones that
    /// matter most in the suite.
    /// </summary>
    public class StrandResolverTests
    {
        private static IReadOnlyCollection<Nucleotide> Alleles(params Nucleotide[] alleles) => alleles;

        [Fact]
        public void SameStrand_LeavesGenotypeUnchanged()
        {
            var observed = new Genotype(Nucleotide.A, Nucleotide.G);

            var match = StrandResolver.Resolve(
                observed,
                Strand.Plus,
                Strand.Plus,
                Alleles(Nucleotide.A, Nucleotide.G));

            Assert.Equal(StrandMatchOutcome.Resolved, match.Outcome);
            Assert.False(match.WasComplemented);
            Assert.Equal(observed.Normalized(), match.ResolvedGenotype);
        }

        [Fact]
        public void OppositeStrand_ComplementsAlleles()
        {
            // The provider reports AA on the plus strand; the source records this
            // variant on the minus strand, where the same call reads TT.
            var observed = new Genotype(Nucleotide.A, Nucleotide.A);

            var match = StrandResolver.Resolve(
                observed,
                Strand.Plus,
                Strand.Minus,
                Alleles(Nucleotide.T, Nucleotide.C));

            Assert.Equal(StrandMatchOutcome.Resolved, match.Outcome);
            Assert.True(match.WasComplemented);
            Assert.Equal(new Genotype(Nucleotide.T, Nucleotide.T).Normalized(), match.ResolvedGenotype);
        }

        [Theory]
        [InlineData(Nucleotide.A, Nucleotide.T)]
        [InlineData(Nucleotide.C, Nucleotide.G)]
        public void PalindromicVariant_HomozygousCall_IsAmbiguousFlip(Nucleotide first, Nucleotide second)
        {
            // A/A complemented is T/T, which is also a valid genotype of an A/T
            // variant. Nothing in the data decides between them.
            var observed = new Genotype(first, first);

            var match = StrandResolver.Resolve(
                observed,
                Strand.Plus,
                Strand.Minus,
                Alleles(first, second));

            Assert.Equal(StrandMatchOutcome.AmbiguousFlip, match.Outcome);
            Assert.Null(match.ResolvedGenotype);
            Assert.NotEmpty(match.Reason);
        }

        [Theory]
        [InlineData(Nucleotide.A, Nucleotide.T)]
        [InlineData(Nucleotide.C, Nucleotide.G)]
        public void PalindromicVariant_HeterozygousCall_IsUsable(Nucleotide first, Nucleotide second)
        {
            // A;T complemented is T;A — the same genotype. Strand is irrelevant, so
            // this palindromic case is decidable where the homozygous one is not.
            var observed = new Genotype(first, second);

            var match = StrandResolver.Resolve(
                observed,
                Strand.Plus,
                Strand.Minus,
                Alleles(first, second));

            Assert.Equal(StrandMatchOutcome.Resolved, match.Outcome);
            Assert.False(match.WasComplemented);
            Assert.Equal(observed.Normalized(), match.ResolvedGenotype);
        }

        [Fact]
        public void PalindromicVariant_HemizygousCall_IsAmbiguousFlip()
        {
            var match = StrandResolver.Resolve(
                new Genotype(Nucleotide.A),
                Strand.Plus,
                Strand.Minus,
                Alleles(Nucleotide.A, Nucleotide.T));

            Assert.Equal(StrandMatchOutcome.AmbiguousFlip, match.Outcome);
        }

        [Fact]
        public void UnknownAnnotationOrientation_IsUnresolvable_NotAssumedPlus()
        {
            var match = StrandResolver.Resolve(
                new Genotype(Nucleotide.A, Nucleotide.A),
                Strand.Plus,
                Strand.Unknown,
                Alleles(Nucleotide.A, Nucleotide.G));

            Assert.Equal(StrandMatchOutcome.Unresolvable, match.Outcome);
        }

        [Fact]
        public void UnknownAlleleSet_IsUnresolvable_BecausePalindromyCannotBeRuledOut()
        {
            var match = StrandResolver.Resolve(
                new Genotype(Nucleotide.A, Nucleotide.A),
                Strand.Plus,
                Strand.Plus,
                variantAlleles: null);

            Assert.Equal(StrandMatchOutcome.Unresolvable, match.Outcome);
        }

        [Fact]
        public void MappedGenotypeInconsistentWithVariantAlleles_IsUnresolvable()
        {
            // Observed C/C maps to C/C on the same strand, but this variant is only
            // known to have A and G alleles. Something upstream disagrees — marker,
            // orientation or build — and the result must not be used.
            var match = StrandResolver.Resolve(
                new Genotype(Nucleotide.C, Nucleotide.C),
                Strand.Plus,
                Strand.Plus,
                Alleles(Nucleotide.A, Nucleotide.G));

            Assert.Equal(StrandMatchOutcome.Unresolvable, match.Outcome);
        }

        [Theory]
        [InlineData(Nucleotide.A, Nucleotide.T, true)]
        [InlineData(Nucleotide.C, Nucleotide.G, true)]
        [InlineData(Nucleotide.A, Nucleotide.G, false)]
        [InlineData(Nucleotide.C, Nucleotide.T, false)]
        public void IsPalindromicVariant_IdentifiesComplementaryAllelePairs(
            Nucleotide first,
            Nucleotide second,
            bool expected)
        {
            Assert.Equal(expected, StrandResolver.IsPalindromicVariant(Alleles(first, second)));
        }
    }
}
