namespace GenomeAnalysis.Core.Annotations
{
    /// <summary>
    /// ClinVar's clinical significance classification for a variant.
    /// </summary>
    public enum ClinicalSignificance
    {
        NotProvided = 0,
        Benign = 1,
        LikelyBenign = 2,

        /// <summary>Variant of uncertain significance. Not evidence of risk.</summary>
        UncertainSignificance = 3,

        LikelyPathogenic = 4,
        Pathogenic = 5,

        /// <summary>Submitters disagree. Must be surfaced as a disagreement.</summary>
        ConflictingInterpretations = 6,

        DrugResponse = 7,
        RiskFactor = 8,
        Protective = 9,
        Association = 10,
        Other = 11
    }

    /// <summary>
    /// ClinVar's review status, which says how much scrutiny a classification has
    /// had. It carries as much weight as the classification itself: a one-star
    /// variant of uncertain significance is not a four-star pathogenic call, and
    /// the report must never flatten that distinction.
    /// </summary>
    public enum ClinVarReviewStatus
    {
        /// <summary>No assertion criteria provided — zero stars.</summary>
        NoAssertionCriteria = 0,

        /// <summary>One submitter, with assertion criteria — one star.</summary>
        SingleSubmitter = 1,

        /// <summary>Multiple submitters, no conflicts — two stars.</summary>
        MultipleSubmittersNoConflicts = 2,

        /// <summary>Reviewed by an expert panel — three stars.</summary>
        ExpertPanel = 3,

        /// <summary>Backed by a practice guideline — four stars.</summary>
        PracticeGuideline = 4,

        /// <summary>Submitters conflict. Surface the conflict, do not pick a side.</summary>
        ConflictingSubmissions = 5
    }

    public static class ClinVarReviewStatusExtensions
    {
        /// <summary>
        /// The star rating ClinVar publishes, 0 to 4. Conflicting submissions
        /// carry one star.
        /// </summary>
        public static int ToStarRating(this ClinVarReviewStatus status)
        {
            switch (status)
            {
                case ClinVarReviewStatus.PracticeGuideline: return 4;
                case ClinVarReviewStatus.ExpertPanel: return 3;
                case ClinVarReviewStatus.MultipleSubmittersNoConflicts: return 2;
                case ClinVarReviewStatus.SingleSubmitter: return 1;
                case ClinVarReviewStatus.ConflictingSubmissions: return 1;
                default: return 0;
            }
        }

        /// <summary>
        /// Whether a classification carries enough review to be presented as more
        /// than a single unreviewed submission. Below this, the report should lead
        /// with the weakness of the evidence.
        /// </summary>
        public static bool IsWellReviewed(this ClinVarReviewStatus status) =>
            status.ToStarRating() >= 2 && status != ClinVarReviewStatus.ConflictingSubmissions;
    }
}
