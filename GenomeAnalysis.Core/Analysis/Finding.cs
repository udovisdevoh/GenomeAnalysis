using System;
using GenomeAnalysis.Core.Annotations;
using GenomeAnalysis.Core.Genome;
using GenomeAnalysis.Core.Strands;

namespace GenomeAnalysis.Core.Analysis
{
    /// <summary>
    /// How much can be said about a marker.
    /// </summary>
    public enum FindingStatus
    {
        /// <summary>The genotype was matched to an annotation and can be interpreted.</summary>
        Determinate = 0,

        /// <summary>
        /// The marker was read but cannot be interpreted — an ambiguous flip, a
        /// missing orientation, an unknown allele set. Displayed with its reason:
        /// this is information, not a failure to hide.
        /// </summary>
        Indeterminate = 1,

        /// <summary>
        /// Nothing to interpret: a no-call, a provider-internal identifier, or a
        /// variant the local database does not cover.
        /// </summary>
        NotApplicable = 2
    }

    /// <summary>
    /// One marker, what was called there, and what is known about it.
    /// </summary>
    public sealed class Finding
    {
        public Finding(
            MarkerCall call,
            FindingStatus status,
            string reason,
            VariantAnnotation? annotation = null,
            GenotypeAnnotation? genotypeAnnotation = null,
            StrandMatch? strandMatch = null)
        {
            Call = call ?? throw new ArgumentNullException(nameof(call));
            Status = status;
            Reason = reason;
            Annotation = annotation;
            GenotypeAnnotation = genotypeAnnotation;
            StrandMatch = strandMatch;
        }

        public MarkerCall Call { get; }

        public FindingStatus Status { get; }

        /// <summary>Why this status was reached, in terms fit to display.</summary>
        public string Reason { get; }

        public VariantAnnotation? Annotation { get; }

        /// <summary>The source's record for this specific genotype, when one matched.</summary>
        public GenotypeAnnotation? GenotypeAnnotation { get; }

        public StrandMatch? StrandMatch { get; }

        /// <summary>
        /// Whether this genotype actually carries a non-reference allele, or
        /// <c>null</c> when the reference allele is unknown.
        /// </summary>
        /// <remarks>
        /// ClinVar classifies the variant, not the person. Someone homozygous for
        /// the reference allele does not carry the finding, and showing them its
        /// classification is a false alarm — prothrombin G20210A would be reported
        /// as "pathogenic" for a plain GG genotype. Where this is <c>null</c> the
        /// honest answer is "cannot tell", never "does not carry it".
        /// </remarks>
        public bool? CarriesVariant =>
            StrandMatch?.ResolvedGenotype == null || Annotation == null
                ? null
                : Annotation.CarriesVariant(StrandMatch.ResolvedGenotype.Value);

        /// <summary>
        /// The clinical record, exposed only when the genotype actually carries the
        /// variant it classifies.
        /// </summary>
        public ClinicalAnnotation? Clinical =>
            CarriesVariant == false ? null : Annotation?.Clinical;

        /// <summary>
        /// The clinical record regardless of carrier status, for a report that wants
        /// to say "this position was tested and carries the ordinary allele".
        /// </summary>
        public ClinicalAnnotation? ClinicalForVariant => Annotation?.Clinical;

        /// <summary>
        /// Ranking weight. Clinical classifications outrank statistical
        /// associations, and within each the better-reviewed evidence leads.
        /// </summary>
        /// <remarks>
        /// This drives presentation order, which is the main way a report avoids
        /// burying something that matters under forty trait associations. It is not
        /// a risk score and must never be shown as one.
        /// </remarks>
        public int PriorityScore
        {
            get
            {
                if (Status != FindingStatus.Determinate)
                {
                    return 0;
                }

                // Carrying only the reference allele means the variant's
                // classification does not describe this person. Such a result is
                // worth reporting — the position was tested — but it does not
                // belong among the findings.
                if (CarriesVariant == false)
                {
                    return 0;
                }

                var score = 0;

                if (Clinical != null)
                {
                    switch (Clinical.Significance)
                    {
                        case ClinicalSignificance.Pathogenic:
                            score += 1000;
                            break;
                        case ClinicalSignificance.LikelyPathogenic:
                            score += 800;
                            break;
                        case ClinicalSignificance.DrugResponse:
                            score += 600;
                            break;
                        case ClinicalSignificance.RiskFactor:
                            score += 400;
                            break;
                        case ClinicalSignificance.ConflictingInterpretations:
                            score += 300;
                            break;
                        case ClinicalSignificance.UncertainSignificance:
                            score += 100;
                            break;
                    }

                    // Review status weighs as heavily as the classification: a
                    // one-star pathogenic call is not a four-star one.
                    score += Clinical.ReviewStatus.ToStarRating() * 50;
                }

                if (Annotation != null)
                {
                    foreach (var trait in Annotation.TraitAssociations)
                    {
                        if (trait.IsGenomeWideSignificant && !trait.IsNegligibleEffect)
                        {
                            score += 30;
                        }
                    }
                }

                return score;
            }
        }

        /// <summary>
        /// Whether this finding must be presented with confirmatory-testing language
        /// and a recommendation of professional genetic counselling.
        /// </summary>
        /// <remarks>
        /// Raw array data is not clinical grade — a substantial share of variants
        /// reported in it are false positives on laboratory confirmation. A
        /// high-impact classification therefore governs how the finding is worded,
        /// not just how prominently it is placed.
        /// </remarks>
        public bool RequiresConfirmatoryTesting =>
            Status == FindingStatus.Determinate &&
            Clinical != null &&
            Clinical.RequiresConfirmatoryTesting;
    }
}
