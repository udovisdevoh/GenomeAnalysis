using System.Linq;
using GenomeAnalysis.Annotations.Gwas;
using Xunit;

namespace GenomeAnalysis.Tests
{
    public class GwasCatalogTests
    {
        // Shape of a real associations response, trimmed to the fields used.
        private const string AssociationsJson = @"{
          ""_embedded"": {
            ""associations"": [
              {
                ""orPerCopyNum"": 1.54,
                ""betaNum"": null,
                ""betaUnit"": null,
                ""pvalue"": 8.0E-12,
                ""efoTraits"": [
                  { ""trait"": ""type 2 diabetes mellitus"", ""uri"": ""http://purl.obolibrary.org/obo/MONDO_0005148"" }
                ],
                ""loci"": [ { ""strongestRiskAlleles"": [ { ""riskAlleleName"": ""rs7903146-T"" } ] } ],
                ""study"": { ""publicationInfo"": { ""pubmedId"": ""17463249"" } }
              },
              {
                ""orPerCopyNum"": 1.02,
                ""pvalue"": 3.0E-3,
                ""efoTraits"": [ { ""trait"": ""body mass index"", ""uri"": null } ],
                ""loci"": [ { ""strongestRiskAlleles"": [ { ""riskAlleleName"": ""rs7903146-T"" } ] } ],
                ""study"": { ""publicationInfo"": { ""pubmedId"": ""20081858"" } }
              }
            ]
          }
        }";

        [Fact]
        public void ReadsEffectSizePValueAndCitation()
        {
            var associations = GwasCatalogClient.ParseAssociations(AssociationsJson, "rs7903146");

            var diabetes = associations.First(a => a.Trait.Contains("diabetes"));

            Assert.Equal(1.54, diabetes.OddsRatio);
            Assert.Equal(8.0E-12, diabetes.PValue);
            Assert.Equal("T", diabetes.RiskAllele);
            Assert.Equal("17463249", diabetes.PubMedId);
            Assert.Contains("MONDO_0005148", diabetes.TraitUri);
        }

        [Fact]
        public void StrongestEvidenceComesFirst()
        {
            // A caller that truncates the list must keep the part that matters.
            var associations = GwasCatalogClient.ParseAssociations(AssociationsJson, "rs7903146");

            Assert.Contains("diabetes", associations[0].Trait);
        }

        [Fact]
        public void GenomeWideSignificanceUsesTheConventionalThreshold()
        {
            var associations = GwasCatalogClient.ParseAssociations(AssociationsJson, "rs7903146");

            Assert.True(associations.First(a => a.Trait.Contains("diabetes")).IsGenomeWideSignificant);

            // p = 3e-3 is nowhere near 5e-8; presenting it beside a real hit would
            // misrepresent the evidence.
            Assert.False(associations.First(a => a.Trait.Contains("body mass")).IsGenomeWideSignificant);
        }

        [Fact]
        public void TinyOddsRatiosAreFlaggedAsNegligible()
        {
            var associations = GwasCatalogClient.ParseAssociations(AssociationsJson, "rs7903146");

            // OR 1.02 is routine GWAS noise at the individual level.
            Assert.True(associations.First(a => a.Trait.Contains("body mass")).IsNegligibleEffect);
            Assert.False(associations.First(a => a.Trait.Contains("diabetes")).IsNegligibleEffect);
        }

        [Fact]
        public void RiskAlleleIsStrippedOfItsRsIdPrefix()
        {
            var associations = GwasCatalogClient.ParseAssociations(AssociationsJson, "rs7903146");
            Assert.All(associations, a => Assert.Equal("T", a.RiskAllele));
        }

        [Fact]
        public void EmptyOrMalformedResponseYieldsNoAssociations()
        {
            Assert.Empty(GwasCatalogClient.ParseAssociations("{}", "rs1"));
            Assert.Empty(GwasCatalogClient.ParseAssociations("<html>404</html>", "rs1"));
            Assert.Empty(GwasCatalogClient.ParseAssociations("", "rs1"));
        }

        [Fact]
        public void AssociationsCarryAttributionWithADrillDownLink()
        {
            var associations = GwasCatalogClient.ParseAssociations(AssociationsJson, "rs7903146");

            Assert.Equal("GWAS Catalog (EBI/NHGRI)", associations[0].Attribution.SourceName);
            Assert.Contains("rs7903146", associations[0].Attribution.RecordUrl);
        }
    }
}
