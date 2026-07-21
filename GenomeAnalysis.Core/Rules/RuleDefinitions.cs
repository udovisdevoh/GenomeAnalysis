using System;
using System.Collections.Generic;

namespace GenomeAnalysis.Core.Rules
{
    /// <summary>
    /// The kinds of combination a rule can express.
    /// </summary>
    public enum RuleKind
    {
        /// <summary>
        /// Alleles derived from a combination of positions, then read as a
        /// diplotype — APOE ε2/ε3/ε4 from rs429358 and rs7412.
        /// </summary>
        Haplotype = 0,

        /// <summary>
        /// Two different variants in the same gene, each capable of impairing it.
        /// Unphased data cannot tell one damaged copy from two.
        /// </summary>
        CompoundHeterozygosity = 1
    }

    /// <summary>
    /// One haplotype: the allele expected at each defining position.
    /// </summary>
    public sealed class HaplotypeDefinition
    {
        public HaplotypeDefinition(string name, IReadOnlyDictionary<string, char> alleles)
        {
            Name = name;
            Alleles = alleles;
        }

        /// <summary>e.g. <c>ε3</c>.</summary>
        public string Name { get; }

        /// <summary>rsID to the allele this haplotype carries there.</summary>
        public IReadOnlyDictionary<string, char> Alleles { get; }
    }

    /// <summary>
    /// One variant participating in a compound-heterozygosity rule.
    /// </summary>
    public sealed class RuleVariant
    {
        public RuleVariant(string rsId, char riskAllele, string label)
        {
            RsId = rsId;
            RiskAllele = riskAllele;
            Label = label;
        }

        public string RsId { get; }

        /// <summary>
        /// The allele that impairs the gene, on the same strand the annotation
        /// source reports — resolution has already happened by the time a rule
        /// sees a genotype.
        /// </summary>
        public char RiskAllele { get; }

        /// <summary>Common name, e.g. <c>C282Y</c>.</summary>
        public string Label { get; }
    }

    /// <summary>
    /// A declarative multi-marker rule.
    /// </summary>
    /// <remarks>
    /// Rules are data loaded from <c>data/rules.json</c>, not compiled code. Adding
    /// a gene means editing that file; the engine does not change.
    /// </remarks>
    public sealed class RuleDefinition
    {
        public RuleDefinition(
            string id,
            string name,
            RuleKind kind,
            string? gene,
            IReadOnlyList<string> requiredMarkers,
            IReadOnlyList<HaplotypeDefinition> haplotypes,
            IReadOnlyList<RuleVariant> variants,
            IReadOnlyDictionary<string, string> interpretations,
            string? note)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentException("Rule id is required.", nameof(id));
            }

            Id = id;
            Name = name;
            Kind = kind;
            Gene = gene;
            RequiredMarkers = requiredMarkers;
            Haplotypes = haplotypes;
            Variants = variants;
            Interpretations = interpretations;
            Note = note;
        }

        public string Id { get; }

        public string Name { get; }

        public RuleKind Kind { get; }

        public string? Gene { get; }

        /// <summary>
        /// Every position the rule needs. All of them must be readable, or the
        /// result is indeterminate.
        /// </summary>
        public IReadOnlyList<string> RequiredMarkers { get; }

        public IReadOnlyList<HaplotypeDefinition> Haplotypes { get; }

        public IReadOnlyList<RuleVariant> Variants { get; }

        /// <summary>Diplotype or state to a plain-language note, where one is defined.</summary>
        public IReadOnlyDictionary<string, string> Interpretations { get; }

        /// <summary>Caveat displayed with any result from this rule.</summary>
        public string? Note { get; }
    }
}
