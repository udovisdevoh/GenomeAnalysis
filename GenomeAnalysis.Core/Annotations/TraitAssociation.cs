using System;

namespace GenomeAnalysis.Core.Annotations
{
    /// <summary>
    /// A published association between a variant and a trait, as curated by the
    /// GWAS Catalog.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These are statistical associations in populations, not statements about an
    /// individual. An odds ratio of 1.4 does not mean a carrier's risk rises 40%
    /// in absolute terms, and the report must not read that way.
    /// </para>
    /// <para>
    /// Do not multiply odds ratios across variants. Published figures assume
    /// independence, come from different populations and different adjustment
    /// models; composing them mechanically overstates the result, sometimes by a
    /// large factor.
    /// </para>
    /// </remarks>
    public sealed class TraitAssociation
    {
        public TraitAssociation(
            string trait,
            double? oddsRatio,
            double? beta,
            string? betaUnit,
            double? pValue,
            string? riskAllele,
            string? pubMedId,
            int? sampleSize,
            SourceAttribution attribution,
            string? traitUri = null)
        {
            if (string.IsNullOrWhiteSpace(trait))
            {
                throw new ArgumentException("Trait is required.", nameof(trait));
            }

            Trait = trait;
            OddsRatio = oddsRatio;
            Beta = beta;
            BetaUnit = betaUnit;
            PValue = pValue;
            RiskAllele = riskAllele;
            PubMedId = pubMedId;
            SampleSize = sampleSize;
            Attribution = attribution ?? throw new ArgumentNullException(nameof(attribution));
            TraitUri = traitUri;
        }

        public string Trait { get; }

        /// <summary>Odds ratio per copy of the risk allele, for binary traits.</summary>
        public double? OddsRatio { get; }

        /// <summary>Effect size for continuous traits, with <see cref="BetaUnit"/>.</summary>
        public double? Beta { get; }

        public string? BetaUnit { get; }

        public double? PValue { get; }

        /// <summary>
        /// The allele the effect is reported for. Without it an odds ratio cannot be
        /// applied to a genotype at all — and note this allele is on the source
        /// study's strand, so it needs the same reconciliation as everything else.
        /// </summary>
        public string? RiskAllele { get; }

        public string? PubMedId { get; }

        public int? SampleSize { get; }

        public string? TraitUri { get; }

        public SourceAttribution Attribution { get; }

        /// <summary>
        /// Whether the association clears the conventional genome-wide significance
        /// threshold of 5×10⁻⁸. Below it, an association is suggestive at best and
        /// should not be presented alongside established findings.
        /// </summary>
        public bool IsGenomeWideSignificant => PValue.HasValue && PValue.Value <= 5e-8;

        /// <summary>
        /// Effect sizes this small are routine in GWAS and carry no meaning for an
        /// individual. Used to keep the report from dressing up noise.
        /// </summary>
        public bool IsNegligibleEffect =>
            OddsRatio.HasValue && OddsRatio.Value > 0.91 && OddsRatio.Value < 1.10;
    }
}
