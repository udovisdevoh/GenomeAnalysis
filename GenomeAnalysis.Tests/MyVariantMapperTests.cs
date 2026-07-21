using System.Linq;
using GenomeAnalysis.Annotations.MyVariant;
using GenomeAnalysis.Core.Annotations;
using Xunit;

namespace GenomeAnalysis.Tests
{
    public class MyVariantMapperTests
    {
        [Fact]
        public void MapsClinicalSignificanceAndReviewStatus()
        {
            // HFE C282Y, the hemochromatosis variant.
            const string json = @"[{
              ""query"": ""rs1800562"",
              ""dbsnp"": { ""rsid"": ""rs1800562"" },
              ""clinvar"": {
                ""variant_id"": 9,
                ""gene"": { ""symbol"": ""HFE"" },
                ""rcv"": {
                  ""clinical_significance"": ""Pathogenic"",
                  ""review_status"": ""criteria provided, multiple submitters, no conflicts"",
                  ""conditions"": { ""name"": ""Hemochromatosis type 1"" }
                }
              },
              ""gnomad_genome"": { ""af"": { ""af"": 0.038 } }
            }]";

            var annotation = MyVariantClient.ParseBatchResponse(json).Single().Value;

            Assert.Equal("rs1800562", annotation.RsId);
            Assert.Equal("HFE", annotation.GeneSymbol);
            Assert.NotNull(annotation.Clinical);
            Assert.Equal(ClinicalSignificance.Pathogenic, annotation.Clinical!.Significance);
            Assert.Equal(ClinVarReviewStatus.MultipleSubmittersNoConflicts, annotation.Clinical.ReviewStatus);
            Assert.Equal(2, annotation.Clinical.ReviewStatus.ToStarRating());
            Assert.Contains("Hemochromatosis type 1", annotation.Clinical.Conditions);
            Assert.True(annotation.Clinical.RequiresConfirmatoryTesting);
            Assert.Equal(0.038, annotation.MinorAlleleFrequency);
        }

        [Fact]
        public void StabilizedOrientationStaysUnknown_BecauseMyVariantDoesNotReportIt()
        {
            // Only SNPedia has the stabilized notion. Inventing a value here would
            // let a caller resolve a strand it has no basis to resolve.
            const string json = @"[{ ""query"": ""rs53576"", ""dbsnp"": { ""rsid"": ""rs53576"" } }]";

            var annotation = MyVariantClient.ParseBatchResponse(json).Single().Value;

            Assert.Equal(GenomeAnalysis.Core.Genome.Strand.Unknown, annotation.StabilizedOrientation);
        }

        [Fact]
        public void SingleRcvObjectAndRcvArray_AreBothAccepted()
        {
            const string arrayForm = @"[{
              ""query"": ""rs429358"",
              ""dbsnp"": { ""rsid"": ""rs429358"" },
              ""clinvar"": { ""rcv"": [
                { ""clinical_significance"": ""risk factor"", ""review_status"": ""criteria provided, single submitter"",
                  ""conditions"": { ""name"": ""Alzheimer disease"" } },
                { ""clinical_significance"": ""risk factor"", ""review_status"": ""no assertion criteria provided"",
                  ""conditions"": { ""name"": ""Hyperlipoproteinemia"" } }
              ] }
            }]";

            var annotation = MyVariantClient.ParseBatchResponse(arrayForm).Single().Value;

            Assert.Equal(ClinicalSignificance.RiskFactor, annotation.Clinical!.Significance);
            Assert.Equal(2, annotation.Clinical.Conditions.Count);
        }

        [Fact]
        public void ReviewStatus_FollowsTheBestReviewedSubmission()
        {
            // Reporting the weakest link instead would sink any well-established
            // variant: one submission out of forty lacking criteria would drag an
            // expert-panel classification down to zero stars.
            const string json = @"[{
              ""query"": ""rs1"",
              ""dbsnp"": { ""rsid"": ""rs1"" },
              ""clinvar"": { ""rcv"": [
                { ""clinical_significance"": ""Pathogenic"", ""review_status"": ""reviewed by expert panel"" },
                { ""clinical_significance"": ""Pathogenic"", ""review_status"": ""no assertion criteria provided"" }
              ] }
            }]";

            var annotation = MyVariantClient.ParseBatchResponse(json).Single().Value;

            Assert.Equal(ClinVarReviewStatus.ExpertPanel, annotation.Clinical!.ReviewStatus);
            Assert.Equal(3, annotation.Clinical.ReviewStatus.ToStarRating());
        }

        [Fact]
        public void ManySubmissions_DoNotLetParallelLabelsOutrankAPathogenicCall()
        {
            // Regression from real data: HFE C282Y (rs1800562) carries dozens of
            // submissions spanning Pathogenic, risk factor, uncertain and other.
            // Ranking by the enum's declaration order returned "Other", because
            // Other is declared above Pathogenic — the variant came out looking
            // unclassified.
            const string json = @"[{
              ""query"": ""rs1800562"",
              ""dbsnp"": { ""rsid"": ""rs1800562"" },
              ""clinvar"": { ""rcv"": [
                { ""clinical_significance"": ""Pathogenic"",             ""review_status"": ""criteria provided, single submitter"" },
                { ""clinical_significance"": ""risk factor"",            ""review_status"": ""criteria provided, single submitter"" },
                { ""clinical_significance"": ""other"",                  ""review_status"": ""criteria provided, single submitter"" },
                { ""clinical_significance"": ""Uncertain significance"", ""review_status"": ""criteria provided, single submitter"" }
              ] }
            }]";

            var annotation = MyVariantClient.ParseBatchResponse(json).Single().Value;

            Assert.Equal(ClinicalSignificance.Pathogenic, annotation.Clinical!.Significance);
            Assert.True(annotation.Clinical.RequiresConfirmatoryTesting);
        }

        [Fact]
        public void ParallelLabelsSurvive_WhenNothingSitsOnThePathogenicityAxis()
        {
            const string json = @"[{
              ""query"": ""rs4149056"", ""dbsnp"": { ""rsid"": ""rs4149056"" },
              ""clinvar"": { ""rcv"": [
                { ""clinical_significance"": ""drug response"", ""review_status"": ""criteria provided, single submitter"" }
              ] }
            }]";

            var annotation = MyVariantClient.ParseBatchResponse(json).Single().Value;

            Assert.Equal(ClinicalSignificance.DrugResponse, annotation.Clinical!.Significance);
            Assert.False(annotation.Clinical.RequiresConfirmatoryTesting);
        }

        [Fact]
        public void DisagreeingSubmissions_AreReportedAsConflict_NotResolved()
        {
            const string json = @"[{
              ""query"": ""rs2"",
              ""dbsnp"": { ""rsid"": ""rs2"" },
              ""clinvar"": { ""rcv"": [
                { ""clinical_significance"": ""Pathogenic"", ""review_status"": ""criteria provided, single submitter"" },
                { ""clinical_significance"": ""Benign"",     ""review_status"": ""criteria provided, single submitter"" }
              ] }
            }]";

            var annotation = MyVariantClient.ParseBatchResponse(json).Single().Value;

            Assert.Equal(ClinicalSignificance.ConflictingInterpretations, annotation.Clinical!.Significance);
        }

        [Fact]
        public void NotFoundEntries_AreSkippedRatherThanFailing()
        {
            // Withdrawn or provider-internal identifiers come back notfound. That
            // is an ordinary outcome, not an error.
            const string json = @"[
              { ""query"": ""i5000940"", ""notfound"": true },
              { ""query"": ""rs53576"", ""dbsnp"": { ""rsid"": ""rs53576"" } }
            ]";

            var results = MyVariantClient.ParseBatchResponse(json).ToList();

            Assert.Single(results);
            Assert.Equal("rs53576", results[0].Key);
        }

        [Fact]
        public void MalformedResponse_YieldsNothingRatherThanThrowing()
        {
            Assert.Empty(MyVariantClient.ParseBatchResponse("<html>gateway timeout</html>"));
            Assert.Empty(MyVariantClient.ParseBatchResponse(""));
        }

        [Theory]
        [InlineData("Pathogenic", ClinicalSignificance.Pathogenic)]
        [InlineData("Likely pathogenic", ClinicalSignificance.LikelyPathogenic)]
        [InlineData("Uncertain significance", ClinicalSignificance.UncertainSignificance)]
        [InlineData("Benign", ClinicalSignificance.Benign)]
        [InlineData("Likely benign", ClinicalSignificance.LikelyBenign)]
        [InlineData("Conflicting interpretations of pathogenicity", ClinicalSignificance.ConflictingInterpretations)]
        [InlineData("drug response", ClinicalSignificance.DrugResponse)]
        [InlineData("", ClinicalSignificance.NotProvided)]
        public void ParseSignificance_CoversClinVarVocabulary(string raw, ClinicalSignificance expected)
        {
            Assert.Equal(expected, MyVariantMapper.ParseSignificance(raw));
        }

        [Theory]
        [InlineData("practice guideline", 4)]
        [InlineData("reviewed by expert panel", 3)]
        [InlineData("criteria provided, multiple submitters, no conflicts", 2)]
        [InlineData("criteria provided, single submitter", 1)]
        [InlineData("criteria provided, conflicting interpretations", 1)]
        [InlineData("no assertion criteria provided", 0)]
        public void ParseReviewStatus_MapsToClinVarStarRating(string raw, int expectedStars)
        {
            Assert.Equal(expectedStars, MyVariantMapper.ParseReviewStatus(raw).ToStarRating());
        }

        [Fact]
        public void CommonVariant_IsFlaggedSoItIsNotPresentedAsADiscovery()
        {
            const string json = @"[{
              ""query"": ""rs4988235"", ""dbsnp"": { ""rsid"": ""rs4988235"" },
              ""gnomad_genome"": { ""af"": { ""af"": 0.41 } }
            }]";

            var annotation = MyVariantClient.ParseBatchResponse(json).Single().Value;

            Assert.True(annotation.IsCommon);
        }
    }
}
