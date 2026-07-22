using System;
using System.Collections.Generic;
using GenomeAnalysis.Core.Genome;

namespace GenomeAnalysis.Core.Rules
{
    /// <summary>
    /// One variant in a polygenic score: the allele the weight applies to, the
    /// alternative, and the weight itself.
    /// </summary>
    /// <remarks>
    /// The weight is a per-allele effect size on the log-odds (or log-hazard)
    /// scale, as published. Scores are summed on this scale — that is the whole
    /// reason a polygenic score is not a product of odds ratios.
    /// </remarks>
    public sealed class ScoreVariant
    {
        public ScoreVariant(
            string rsId,
            char effectAllele,
            char otherAllele,
            double weight,
            double? effectAlleleFrequency = null)
        {
            RsId = rsId;
            EffectAllele = effectAllele;
            OtherAllele = otherAllele;
            Weight = weight;
            EffectAlleleFrequency = effectAlleleFrequency;
        }

        public string RsId { get; }

        public char EffectAllele { get; }

        public char OtherAllele { get; }

        public double Weight { get; }

        /// <summary>
        /// Frequency of the effect allele in the score's development population,
        /// where the scoring file states it. Needed for a model-based reference
        /// distribution; without it, no percentile can be computed.
        /// </summary>
        public double? EffectAlleleFrequency { get; }

        /// <summary>
        /// True when the effect and other alleles are each other's complement
        /// (A/T or C/G). A homozygous call on such a variant cannot be strand-
        /// resolved, so it must be excluded from the sum rather than guessed.
        /// </summary>
        public bool IsPalindromic =>
            NucleotideExtensions.TryParse(EffectAllele, out var effect) &&
            NucleotideExtensions.TryParse(OtherAllele, out var other) &&
            NucleotideExtensions.IsPalindromicPair(effect, other);
    }

    /// <summary>
    /// A published polygenic score: a pre-specified list of weighted variants from
    /// one source.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what makes a polygenic score defensible rather than a home-brewed
    /// product of odds ratios: the variants and weights are fixed in advance by a
    /// single study, registered in the PGS Catalog with a citation and a
    /// development ancestry. The tool computes the published score; it does not
    /// invent one.
    /// </para>
    /// <para>
    /// Two limits ride along with every score and must be surfaced. It was
    /// developed in a particular ancestry and transfers poorly outside it. And a
    /// consumer array covers only a fraction of its variants, so the sum is
    /// partial — see <see cref="PolygenicScoreResult"/>.
    /// </para>
    /// </remarks>
    public sealed class PolygenicScore
    {
        public PolygenicScore(
            string id,
            string name,
            string trait,
            string ancestry,
            string citation,
            string genomeBuild,
            IReadOnlyList<ScoreVariant> variants,
            double? referenceMean = null,
            double? referenceStandardDeviation = null)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentException("Score id is required.", nameof(id));
            }

            Id = id;
            Name = name;
            Trait = trait;
            Ancestry = ancestry;
            Citation = citation;
            GenomeBuild = genomeBuild;
            Variants = variants ?? Array.Empty<ScoreVariant>();
            ReferenceMean = referenceMean;
            ReferenceStandardDeviation = referenceStandardDeviation;
        }

        public string Id { get; }

        public string Name { get; }

        public string Trait { get; }

        /// <summary>The population the score was developed in — its calibration is tied to this.</summary>
        public string Ancestry { get; }

        public string Citation { get; }

        public string GenomeBuild { get; }

        public IReadOnlyList<ScoreVariant> Variants { get; }

        /// <summary>
        /// Mean of the score in a documented reference sample, if the score ships
        /// one. A percentile needs a reference distribution; this or per-variant
        /// frequencies supply it, and absent both, none is produced.
        /// </summary>
        public double? ReferenceMean { get; }

        public double? ReferenceStandardDeviation { get; }
    }
}
