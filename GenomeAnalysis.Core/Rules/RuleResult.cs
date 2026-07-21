using System;
using System.Collections.Generic;
using System.Linq;

namespace GenomeAnalysis.Core.Rules
{
    /// <summary>
    /// How much a multi-marker rule could conclude.
    /// </summary>
    public enum RuleOutcome
    {
        /// <summary>Every required marker was read and the combination resolves to one answer.</summary>
        Determinate = 0,

        /// <summary>
        /// The rule applies but cannot be resolved — a required marker is missing,
        /// a genotype was indeterminate, or the data admits several answers that
        /// nothing in it can separate.
        /// </summary>
        Indeterminate = 1,

        /// <summary>The rule's markers are absent from this file entirely.</summary>
        NotApplicable = 2
    }

    /// <summary>
    /// The conclusion of one multi-marker rule.
    /// </summary>
    public sealed class RuleResult
    {
        private RuleResult(
            string ruleId,
            string ruleName,
            string? gene,
            RuleOutcome outcome,
            string? conclusion,
            string reason,
            IReadOnlyList<string> candidates,
            IReadOnlyList<string> missingMarkers,
            IReadOnlyList<string> usedMarkers,
            bool phaseLimited,
            string? interpretation)
        {
            RuleId = ruleId;
            RuleName = ruleName;
            Gene = gene;
            Outcome = outcome;
            Conclusion = conclusion;
            Reason = reason;
            Candidates = candidates;
            MissingMarkers = missingMarkers;
            UsedMarkers = usedMarkers;
            PhaseLimited = phaseLimited;
            Interpretation = interpretation;
        }

        public string RuleId { get; }

        public string RuleName { get; }

        public string? Gene { get; }

        public RuleOutcome Outcome { get; }

        /// <summary>The single answer, when there is one — e.g. <c>ε3/ε4</c>.</summary>
        public string? Conclusion { get; }

        /// <summary>Why this outcome was reached, in terms fit to display.</summary>
        public string Reason { get; }

        /// <summary>
        /// The answers the data is consistent with when it cannot be narrowed to
        /// one. Shown rather than silently reduced to a first guess.
        /// </summary>
        public IReadOnlyList<string> Candidates { get; }

        /// <summary>
        /// Required markers the file did not supply. A rule needing five positions
        /// with three present is indeterminate, never an assumption about the rest.
        /// </summary>
        public IReadOnlyList<string> MissingMarkers { get; }

        public IReadOnlyList<string> UsedMarkers { get; }

        /// <summary>
        /// Whether the conclusion is limited by the absence of phase information.
        /// </summary>
        /// <remarks>
        /// Array data cannot say whether two variants sit on the same chromosome
        /// copy or on opposite copies. Where that distinction changes the meaning,
        /// the finding must be worded "consistent with", never "confirmed".
        /// </remarks>
        public bool PhaseLimited { get; }

        /// <summary>Plain-language note attached to the conclusion, where the rule supplies one.</summary>
        public string? Interpretation { get; }

        public static RuleResult Determinate(
            string ruleId,
            string ruleName,
            string? gene,
            string conclusion,
            string reason,
            IReadOnlyList<string> usedMarkers,
            bool phaseLimited = false,
            string? interpretation = null) =>
            new RuleResult(ruleId, ruleName, gene, RuleOutcome.Determinate, conclusion, reason,
                Array.Empty<string>(), Array.Empty<string>(), usedMarkers, phaseLimited, interpretation);

        public static RuleResult Ambiguous(
            string ruleId,
            string ruleName,
            string? gene,
            IReadOnlyList<string> candidates,
            string reason,
            IReadOnlyList<string> usedMarkers,
            bool phaseLimited = true) =>
            new RuleResult(ruleId, ruleName, gene, RuleOutcome.Indeterminate, null, reason,
                candidates, Array.Empty<string>(), usedMarkers, phaseLimited, null);

        public static RuleResult Missing(
            string ruleId,
            string ruleName,
            string? gene,
            IReadOnlyList<string> missing,
            IReadOnlyList<string> present) =>
            new RuleResult(ruleId, ruleName, gene, RuleOutcome.Indeterminate, null,
                "Ce résultat exige " + (missing.Count + present.Count) + " positions ; " +
                missing.Count + " manquent ou n'ont pas pu être lues (" +
                string.Join(", ", missing.Take(4)) + (missing.Count > 4 ? "…" : string.Empty) + "). " +
                "Aucune supposition n'est faite sur les positions absentes.",
                Array.Empty<string>(), missing, present, false, null);

        public static RuleResult NotApplicable(string ruleId, string ruleName, string? gene) =>
            new RuleResult(ruleId, ruleName, gene, RuleOutcome.NotApplicable, null,
                "Aucune des positions de cette règle n'est présente dans ce fichier.",
                Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), false, null);
    }
}
