using System.Collections.Generic;
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
    public class PharmacogenomicsEngineTests
    {
        /// <summary>
        /// A simplified CYP2C19: *1 normal, *2 no function, *17 increased, each
        /// defined by one position.
        /// </summary>
        private static Pharmacogene Cyp2C19(bool includeUntestablePositions = false)
        {
            var alleles = new List<StarAllele>
            {
                new StarAllele("*1", "Normal function", new Dictionary<string, char>
                {
                    { "rs4244285", 'G' }, { "rs12248560", 'C' }
                }),
                new StarAllele("*2", "No function", new Dictionary<string, char>
                {
                    { "rs4244285", 'A' }, { "rs12248560", 'C' }
                }),
                new StarAllele("*17", "Increased function", new Dictionary<string, char>
                {
                    { "rs4244285", 'G' }, { "rs12248560", 'T' }
                })
            };

            if (includeUntestablePositions)
            {
                alleles.Add(new StarAllele("*3", "No function", new Dictionary<string, char>
                {
                    { "rs4986893", 'A' }
                }));
            }

            var rules = new List<(string, string, string)>
            {
                ("Normal function", "Normal function", "Normal Metabolizer"),
                ("No function", "Normal function", "Intermediate Metabolizer"),
                ("No function", "No function", "Poor Metabolizer"),
                ("Increased function", "Normal function", "Rapid Metabolizer")
            };

            return new Pharmacogene("CYP2C19", alleles, rules);
        }

        private static Finding Resolved(string rsId, string genotype)
        {
            Genotype.TryParse(genotype, out var parsed);

            var annotation = new VariantAnnotation(
                rsId, Strand.Plus, Strand.Unknown, null, null,
                SourceAttribution.Ensembl(rsId), null, null, null, parsed.Alleles.ToList());

            var call = new MarkerCall(rsId, Chromosome.Autosome(10), 1, parsed);
            var match = StrandResolver.Resolve(parsed, Strand.Plus, Strand.Plus, parsed.Alleles.ToList());

            return new Finding(call, FindingStatus.Determinate, "test", annotation, null, match);
        }

        private static RuleResult Evaluate(Pharmacogene gene, params Finding[] findings) =>
            new PharmacogenomicsEngine(new[] { gene }).Evaluate(findings).Single();

        [Fact]
        public void CallsADiplotypeAndItsPhenotypeWhenPositionsAreCovered()
        {
            // Homozygous reference at both positions: *1/*1, normal metaboliser.
            var result = Evaluate(Cyp2C19(), Resolved("rs4244285", "GG"), Resolved("rs12248560", "CC"));

            Assert.Equal(RuleOutcome.Determinate, result.Outcome);
            Assert.Contains("*1/*1", result.Conclusion);
            Assert.Contains("Normal Metabolizer", result.Conclusion);
        }

        [Fact]
        public void CallsALossOfFunctionHeterozygote()
        {
            var result = Evaluate(Cyp2C19(), Resolved("rs4244285", "GA"), Resolved("rs12248560", "CC"));

            Assert.Equal(RuleOutcome.Determinate, result.Outcome);
            Assert.Contains("*1/*2", result.Conclusion);
            Assert.Contains("Intermediate Metabolizer", result.Conclusion);
        }

        [Fact]
        public void CallsAPoorMetaboliser()
        {
            var result = Evaluate(Cyp2C19(), Resolved("rs4244285", "AA"), Resolved("rs12248560", "CC"));

            Assert.Equal(RuleOutcome.Determinate, result.Outcome);
            Assert.Contains("*2/*2", result.Conclusion);
            Assert.Contains("Poor Metabolizer", result.Conclusion);
        }

        [Fact]
        public void UntestedAllelesAreReportedAsNotExcluded()
        {
            // The decisive caveat: an untested no-function allele can hide behind an
            // apparently normal one and change the phenotype entirely.
            var result = Evaluate(
                Cyp2C19(includeUntestablePositions: true),
                Resolved("rs4244285", "GG"),
                Resolved("rs12248560", "CC"));

            Assert.Equal(RuleOutcome.Determinate, result.Outcome);
            Assert.Contains("*1/*1", result.Conclusion);
            Assert.NotNull(result.Interpretation);
            Assert.Contains("pas exclus", result.Interpretation);
            Assert.Contains("test pharmacogénomique dédié", result.Interpretation, System.StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void NoAssessableAlleleYieldsIndeterminate_NotAGuess()
        {
            // One of the two defining positions is missing, so no allele can be
            // assessed and nothing is called.
            var result = Evaluate(Cyp2C19(), Resolved("rs4244285", "GG"));

            Assert.Equal(RuleOutcome.Indeterminate, result.Outcome);
            Assert.Null(result.Conclusion);
            Assert.Contains("positions définitoires", result.Reason);
        }

        [Fact]
        public void GeneEntirelyAbsentFromTheFileIsNotApplicable()
        {
            var result = Evaluate(Cyp2C19(), Resolved("rs1800562", "GG"));

            Assert.Equal(RuleOutcome.NotApplicable, result.Outcome);
        }

        [Fact]
        public void GenotypesMatchingNoAllelePairAreReportedAsSuch()
        {
            var result = Evaluate(Cyp2C19(), Resolved("rs4244285", "TT"), Resolved("rs12248560", "CC"));

            Assert.Equal(RuleOutcome.Indeterminate, result.Outcome);
            Assert.Contains("aucune paire", result.Reason);
        }

        [Fact]
        public void IndeterminateGenotypesAreNotUsedForCalling()
        {
            var unresolved = new Finding(
                new MarkerCall("rs12248560", Chromosome.Autosome(10), 1, new Genotype(Nucleotide.C, Nucleotide.C)),
                FindingStatus.Indeterminate,
                "flip ambigu");

            var result = Evaluate(Cyp2C19(), Resolved("rs4244285", "GG"), unresolved);

            Assert.Equal(RuleOutcome.Indeterminate, result.Outcome);
        }

        [Fact]
        public void PhenotypeLookupIsOrderIndependent()
        {
            var gene = Cyp2C19();

            Assert.Equal("Intermediate Metabolizer", gene.Phenotype("No function", "Normal function"));
            Assert.Equal("Intermediate Metabolizer", gene.Phenotype("Normal function", "No function"));
        }

        [Fact]
        public void MissingFunctionYieldsNoPhenotypeRatherThanAGuess()
        {
            var gene = new Pharmacogene(
                "X",
                new[]
                {
                    new StarAllele("*1", null, new Dictionary<string, char> { { "rs1", 'G' } }),
                    new StarAllele("*2", "No function", new Dictionary<string, char> { { "rs1", 'A' } })
                },
                new List<(string, string, string)>());

            Assert.Null(gene.Phenotype(null, "No function"));
        }
    }

    public class PharmacogenomicsLoaderTests
    {
        [Fact]
        public void ParsesTheHarvestedShape()
        {
            var genes = RuleLoader.ParsePharmacogenes(@"{
              ""genes"": {
                ""CYP2C19"": {
                  ""starAlleles"": [
                    { ""name"": ""*1"", ""function"": ""Normal function"",
                      ""definitions"": { ""rs4244285"": ""G"" } },
                    { ""name"": ""*2"", ""function"": ""No function"",
                      ""definitions"": { ""rs4244285"": ""A"" } }
                  ],
                  ""phenotypeRules"": [
                    { ""function1"": ""No function"", ""function2"": ""Normal function"",
                      ""phenotype"": ""Intermediate Metabolizer"" }
                  ]
                }
              }
            }");

            var gene = Assert.Single(genes);

            Assert.Equal("CYP2C19", gene.Gene);
            Assert.Equal(2, gene.Alleles.Count);
            Assert.Equal("Intermediate Metabolizer", gene.Phenotype("No function", "Normal function"));
        }

        [Fact]
        public void MalformedFileYieldsNoGenes_NotACrash()
        {
            Assert.Empty(RuleLoader.ParsePharmacogenes("{ not json"));
            Assert.Empty(RuleLoader.ParsePharmacogenes(string.Empty));
        }
    }
}
