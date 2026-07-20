using GenomeAnalysis.Core.Genome;
using Xunit;

namespace GenomeAnalysis.Tests
{
    public class GenotypeTests
    {
        [Theory]
        [InlineData("--")]      // 23andMe no-call
        [InlineData("0")]       // AncestryDNA no-call
        [InlineData("00")]
        [InlineData("II")]      // insertion marker, not interpreted
        [InlineData("DD")]      // deletion marker, not interpreted
        [InlineData("NN")]
        [InlineData("")]
        [InlineData(null)]
        public void NoCallTokens_AreNotGenotypes(string? raw)
        {
            Assert.True(Genotype.IsNoCallToken(raw));
            Assert.False(Genotype.TryParse(raw, out _));
        }

        [Fact]
        public void DiploidCall_ParsesBothAlleles()
        {
            Assert.True(Genotype.TryParse("AG", out var genotype));

            Assert.Equal(2, genotype.AlleleCount);
            Assert.True(genotype.IsHeterozygous);
            Assert.False(genotype.IsHemizygous);
        }

        [Fact]
        public void SingleAlleleCall_IsHemizygous()
        {
            // Y and MT markers, and X in males, carry one allele.
            Assert.True(Genotype.TryParse("A", out var genotype));

            Assert.Equal(1, genotype.AlleleCount);
            Assert.True(genotype.IsHemizygous);
            Assert.False(genotype.IsHomozygous);
        }

        [Fact]
        public void AncestryPairColumns_ParseIntoOneGenotype()
        {
            Assert.True(Genotype.TryParsePair("A", "G", out var genotype));
            Assert.Equal(new Genotype(Nucleotide.A, Nucleotide.G), genotype);
        }

        [Fact]
        public void AncestryPairColumns_RejectNoCallInEitherPosition()
        {
            Assert.False(Genotype.TryParsePair("0", "G", out _));
            Assert.False(Genotype.TryParsePair("A", "0", out _));
        }

        [Fact]
        public void Normalized_MakesAlleleOrderIrrelevant()
        {
            Genotype.TryParse("AG", out var ag);
            Genotype.TryParse("GA", out var ga);

            Assert.Equal(ag.Normalized(), ga.Normalized());
        }

        [Fact]
        public void Complement_SwapsBothAlleles()
        {
            Genotype.TryParse("AC", out var genotype);

            Assert.Equal(new Genotype(Nucleotide.T, Nucleotide.G).Normalized(),
                genotype.Complement().Normalized());
        }

        [Theory]
        [InlineData("AT", true)]
        [InlineData("TA", true)]
        [InlineData("CG", true)]
        [InlineData("AA", false)]
        [InlineData("AG", false)]
        public void IsSelfComplementary_DetectsGenotypesStrandCannotChange(string raw, bool expected)
        {
            Genotype.TryParse(raw, out var genotype);
            Assert.Equal(expected, genotype.IsSelfComplementary());
        }

        [Fact]
        public void SnpediaNotation_MatchesGenotypePageFormat()
        {
            Genotype.TryParse("AG", out var genotype);
            Assert.Equal("(A;G)", genotype.ToSnpediaNotation());
        }

        [Fact]
        public void ToString_RoundTripsThroughTryParse()
        {
            Genotype.TryParse("CT", out var original);
            Assert.True(Genotype.TryParse(original.ToString(), out var reparsed));
            Assert.Equal(original, reparsed);
        }
    }
}
