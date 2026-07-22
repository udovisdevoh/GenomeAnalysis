using System;
using System.Collections.Generic;
using System.Linq;
using GenomeAnalysis.Core.Analysis;
using GenomeAnalysis.Core.Annotations;
using GenomeAnalysis.Core.Genome;
using GenomeAnalysis.Core.Rules;
using GenomeAnalysis.Core.Strands;
using Xunit;

namespace GenomeAnalysis.Tests
{
    public class PolygenicScoreEngineTests
    {
        private static Finding Resolved(string rsId, string genotype)
        {
            Genotype.TryParse(genotype, out var parsed);

            var annotation = new VariantAnnotation(
                rsId, Strand.Plus, Strand.Unknown, null, null,
                SourceAttribution.Ensembl(rsId), null, null, null, parsed.Alleles.ToList());

            var call = new MarkerCall(rsId, Chromosome.Autosome(1), 1, parsed);
            var match = StrandResolver.Resolve(parsed, Strand.Plus, Strand.Plus, parsed.Alleles.ToList());

            return new Finding(call, FindingStatus.Determinate, "test", annotation, null, match);
        }

        private static PolygenicScore Score(params ScoreVariant[] variants) => new PolygenicScore(
            "PGS_TEST", "Test score", "Trait", "European", "Cite et al.", "GRCh37", variants);

        private static PolygenicScoreResult Evaluate(PolygenicScore score, params Finding[] findings) =>
            new PolygenicScoreEngine(new[] { score }).Evaluate(findings).Single();

        [Fact]
        public void SumsDosageTimesWeight_NeverMultiplyingOddsRatios()
        {
            // Two effect alleles at variant 1 (weight 0.5) plus one at variant 2
            // (weight 0.2) = 2*0.5 + 1*0.2 = 1.2.
            var score = Score(
                new ScoreVariant("rs1", 'A', 'G', 0.5),
                new ScoreVariant("rs2", 'T', 'C', 0.2));

            var result = Evaluate(score, Resolved("rs1", "AA"), Resolved("rs2", "TC"));

            Assert.Equal(1.2, result.RawScore, 6);
            Assert.Equal(2, result.VariantsCovered);
        }

        [Fact]
        public void CountsEffectAlleleDosageZeroOneTwo()
        {
            var score = Score(new ScoreVariant("rs1", 'A', 'G', 1.0));

            Assert.Equal(0.0, Evaluate(score, Resolved("rs1", "GG")).RawScore, 6);
            Assert.Equal(1.0, Evaluate(score, Resolved("rs1", "AG")).RawScore, 6);
            Assert.Equal(2.0, Evaluate(score, Resolved("rs1", "AA")).RawScore, 6);
        }

        [Fact]
        public void MissingVariantIsExcluded_NotTreatedAsZeroDosage()
        {
            // Only rs1 is present. rs2 must not contribute a silent zero — it drops
            // out and lowers coverage instead.
            var score = Score(
                new ScoreVariant("rs1", 'A', 'G', 0.5),
                new ScoreVariant("rs2", 'T', 'C', 0.5));

            var result = Evaluate(score, Resolved("rs1", "AA"));

            Assert.Equal(1.0, result.RawScore, 6);
            Assert.Equal(1, result.VariantsCovered);
            Assert.Equal(0.5, result.CoveredWeightFraction, 6);
        }

        [Fact]
        public void CoverageIsMeasuredByWeight_NotJustCount()
        {
            // Covering the small-weight variant and missing the large one is poor
            // coverage even though half the variants are present.
            var score = Score(
                new ScoreVariant("rs1", 'A', 'G', 0.1),
                new ScoreVariant("rs2", 'T', 'C', 0.9));

            var result = Evaluate(score, Resolved("rs1", "AG"));

            Assert.Equal(0.1, result.CoveredWeightFraction, 6);
        }

        [Fact]
        public void PalindromicVariantIsExcluded_NotGuessed()
        {
            // A/T effect/other, homozygous call: both strand readings are valid, so
            // the dosage is undecidable. It must drop out of the sum.
            var score = Score(new ScoreVariant("rs1", 'A', 'T', 1.0));

            var result = Evaluate(score, Resolved("rs1", "AA"));

            Assert.Equal(0.0, result.RawScore, 6);
            Assert.Equal(0, result.VariantsCovered);
            Assert.Equal(1, result.VariantsExcludedAmbiguous);
        }

        [Fact]
        public void HeterozygousPalindromeIsUsable_BecauseStrandDoesNotChangeIt()
        {
            // A;T is its own complement, so dosage of the effect allele is 1 either
            // way — this palindromic case is decidable.
            var score = Score(new ScoreVariant("rs1", 'A', 'T', 1.0));

            var result = Evaluate(score, Resolved("rs1", "AT"));

            Assert.Equal(1.0, result.RawScore, 6);
            Assert.Equal(1, result.VariantsCovered);
        }

        [Fact]
        public void GenotypeInconsistentWithScoreAllelesIsExcluded()
        {
            // Score expects A/G; the file says C/C. Something upstream disagrees, so
            // the variant is dropped rather than scored as zero effect alleles.
            var score = Score(new ScoreVariant("rs1", 'A', 'G', 1.0));

            var result = Evaluate(score, Resolved("rs1", "CC"));

            Assert.Equal(1, result.VariantsExcludedMismatch);
            Assert.Equal(0, result.VariantsCovered);
        }

        [Fact]
        public void NoCoveredVariantsIsNotApplicable()
        {
            var score = Score(new ScoreVariant("rs1", 'A', 'G', 1.0));

            var result = Evaluate(score, Resolved("rs2", "AA"));

            Assert.Equal(PolygenicScoreOutcome.NotApplicable, result.Outcome);
        }

        [Fact]
        public void PercentileIsWithheldWhenCoverageIsLow()
        {
            // Present one of two equal-weight variants: 50% coverage, below the
            // threshold. A precise percentile from half a score would mislead.
            var score = Score(
                new ScoreVariant("rs1", 'A', 'G', 0.5, effectAlleleFrequency: 0.3),
                new ScoreVariant("rs2", 'T', 'C', 0.5, effectAlleleFrequency: 0.3));

            var result = Evaluate(score, Resolved("rs1", "AG"));

            Assert.Equal(PolygenicScoreOutcome.Partial, result.Outcome);
            Assert.Null(result.Percentile);
            Assert.Contains("couverture est trop faible", result.Reason);
        }

        [Fact]
        public void NoReferenceDistribution_MeansNoPercentile_EvenAtFullCoverage()
        {
            // Full coverage but no frequencies and no documented reference: a raw
            // sum cannot be placed on a distribution, so no percentile is invented.
            var score = Score(new ScoreVariant("rs1", 'A', 'G', 1.0));

            var result = Evaluate(score, Resolved("rs1", "AG"));

            Assert.Equal(PolygenicScoreOutcome.Partial, result.Outcome);
            Assert.Null(result.Percentile);
            Assert.Contains("aucune distribution de référence", result.Reason);
        }

        [Fact]
        public void ModelBasedPercentileIsComputedFromFrequenciesAtFullCoverage()
        {
            // Effect-allele frequency present for every covered variant, full
            // coverage: a model reference mean/variance under HWE gives a z-score.
            // With f = 0.5 and a heterozygous call, dosage 1 equals the mean 2f = 1,
            // so the person sits at the population median.
            var score = Score(new ScoreVariant("rs1", 'A', 'G', 1.0, effectAlleleFrequency: 0.5));

            var result = Evaluate(score, Resolved("rs1", "AG"));

            Assert.Equal(PolygenicScoreOutcome.Placed, result.Outcome);
            Assert.NotNull(result.Percentile);
            Assert.Equal(50.0, result.Percentile!.Value, 1);
            Assert.Equal(0.0, result.ZScore!.Value, 6);
        }

        [Fact]
        public void HighDosageAgainstARareEffectAllele_LandsHighInTheDistribution()
        {
            // Effect allele frequency 0.1, but the person carries two copies: well
            // above the population mean, so a high percentile.
            var score = Score(new ScoreVariant("rs1", 'A', 'G', 1.0, effectAlleleFrequency: 0.1));

            var result = Evaluate(score, Resolved("rs1", "AA"));

            Assert.Equal(PolygenicScoreOutcome.Placed, result.Outcome);
            Assert.True(result.Percentile > 90, "Two copies of a rare effect allele should rank high.");
        }

        [Fact]
        public void DocumentedReferenceDistributionIsUsedWhenPresent()
        {
            var score = new PolygenicScore(
                "PGS_REF", "Ref score", "Trait", "European", "Cite", "GRCh37",
                new[] { new ScoreVariant("rs1", 'A', 'G', 1.0) },
                referenceMean: 0.0,
                referenceStandardDeviation: 1.0);

            // Raw score of 2 (homozygous effect) against mean 0, sd 1 → z = 2.
            var result = new PolygenicScoreEngine(new[] { score }).Evaluate(new[] { Resolved("rs1", "AA") }).Single();

            Assert.Equal(PolygenicScoreOutcome.Placed, result.Outcome);
            Assert.Equal(2.0, result.ZScore!.Value, 6);
            Assert.True(result.Percentile > 97);
        }

        [Fact]
        public void EveryPlacedResultCarriesTheAncestryCaveat()
        {
            var score = Score(new ScoreVariant("rs1", 'A', 'G', 1.0, effectAlleleFrequency: 0.5));

            var result = Evaluate(score, Resolved("rs1", "AG"));

            Assert.Contains("développé en population", result.Reason);
            Assert.Contains("non un diagnostic", result.Reason);
        }

        [Fact]
        public void NoCallsAreNotScored()
        {
            // A no-call carries no genotype, so it cannot contribute. (A score
            // resolves strand against its own alleles, so an annotation-layer
            // "indeterminate" does not by itself exclude a variant — only the
            // absence of a genotype does.)
            var noCall = new Finding(
                new MarkerCall("rs1", Chromosome.Autosome(1), 1, null, "--"),
                FindingStatus.NotApplicable,
                "no-call");

            var result = Evaluate(Score(new ScoreVariant("rs1", 'A', 'G', 1.0)), noCall);

            Assert.Equal(PolygenicScoreOutcome.NotApplicable, result.Outcome);
        }

        [Fact]
        public void ScoresVariantsTheAnnotationDatabaseDoesNotKnow()
        {
            // The score's alleles are self-sufficient: a PGS variant absent from the
            // annotation database is still scored from the observed call.
            var notInDatabase = new Finding(
                new MarkerCall("rs_novel", Chromosome.Autosome(1), 1, new Genotype(Nucleotide.A, Nucleotide.A)),
                FindingStatus.NotApplicable,
                "not covered by the local variant database");

            var result = Evaluate(Score(new ScoreVariant("rs_novel", 'A', 'G', 1.0)), notInDatabase);

            Assert.Equal(1, result.VariantsCovered);
            Assert.Equal(2.0, result.RawScore, 6);
        }

        [Theory]
        [InlineData(0.0, 50.0)]
        [InlineData(1.96, 97.5)]
        [InlineData(-1.96, 2.5)]
        [InlineData(1.0, 84.13)]
        public void NormalCdfMatchesKnownQuantiles(double z, double expectedPercentile)
        {
            Assert.Equal(expectedPercentile, PolygenicScoreEngine.NormalCdf(z) * 100, 1);
        }
    }
}
