using System;
using System.Collections.Generic;

namespace GenomeAnalysis.Core.Annotations
{
    /// <summary>
    /// A clinical interpretation of a variant, normally from ClinVar.
    /// </summary>
    /// <remarks>
    /// Presenting this to a user carries obligations: raw consumer array data is
    /// not clinical grade, and a substantial share of variants reported in it turn
    /// out to be false positives on confirmation in an accredited laboratory. Any
    /// high-impact finding must be shown as requiring confirmatory testing, and
    /// <see cref="ReviewStatus"/> must be displayed alongside
    /// <see cref="Significance"/> rather than dropped.
    /// </remarks>
    public sealed class ClinicalAnnotation
    {
        public ClinicalAnnotation(
            ClinicalSignificance significance,
            ClinVarReviewStatus reviewStatus,
            IReadOnlyList<string>? conditions,
            SourceAttribution attribution,
            string? variationId = null,
            DateTimeOffset? lastEvaluated = null)
        {
            Significance = significance;
            ReviewStatus = reviewStatus;
            Conditions = conditions ?? new List<string>();
            Attribution = attribution ?? throw new ArgumentNullException(nameof(attribution));
            VariationId = variationId;
            LastEvaluated = lastEvaluated;
        }

        public ClinicalSignificance Significance { get; }

        public ClinVarReviewStatus ReviewStatus { get; }

        public IReadOnlyList<string> Conditions { get; }

        public string? VariationId { get; }

        public DateTimeOffset? LastEvaluated { get; }

        public SourceAttribution Attribution { get; }

        /// <summary>
        /// Whether this classification calls for the strongest framing: explicit
        /// confirmatory-testing language and a recommendation of professional
        /// genetic counselling.
        /// </summary>
        public bool RequiresConfirmatoryTesting =>
            Significance == ClinicalSignificance.Pathogenic ||
            Significance == ClinicalSignificance.LikelyPathogenic;
    }
}
