using System.Collections.Generic;
using System.Linq;
using GenomeAnalysis.Core.Analysis;
using GenomeAnalysis.Core.Annotations;
using GenomeAnalysis.Core.Genome;
using GenomeAnalysis.Core.Strands;
using Xunit;

namespace GenomeAnalysis.Tests
{
    public class GenomeAnalyzerTests
    {
        /// <summary>
        /// Prothrombin G20210A: reference G, pathogenic variant A.
        /// </summary>
        private static VariantAnnotation Prothrombin() => new VariantAnnotation(
            "rs1799963",
            Strand.Plus,
            Strand.Unknown,
            genotypes: null,
            summary: null,
            attribution: SourceAttribution.Ensembl("rs1799963"),
            geneSymbol: "F2",
            clinical: new ClinicalAnnotation(
                ClinicalSignificance.Pathogenic,
                ClinVarReviewStatus.MultipleSubmittersNoConflicts,
                new[] { "Thrombophilia due to thrombin defect" },
                SourceAttribution.ClinVar("13")),
            minorAlleleFrequency: 0.008,
            knownAlleles: new[] { Nucleotide.G, Nucleotide.A },
            mergedRsIds: null,
            mostSevereConsequence: "3_prime_UTR_variant",
            traitAssociations: null,
            referenceAllele: Nucleotide.G);

        private static IReadOnlyList<Finding> Analyze(params MarkerCall[] calls)
        {
            var source = new RecordingAnnotationSource(Prothrombin());
            return new GenomeAnalyzer(source).Analyze(calls);
        }

        private static MarkerCall Call(string rsId, string genotype)
        {
            Genotype.TryParse(genotype, out var parsed);
            return new MarkerCall(rsId, Chromosome.Autosome(11), 46761055, parsed);
        }

        [Fact]
        public void ReferenceGenotype_DoesNotInheritTheVariantsClassification()
        {
            // GG is the ordinary genotype. Reporting it as pathogenic because
            // ClinVar classifies the A allele that way is a false alarm — and the
            // exact mistake this guard exists to prevent.
            var finding = Analyze(Call("rs1799963", "GG")).Single();

            Assert.Equal(FindingStatus.Determinate, finding.Status);
            Assert.False(finding.CarriesVariant);
            Assert.Null(finding.Clinical);
            Assert.False(finding.RequiresConfirmatoryTesting);
            Assert.Equal(0, finding.PriorityScore);
        }

        [Fact]
        public void ReferenceGenotype_StillExposesTheVariantsClassificationSeparately()
        {
            // "Tested and unremarkable" is worth reporting; it is a different
            // statement from "never looked at".
            var finding = Analyze(Call("rs1799963", "GG")).Single();

            Assert.NotNull(finding.ClinicalForVariant);
            Assert.Equal(ClinicalSignificance.Pathogenic, finding.ClinicalForVariant!.Significance);
        }

        [Fact]
        public void HeterozygousCarrier_GetsTheClassificationAndTheConfirmationWarning()
        {
            var finding = Analyze(Call("rs1799963", "GA")).Single();

            Assert.True(finding.CarriesVariant);
            Assert.Equal(ClinicalSignificance.Pathogenic, finding.Clinical!.Significance);
            Assert.True(finding.RequiresConfirmatoryTesting);
            Assert.True(finding.PriorityScore > 0);
        }

        [Fact]
        public void HomozygousVariant_IsAlsoACarrier()
        {
            var finding = Analyze(Call("rs1799963", "AA")).Single();

            Assert.True(finding.CarriesVariant);
            Assert.True(finding.RequiresConfirmatoryTesting);
        }

        [Fact]
        public void UnknownReferenceAllele_ReportsCannotTell_NotDoesNotCarry()
        {
            var withoutReference = new VariantAnnotation(
                "rs1", Strand.Plus, Strand.Unknown, null, null,
                SourceAttribution.Ensembl(), null,
                new ClinicalAnnotation(
                    ClinicalSignificance.Pathogenic, ClinVarReviewStatus.SingleSubmitter,
                    null, SourceAttribution.ClinVar()),
                null,
                new[] { Nucleotide.G, Nucleotide.A });

            var analyzer = new GenomeAnalyzer(new RecordingAnnotationSource(withoutReference));
            var finding = analyzer.Analyze(new[] { Call("rs1", "GG") }).Single();

            // Null, not false: silently treating "unknown" as "does not carry" would
            // hide a real finding.
            Assert.Null(finding.CarriesVariant);
            Assert.NotNull(finding.Clinical);
        }

        [Fact]
        public void NoCallIsNotApplicable_WithItsReason()
        {
            var call = new MarkerCall("rs1799963", Chromosome.Autosome(11), 46761055, null, "--");
            var finding = new GenomeAnalyzer(new RecordingAnnotationSource(Prothrombin()))
                .Analyze(new[] { call }).Single();

            Assert.Equal(FindingStatus.NotApplicable, finding.Status);
            Assert.Contains("no genotype", finding.Reason);
        }

        [Fact]
        public void ProviderInternalIdIsNotApplicable()
        {
            var call = new MarkerCall("i5000940", Chromosome.Autosome(1), 82154, new Genotype(Nucleotide.A, Nucleotide.A));
            var finding = new GenomeAnalyzer(new RecordingAnnotationSource()).Analyze(new[] { call }).Single();

            Assert.Equal(FindingStatus.NotApplicable, finding.Status);
            Assert.Contains("Provider-internal", finding.Reason);
        }

        [Fact]
        public void VariantOutsideTheDatabaseIsNotApplicable()
        {
            var finding = Analyze(Call("rs99999999", "AG")).Single();

            Assert.Equal(FindingStatus.NotApplicable, finding.Status);
            Assert.Contains("local variant database", finding.Reason);
        }

        [Fact]
        public void PalindromicVariantYieldsIndeterminate_WithItsReason()
        {
            var palindromic = new VariantAnnotation(
                "rs9939609", Strand.Plus, Strand.Unknown, null, null,
                SourceAttribution.Ensembl(), null, null, null,
                new[] { Nucleotide.T, Nucleotide.A },
                null, null, null, Nucleotide.T);

            var analyzer = new GenomeAnalyzer(new RecordingAnnotationSource(palindromic));
            var finding = analyzer.Analyze(new[] { Call("rs9939609", "AA") }).Single();

            Assert.Equal(FindingStatus.Indeterminate, finding.Status);
            Assert.Equal(StrandMatchOutcome.AmbiguousFlip, finding.StrandMatch!.Outcome);
            Assert.Contains("palindromic", finding.Reason);
        }

        [Fact]
        public void PrioritiseOrdersDeterminateFindingsByEvidenceStrength()
        {
            var findings = new[]
            {
                Analyze(Call("rs1799963", "GG")).Single(),
                Analyze(Call("rs1799963", "GA")).Single()
            };

            var ordered = GenomeAnalyzer.Prioritise(findings);

            Assert.True(ordered[0].PriorityScore > ordered[1].PriorityScore);
        }

        [Fact]
        public void SummaryCountsEveryCategory()
        {
            var findings = new[]
            {
                Analyze(Call("rs1799963", "GA")).Single(),
                Analyze(Call("rs99999999", "AG")).Single()
            };

            var summary = new AnalysisSummary(findings);

            Assert.Equal(2, summary.Total);
            Assert.Equal(1, summary.Determinate);
            Assert.Equal(1, summary.NotApplicable);
            Assert.Equal(1, summary.RequiringConfirmation);
        }
    }
}
