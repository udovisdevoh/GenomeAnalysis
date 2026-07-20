using GenomeAnalysis.Core.Genome;

namespace GenomeAnalysis.Core.Strands
{
    public enum StrandMatchOutcome
    {
        /// <summary>
        /// The observed genotype was mapped into the annotation source's
        /// orientation and can be looked up.
        /// </summary>
        Resolved = 0,

        /// <summary>
        /// A palindromic (A/T or C/G) variant whose genotype cannot be mapped:
        /// both strand readings are valid genotypes of the same variant. SNPedia
        /// calls this an <em>ambiguous flip</em>. Report it; never guess it.
        /// </summary>
        AmbiguousFlip = 1,

        /// <summary>
        /// Not enough information to map safely — orientation missing, allele set
        /// unknown, or the genotype is inconsistent with the variant's alleles.
        /// </summary>
        Unresolvable = 2
    }

    /// <summary>
    /// The outcome of reconciling an observed genotype with an annotation
    /// source's strand orientation.
    /// </summary>
    public sealed class StrandMatch
    {
        private StrandMatch(
            StrandMatchOutcome outcome,
            Genotype? resolvedGenotype,
            bool wasComplemented,
            string reason)
        {
            Outcome = outcome;
            ResolvedGenotype = resolvedGenotype;
            WasComplemented = wasComplemented;
            Reason = reason;
        }

        public StrandMatchOutcome Outcome { get; }

        /// <summary>
        /// The genotype expressed in the annotation source's orientation, set only
        /// when <see cref="Outcome"/> is <see cref="StrandMatchOutcome.Resolved"/>.
        /// </summary>
        public Genotype? ResolvedGenotype { get; }

        /// <summary>Whether the alleles had to be complemented to get there.</summary>
        public bool WasComplemented { get; }

        /// <summary>
        /// Why this outcome was reached, in terms suitable for display. An
        /// unresolved marker is information worth showing, not a failure to hide.
        /// </summary>
        public string Reason { get; }

        public bool IsResolved => Outcome == StrandMatchOutcome.Resolved;

        internal static StrandMatch Resolved(Genotype genotype, bool wasComplemented, string reason) =>
            new StrandMatch(StrandMatchOutcome.Resolved, genotype, wasComplemented, reason);

        internal static StrandMatch AmbiguousFlip(string reason) =>
            new StrandMatch(StrandMatchOutcome.AmbiguousFlip, null, false, reason);

        internal static StrandMatch Unresolvable(string reason) =>
            new StrandMatch(StrandMatchOutcome.Unresolvable, null, false, reason);
    }
}
