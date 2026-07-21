using System.Collections.Generic;
using System.Linq;
using GenomeAnalysis.Annotations.Ensembl;
using GenomeAnalysis.Annotations.Local;
using GenomeAnalysis.Core.Annotations;
using GenomeAnalysis.Core.Genome;
using GenomeAnalysis.Core.Strands;
using Xunit;

namespace GenomeAnalysis.Tests
{
    public class EnsemblClientTests
    {
        // Shape of a real POST /variation/homo_sapiens response, trimmed.
        private const string BatchJson = @"{
          ""rs7412"": {
            ""name"": ""rs7412"",
            ""most_severe_consequence"": ""missense_variant"",
            ""MAF"": 0.0757,
            ""synonyms"": [""rs3200542"", ""RCV000019452"", ""VCV000017848"", ""VAR_000664""],
            ""mappings"": [
              { ""assembly_name"": ""GRCh37"", ""allele_string"": ""C/T"", ""strand"": 1 },
              { ""assembly_name"": ""GRCh38"", ""allele_string"": ""C/T"", ""strand"": 1 }
            ]
          },
          ""rs9939609"": {
            ""name"": ""rs9939609"",
            ""mappings"": [ { ""assembly_name"": ""GRCh38"", ""allele_string"": ""T/A"", ""strand"": -1 } ]
          }
        }";

        [Fact]
        public void ReadsAlleleSetFromGrch38Mapping()
        {
            var results = EnsemblClient.ParseBatchResponse(BatchJson).ToDictionary(p => p.Key, p => p.Value);

            Assert.Equal(2, results["rs7412"].KnownAlleles.Count);
            Assert.Contains(Nucleotide.C, results["rs7412"].KnownAlleles);
            Assert.Contains(Nucleotide.T, results["rs7412"].KnownAlleles);
        }

        [Fact]
        public void ReadsStrandAndConsequence()
        {
            var results = EnsemblClient.ParseBatchResponse(BatchJson).ToDictionary(p => p.Key, p => p.Value);

            Assert.Equal(Strand.Plus, results["rs7412"].Orientation);
            Assert.Equal(Strand.Minus, results["rs9939609"].Orientation);
            Assert.Equal("missense_variant", results["rs7412"].MostSevereConsequence);
        }

        [Fact]
        public void ExtractsOnlyRsIdsFromSynonyms()
        {
            // Ensembl mixes ClinVar RCV/VCV and UniProt VAR_ identifiers into the
            // same list; only rsIDs are usable for merge resolution.
            var results = EnsemblClient.ParseBatchResponse(BatchJson).ToDictionary(p => p.Key, p => p.Value);

            Assert.Equal(new[] { "rs3200542" }, results["rs7412"].MergedRsIds);
        }

        [Fact]
        public void DetectsPalindromicVariantFromRealAlleleData()
        {
            // rs9939609 (FTO) is a genuine T/A variant, so a homozygous call on it
            // can never be strand-resolved.
            var results = EnsemblClient.ParseBatchResponse(BatchJson).ToDictionary(p => p.Key, p => p.Value);

            Assert.True(results["rs9939609"].IsPalindromic);
            Assert.False(results["rs7412"].IsPalindromic);
        }

        [Theory]
        [InlineData("A/G", 2)]
        [InlineData("C/T/G", 3)]
        [InlineData("-/AT", 0)]       // indel: not a substitution
        [InlineData("A/ATTG", 1)]     // keeps the single-nucleotide allele only
        [InlineData("", 0)]
        [InlineData(null, 0)]
        public void ParseAlleleString_KeepsOnlySingleNucleotides(string? input, int expected)
        {
            Assert.Equal(expected, EnsemblClient.ParseAlleleString(input).Count);
        }
    }

    public class AnnotationMergeTests
    {
        private static VariantAnnotation Reference() => new VariantAnnotation(
            "rs1800562", Strand.Plus, Strand.Unknown, null, null,
            SourceAttribution.Ensembl("rs1800562"), null, null, null,
            new[] { Nucleotide.G, Nucleotide.A }, new[] { "rs111535158" }, "missense_variant");

        private static VariantAnnotation Clinical() => new VariantAnnotation(
            "rs1800562", Strand.Unknown, Strand.Unknown, null, null,
            SourceAttribution.MyVariant("rs1800562"), "HFE",
            new ClinicalAnnotation(
                ClinicalSignificance.Pathogenic,
                ClinVarReviewStatus.MultipleSubmittersNoConflicts,
                new[] { "Hemochromatosis type 1" },
                SourceAttribution.ClinVar("9"), "9"),
            0.038);

        [Fact]
        public void CombinesReferenceDataWithClinicalData()
        {
            var merged = AnnotationMerge.Combine(new[] { Reference(), Clinical() });

            Assert.NotNull(merged);
            Assert.Equal(Strand.Plus, merged!.Orientation);
            Assert.Equal(2, merged.KnownAlleles.Count);
            Assert.Equal("HFE", merged.GeneSymbol);
            Assert.Equal(ClinicalSignificance.Pathogenic, merged.Clinical!.Significance);
            Assert.Equal(0.038, merged.MinorAlleleFrequency);
            Assert.Contains("rs111535158", merged.MergedRsIds);
        }

        [Fact]
        public void UnknownStrandNeverOverwritesAKnownOne()
        {
            var merged = AnnotationMerge.Combine(new[] { Clinical(), Reference() });
            Assert.Equal(Strand.Plus, merged!.Orientation);
        }

        [Fact]
        public void ClinicalRecordWithBetterReviewWins()
        {
            var weak = new VariantAnnotation(
                "rs1", Strand.Plus, Strand.Unknown, null, null, SourceAttribution.MyVariant(), null,
                new ClinicalAnnotation(
                    ClinicalSignificance.Pathogenic, ClinVarReviewStatus.NoAssertionCriteria,
                    null, SourceAttribution.ClinVar()));

            var strong = new VariantAnnotation(
                "rs1", Strand.Plus, Strand.Unknown, null, null, SourceAttribution.MyVariant(), null,
                new ClinicalAnnotation(
                    ClinicalSignificance.Benign, ClinVarReviewStatus.ExpertPanel,
                    null, SourceAttribution.ClinVar()));

            var merged = AnnotationMerge.Combine(new[] { weak, strong });

            // A four-star benign call outweighs an unreviewed pathogenic one.
            // Choosing by alarm level instead would systematically overstate.
            Assert.Equal(ClinicalSignificance.Benign, merged!.Clinical!.Significance);
        }

        [Fact]
        public void ReturnsNullWhenNothingToCombine()
        {
            Assert.Null(AnnotationMerge.Combine(new VariantAnnotation?[] { null, null }));
        }

        [Fact]
        public void FrequencyAboveOneHalf_IsReadAsTheComplementaryAllele()
        {
            // Real case: Ensembl reports MAF 0.98274 for rs6025 (factor V Leiden),
            // whose risk allele is near 2% in Europeans. Left alone, a rare
            // pathogenic variant would be flagged common and played down.
            var annotation = new VariantAnnotation(
                "rs6025", Strand.Plus, Strand.Unknown, null, null,
                SourceAttribution.Ensembl(), null, null, 0.98274);

            var merged = AnnotationMerge.Combine(new[] { annotation, annotation });

            Assert.Equal(0.01726, merged!.MinorAlleleFrequency!.Value, 5);
            Assert.False(merged.IsCommon);
        }

        [Fact]
        public void ImpossibleFrequency_IsDroppedRatherThanShown()
        {
            var annotation = new VariantAnnotation(
                "rs1", Strand.Plus, Strand.Unknown, null, null,
                SourceAttribution.Ensembl(), null, null, 42.0);

            var merged = AnnotationMerge.Combine(new[] { annotation, annotation });

            Assert.Null(merged!.MinorAlleleFrequency);
        }
    }

    public class VariantDatabaseTests
    {
        private const string DatabaseJson = @"{
          ""schemaVersion"": 1,
          ""generatedAt"": ""2026-07-20T00:00:00+00:00"",
          ""sources"": [ { ""name"": ""Ensembl"", ""licence"": ""open data"" } ],
          ""variants"": {
            ""rs1800562"": {
              ""v"": 2, ""rsId"": ""rs1800562"", ""orientation"": ""Plus"",
              ""stabilizedOrientation"": ""Unknown"", ""alleles"": ""G/A"",
              ""mergedRsIds"": [ ""rs111535158"" ], ""gene"": ""HFE"",
              ""attribution"": { ""source"": ""Ensembl"", ""licence"": ""open data"" },
              ""genotypes"": []
            }
          }
        }";

        [Fact]
        public void LoadsVariantsAndProvenance()
        {
            var database = VariantDatabase.Parse(DatabaseJson);

            Assert.Equal(1, database.Count);
            Assert.Contains("Ensembl", database.SourceNames);
            Assert.NotNull(database.GeneratedAt);
        }

        [Fact]
        public void ResolvesAMergedRsIdToItsCurrentRecord()
        {
            // A file from 2013 may carry the withdrawn identifier. Without this the
            // variant simply comes back unknown.
            var database = VariantDatabase.Parse(DatabaseJson);

            var found = database.Find("rs111535158");

            Assert.NotNull(found);
            Assert.Equal("rs1800562", found!.RsId);
        }

        [Fact]
        public void UnknownVariantReturnsNullRatherThanThrowing()
        {
            var database = VariantDatabase.Parse(DatabaseJson);

            Assert.Null(database.Find("rs99999999"));
            Assert.Null(database.Find("i5000940"));
            Assert.Null(database.Find(null));
        }

        [Fact]
        public void MalformedFileYieldsAnEmptyDatabase_NotACrash()
        {
            Assert.Equal(0, VariantDatabase.Parse("{ not json").Count);
            Assert.Equal(0, VariantDatabase.Parse("").Count);
        }

        [Fact]
        public void SuppliesAlleleSetThatMakesStrandResolutionWork()
        {
            // The point of storing the allele set: without it StrandResolver has to
            // refuse every lookup, because it cannot rule out a palindrome.
            var database = VariantDatabase.Parse(DatabaseJson);
            var variant = database.Find("rs1800562")!;

            Genotype.TryParse("GA", out var observed);

            var match = StrandResolver.Resolve(
                observed, Strand.Plus, variant.Orientation, variant.KnownAlleles);

            Assert.Equal(StrandMatchOutcome.Resolved, match.Outcome);
        }
    }
}
