using System.Collections.Generic;
using GenomeAnalysis.Annotations.Snpedia;
using GenomeAnalysis.Core.Genome;
using Xunit;

namespace GenomeAnalysis.Tests
{
    public class SnpediaPageNameTests
    {
        [Theory]
        [InlineData("rs53576", "Rs53576")]
        [InlineData("RS53576", "Rs53576")]
        [InlineData("Rs53576", "Rs53576")]
        public void SnpPageTitle_UsesMediaWikiCapitalisation(string rsId, string expected)
        {
            Assert.Equal(expected, SnpediaPageNames.ForSnp(rsId));
        }

        [Fact]
        public void GenotypePageTitle_AppendsAllelesInParentheses()
        {
            Genotype.TryParse("AG", out var genotype);
            Assert.Equal("Rs53576(A;G)", SnpediaPageNames.ForGenotype("rs53576", genotype));
        }

        [Fact]
        public void ProviderInternalIdentifier_HasNoSnpediaPage()
        {
            // 23andMe's i-prefixed markers exist only in their own data.
            Assert.False(SnpediaPageNames.IsSnpPageTitle("i5000940"));
            Assert.Throws<System.ArgumentException>(() => SnpediaPageNames.ForSnp("i5000940"));
        }

        [Fact]
        public void GenotypePageTitle_ParsesBackIntoParts()
        {
            Assert.True(SnpediaPageNames.TryParseGenotypePage("Rs53576(A;G)", out var rsId, out var genotype));

            Assert.Equal("rs53576", rsId);
            Assert.Equal(new Genotype(Nucleotide.A, Nucleotide.G), genotype);
        }

        [Fact]
        public void IndelGenotypePage_IsRejectedRatherThanMisparsed()
        {
            // (I;D) is an insertion/deletion genotype this tool does not interpret.
            Assert.False(SnpediaPageNames.TryParseGenotypePage("Rs1799752(I;D)", out _, out _));
        }
    }

    public class SemanticDataTests
    {
        // Shape of a browsebysubject response, trimmed to the properties used.
        private const string SnpPageJson = @"{
          ""query"": {
            ""subject"": ""Rs53576#0##"",
            ""data"": [
              { ""property"": ""Orientation"",           ""dataitem"": [ { ""type"": 2, ""item"": ""plus"" } ] },
              { ""property"": ""StabilizedOrientation"",  ""dataitem"": [ { ""type"": 2, ""item"": ""minus"" } ] },
              { ""property"": ""Gene"",                   ""dataitem"": [ { ""type"": 2, ""item"": ""OXTR"" } ] },
              { ""property"": ""Geno1"",                  ""dataitem"": [ { ""type"": 9, ""item"": ""Rs53576(A;A)#0##"" } ] },
              { ""property"": ""Geno2"",                  ""dataitem"": [ { ""type"": 9, ""item"": ""Rs53576(A;G)#0##"" } ] },
              { ""property"": ""Geno3"",                  ""dataitem"": [ { ""type"": 9, ""item"": ""Rs53576(G;G)#0##"" } ] }
            ]
          }
        }";

        [Fact]
        public void Parse_ReadsPropertiesAndStripsSmwSuffixes()
        {
            var data = SemanticData.Parse(SnpPageJson);

            Assert.Equal("Rs53576", data.Subject);
            Assert.Equal("plus", data.GetString("Orientation"));
            Assert.Equal("minus", data.GetString("StabilizedOrientation"));
            Assert.Equal("OXTR", data.GetString("Gene"));
        }

        [Fact]
        public void Parse_MatchesPropertyNamesCaseInsensitively()
        {
            var data = SemanticData.Parse(SnpPageJson);
            Assert.Equal("plus", data.GetString("orientation"));
        }

        [Fact]
        public void Parse_ReturnsEmptyForMalformedJson_RatherThanThrowing()
        {
            Assert.True(SemanticData.Parse("not json at all").IsEmpty);
            Assert.True(SemanticData.Parse("").IsEmpty);
        }

        [Fact]
        public void DiscoverGenotypePages_FindsThemByTitleShape()
        {
            var titles = SnpediaMapper.DiscoverGenotypePageTitles(SemanticData.Parse(SnpPageJson));

            Assert.Equal(3, titles.Count);
            Assert.Contains("Rs53576(A;A)", titles);
            Assert.Contains("Rs53576(G;G)", titles);
        }
    }

    public class SnpediaMapperTests
    {
        [Theory]
        [InlineData("plus", Strand.Plus)]
        [InlineData("minus", Strand.Minus)]
        [InlineData("PLUS", Strand.Plus)]
        [InlineData("", Strand.Unknown)]
        [InlineData(null, Strand.Unknown)]
        [InlineData("sideways", Strand.Unknown)]
        public void ParseStrand_NeverDefaultsToPlusOnUnknownInput(string? value, Strand expected)
        {
            // Defaulting an unreadable orientation to plus is exactly how a
            // silently-wrong association gets produced.
            Assert.Equal(expected, SnpediaMapper.ParseStrand(value));
        }

        [Fact]
        public void MapSnpPage_CarriesStabilizedOrientationSeparatelyFromOrientation()
        {
            var snpPage = SemanticData.Parse(@"{""query"":{""subject"":""Rs1801133#0##"",""data"":[
                { ""property"": ""Orientation"",          ""dataitem"": [ { ""type"": 2, ""item"": ""plus"" } ] },
                { ""property"": ""StabilizedOrientation"", ""dataitem"": [ { ""type"": 2, ""item"": ""minus"" } ] }
            ]}}");

            var annotation = SnpediaMapper.MapSnpPage(
                "rs1801133",
                snpPage,
                new List<KeyValuePair<string, SemanticData>>());

            Assert.Equal(Strand.Plus, annotation.Orientation);
            Assert.Equal(Strand.Minus, annotation.StabilizedOrientation);
        }

        [Fact]
        public void MapGenotypePage_ReadsMagnitudeAndRepute()
        {
            var page = SemanticData.Parse(@"{""query"":{""subject"":""Rs53576(A;A)#0##"",""data"":[
                { ""property"": ""Magnitude"", ""dataitem"": [ { ""type"": 1, ""item"": ""2.5"" } ] },
                { ""property"": ""Repute"",    ""dataitem"": [ { ""type"": 2, ""item"": ""Bad"" } ] },
                { ""property"": ""Summary"",   ""dataitem"": [ { ""type"": 2, ""item"": ""Lower empathy"" } ] }
            ]}}");

            var mapped = SnpediaMapper.MapGenotypePage("Rs53576(A;A)", page);

            Assert.NotNull(mapped);
            Assert.Equal(2.5, mapped!.Magnitude);
            Assert.Equal(GenomeAnalysis.Core.Annotations.Repute.Bad, mapped.Repute);
            Assert.Equal("Lower empathy", mapped.Summary);
            Assert.Equal("SNPedia", mapped.Attribution.SourceName);
        }

        [Fact]
        public void MapGenotypePage_SkipsIndelGenotypes()
        {
            Assert.Null(SnpediaMapper.MapGenotypePage("Rs1799752(I;D)", SemanticData.Parse("{}")));
        }

        [Fact]
        public void MappedAnnotation_AlwaysCarriesAttribution()
        {
            // SNPedia's licence makes attribution a condition of use, so an
            // annotation must never arrive without it.
            var annotation = SnpediaMapper.MapSnpPage(
                "rs53576",
                SemanticData.Parse(@"{""query"":{""subject"":""Rs53576#0##"",""data"":[]}}"),
                new List<KeyValuePair<string, SemanticData>>());

            Assert.Equal("SNPedia", annotation.Attribution.SourceName);
            Assert.Contains("CC BY-NC-SA", annotation.Attribution.Licence);
            Assert.Contains("Rs53576", annotation.Attribution.RecordUrl);
        }
    }
}
