using System;

namespace GenomeAnalysis.Core.Genome
{
    /// <summary>
    /// One row of a raw consumer DNA file: a marker, where it sits, and what was
    /// called there.
    /// </summary>
    /// <remarks>
    /// This is health data. Do not write <see cref="Genotype"/> to logs, error
    /// messages or traces; <see cref="ToString"/> deliberately omits it.
    /// </remarks>
    public sealed class MarkerCall
    {
        public MarkerCall(
            string markerId,
            Chromosome chromosome,
            int position,
            Genotype? genotype,
            string? rawGenotypeToken = null)
        {
            if (string.IsNullOrWhiteSpace(markerId))
            {
                throw new ArgumentException("Marker id is required.", nameof(markerId));
            }

            MarkerId = markerId.Trim();
            Chromosome = chromosome;
            Position = position;
            Genotype = genotype;
            RawGenotypeToken = rawGenotypeToken;
        }

        /// <summary>
        /// The marker identifier as written in the file: an rsID (<c>rs53576</c>)
        /// or a provider-internal id (<c>i5000940</c>).
        /// </summary>
        public string MarkerId { get; }

        public Chromosome Chromosome { get; }

        public int Position { get; }

        /// <summary>The call, or <c>null</c> when the provider reported no genotype.</summary>
        public Genotype? Genotype { get; }

        /// <summary>
        /// The original token when this is a no-call, kept so the report can say
        /// why a marker was skipped. Never contains an actual genotype.
        /// </summary>
        public string? RawGenotypeToken { get; }

        public bool IsNoCall => Genotype == null;

        /// <summary>
        /// True for a dbSNP reference sequence id. Only these can be looked up in
        /// public databases.
        /// </summary>
        public bool IsRsId =>
            MarkerId.Length > 2 &&
            MarkerId.StartsWith("rs", StringComparison.OrdinalIgnoreCase) &&
            IsAllDigits(MarkerId, 2);

        /// <summary>
        /// True for a provider-internal identifier such as <c>i5000940</c>. These
        /// have no counterpart in public databases and are expected to go
        /// unannotated; that is not an error.
        /// </summary>
        public bool IsProviderInternalId =>
            MarkerId.Length > 1 &&
            MarkerId.StartsWith("i", StringComparison.OrdinalIgnoreCase) &&
            IsAllDigits(MarkerId, 1);

        private static bool IsAllDigits(string value, int startIndex)
        {
            for (var i = startIndex; i < value.Length; i++)
            {
                if (!char.IsDigit(value[i]))
                {
                    return false;
                }
            }

            return value.Length > startIndex;
        }

        /// <summary>
        /// Diagnostic text. Excludes the genotype on purpose so that logging a
        /// marker can never leak health data.
        /// </summary>
        public override string ToString()
        {
            return MarkerId + " @ chr" + Chromosome + ":" + Position.ToString();
        }
    }
}
