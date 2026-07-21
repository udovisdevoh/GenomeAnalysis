using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GenomeAnalysis.Annotations.Local;
using GenomeAnalysis.Core.Analysis;
using GenomeAnalysis.Core.Annotations;
using GenomeAnalysis.Core.Genome;
using GenomeAnalysis.Core.Rules;
using GenomeAnalysis.Core.Strands;
using Xunit;

namespace GenomeAnalysis.Tests
{
    public class RuleEngineTests
    {
        private static readonly IReadOnlyList<RuleDefinition> Rules = RuleLoader.Parse(@"{
          ""rules"": [
            {
              ""id"": ""apoe-diplotype"", ""name"": ""Génotype APOE"", ""kind"": ""haplotype"", ""gene"": ""APOE"",
              ""requiredMarkers"": [""rs429358"", ""rs7412""],
              ""haplotypes"": [
                { ""name"": ""ε1"", ""alleles"": { ""rs429358"": ""C"", ""rs7412"": ""T"" } },
                { ""name"": ""ε2"", ""alleles"": { ""rs429358"": ""T"", ""rs7412"": ""T"" } },
                { ""name"": ""ε3"", ""alleles"": { ""rs429358"": ""T"", ""rs7412"": ""C"" } },
                { ""name"": ""ε4"", ""alleles"": { ""rs429358"": ""C"", ""rs7412"": ""C"" } }
              ],
              ""interpretations"": { ""ε3/ε3"": ""Diplotype le plus fréquent."" }
            },
            {
              ""id"": ""hfe"", ""name"": ""Variants HFE"", ""kind"": ""compoundHeterozygosity"", ""gene"": ""HFE"",
              ""requiredMarkers"": [""rs1800562"", ""rs1799945""],
              ""note"": ""Pénétrance faible."",
              ""variants"": [
                { ""rsId"": ""rs1800562"", ""riskAllele"": ""A"", ""label"": ""C282Y"" },
                { ""rsId"": ""rs1799945"", ""riskAllele"": ""G"", ""label"": ""H63D"" }
              ]
            }
          ]
        }");

        /// <summary>
        /// Builds a determinate finding carrying an already strand-resolved genotype,
        /// which is what the engine consumes.
        /// </summary>
        private static Finding Resolved(string rsId, string genotype)
        {
            Genotype.TryParse(genotype, out var parsed);

            var annotation = new VariantAnnotation(
                rsId, Strand.Plus, Strand.Unknown, null, null,
                SourceAttribution.Ensembl(rsId), null, null, null,
                parsed.Alleles.ToList());

            var call = new MarkerCall(rsId, Chromosome.Autosome(19), 1, parsed);
            var match = StrandResolver.Resolve(parsed, Strand.Plus, Strand.Plus, parsed.Alleles.ToList());

            return new Finding(call, FindingStatus.Determinate, "test", annotation, null, match);
        }

        private static RuleResult Evaluate(string ruleId, params Finding[] findings) =>
            new RuleEngine(Rules).Evaluate(findings).Single(r => r.RuleId == ruleId);

        [Fact]
        public void ApoeDerivesTheCommonDiplotypeFromTwoMarkers()
        {
            // Neither SNP carries the answer alone; the pair does.
            var result = Evaluate("apoe-diplotype", Resolved("rs429358", "TT"), Resolved("rs7412", "CC"));

            Assert.Equal(RuleOutcome.Determinate, result.Outcome);
            Assert.Equal("ε3/ε3", result.Conclusion);
            Assert.Contains("fréquent", result.Interpretation);
        }

        [Fact]
        public void ApoeDerivesAHeterozygousDiplotype()
        {
            var result = Evaluate("apoe-diplotype", Resolved("rs429358", "TC"), Resolved("rs7412", "CC"));

            Assert.Equal(RuleOutcome.Determinate, result.Outcome);
            Assert.Equal("ε3/ε4", result.Conclusion);
        }

        [Fact]
        public void ApoeHomozygousE4IsDerived()
        {
            var result = Evaluate("apoe-diplotype", Resolved("rs429358", "CC"), Resolved("rs7412", "CC"));

            Assert.Equal(RuleOutcome.Determinate, result.Outcome);
            Assert.Equal("ε4/ε4", result.Conclusion);
        }

        [Fact]
        public void ApoeDoubleHeterozygote_IsAmbiguousBecauseArrayDataIsUnphased()
        {
            // Heterozygous at both positions fits ε1/ε3 and ε2/ε4 equally well, and
            // nothing in unphased data separates them. The two readings carry
            // opposite meanings, so a guess here would be worse than no answer.
            var result = Evaluate("apoe-diplotype", Resolved("rs429358", "TC"), Resolved("rs7412", "CT"));

            Assert.Equal(RuleOutcome.Indeterminate, result.Outcome);
            Assert.Null(result.Conclusion);
            Assert.True(result.PhaseLimited);
            Assert.Equal(2, result.Candidates.Count);
            Assert.Contains("ε2/ε4", result.Candidates);
            Assert.Contains("ε1/ε3", result.Candidates);
            Assert.Contains("pas phasées", result.Reason);
        }

        [Fact]
        public void MissingRequiredMarkerYieldsIndeterminate_NotAnAssumption()
        {
            var result = Evaluate("apoe-diplotype", Resolved("rs429358", "TT"));

            Assert.Equal(RuleOutcome.Indeterminate, result.Outcome);
            Assert.Contains("rs7412", result.MissingMarkers);
            Assert.Contains("Aucune supposition", result.Reason);
        }

        [Fact]
        public void IndeterminateGenotypeIsNotUsedAsThoughItHadResolved()
        {
            // A finding that failed strand resolution must not feed a rule.
            var unresolved = new Finding(
                new MarkerCall("rs7412", Chromosome.Autosome(19), 1, new Genotype(Nucleotide.C, Nucleotide.C)),
                FindingStatus.Indeterminate,
                "flip ambigu");

            var result = Evaluate("apoe-diplotype", Resolved("rs429358", "TT"), unresolved);

            Assert.Equal(RuleOutcome.Indeterminate, result.Outcome);
            Assert.Contains("rs7412", result.MissingMarkers);
        }

        [Fact]
        public void NoMarkersAtAllIsNotApplicable()
        {
            var result = Evaluate("apoe-diplotype", Resolved("rs1800562", "GG"), Resolved("rs1799945", "CC"));

            Assert.Equal(RuleOutcome.NotApplicable, result.Outcome);
        }

        [Fact]
        public void GenotypesMatchingNoDefinedHaplotypeAreReportedAsSuch()
        {
            // Points at a strand, build or marker mismatch upstream — not at a rare
            // diplotype to be invented.
            var result = Evaluate("apoe-diplotype", Resolved("rs429358", "GG"), Resolved("rs7412", "CC"));

            Assert.Equal(RuleOutcome.Indeterminate, result.Outcome);
            Assert.Empty(result.Candidates);
            Assert.Contains("discordance", result.Reason);
        }

        [Fact]
        public void TwoDifferentHeterozygousVariants_AreConsistentWith_NeverConfirmed()
        {
            var result = Evaluate("hfe", Resolved("rs1800562", "GA"), Resolved("rs1799945", "CG"));

            Assert.Equal(RuleOutcome.Determinate, result.Outcome);
            Assert.True(result.PhaseLimited);
            Assert.Contains("compatible avec", result.Conclusion);
            Assert.Contains("copies opposées", result.Reason);
            Assert.DoesNotContain("confirmé", result.Reason);
        }

        [Fact]
        public void HomozygousVariantNeedsNoPhaseInformation()
        {
            var result = Evaluate("hfe", Resolved("rs1800562", "AA"), Resolved("rs1799945", "CC"));

            Assert.Equal(RuleOutcome.Determinate, result.Outcome);
            Assert.False(result.PhaseLimited);
            Assert.Contains("homozygote", result.Conclusion);
        }

        [Fact]
        public void SingleHeterozygousVariantLeavesTheOtherCopyIntact()
        {
            var result = Evaluate("hfe", Resolved("rs1800562", "GA"), Resolved("rs1799945", "CC"));

            Assert.Equal(RuleOutcome.Determinate, result.Outcome);
            Assert.False(result.PhaseLimited);
            Assert.Contains("hétérozygote", result.Conclusion);
        }

        [Fact]
        public void CarryingNeitherVariantIsStatedExplicitly()
        {
            var result = Evaluate("hfe", Resolved("rs1800562", "GG"), Resolved("rs1799945", "CC"));

            Assert.Equal(RuleOutcome.Determinate, result.Outcome);
            Assert.Contains("aucun variant", result.Conclusion);
            Assert.Contains("Pénétrance", result.Interpretation);
        }
    }

    public class RuleLoaderTests
    {
        [Fact]
        public void LoadsTheShippedRuleFile()
        {
            var path = Path.Combine(FindRepositoryRoot(), "data", "rules.json");
            Assert.True(File.Exists(path), "data/rules.json is missing from the repository.");

            var rules = RuleLoader.Load(path);

            Assert.NotEmpty(rules);
            Assert.Contains(rules, r => r.Id == "apoe-diplotype" && r.Kind == RuleKind.Haplotype);
            Assert.Contains(rules, r => r.Kind == RuleKind.CompoundHeterozygosity);
        }

        [Fact]
        public void UnknownRuleKindIsSkipped_NotGuessedAt()
        {
            var rules = RuleLoader.Parse(@"{""rules"":[
                {""id"":""x"",""name"":""X"",""kind"":""polygenicScore"",""requiredMarkers"":[""rs1""]}]}");

            Assert.Empty(rules);
        }

        [Fact]
        public void MalformedFileYieldsNoRules_NotACrash()
        {
            Assert.Empty(RuleLoader.Parse("{ not json"));
            Assert.Empty(RuleLoader.Parse(string.Empty));
        }

        [Fact]
        public void RuleWithoutRequiredMarkersIsRejected()
        {
            var rules = RuleLoader.Parse(@"{""rules"":[{""id"":""x"",""name"":""X"",""kind"":""haplotype""}]}");
            Assert.Empty(rules);
        }

        private static string FindRepositoryRoot()
        {
            var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);

            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "GenomeAnalysis.sln")))
            {
                directory = directory.Parent;
            }

            return directory?.FullName ?? Directory.GetCurrentDirectory();
        }
    }
}
