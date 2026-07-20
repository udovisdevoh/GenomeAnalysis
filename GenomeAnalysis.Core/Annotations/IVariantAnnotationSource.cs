using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace GenomeAnalysis.Core.Annotations
{
    /// <summary>
    /// A source of variant annotations. Declared in Core and implemented in
    /// GenomeAnalysis.Annotations, so the analysis engine can be exercised with a
    /// test double and never needs a network or a database to run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Privacy contract.</strong> Implementations receive variant
    /// identifiers only — never a genotype, never a file, never anything derived
    /// from the user's calls.
    /// </para>
    /// <para>
    /// That is necessary but not sufficient. Querying only the variants where the
    /// user carries a notable allele reveals their genotype through the pattern of
    /// requests alone, without a single allele ever being transmitted. Callers
    /// must therefore drive lookups from the provider's public chip manifest or
    /// from a pre-populated bulk cache, never from a user-filtered subset.
    /// </para>
    /// </remarks>
    public interface IVariantAnnotationSource
    {
        /// <summary>A short name for this source, used for attribution.</summary>
        string SourceName { get; }

        /// <summary>
        /// Fetches the annotation for one variant, or <c>null</c> when the source
        /// has no record of it. An unknown rsID is a normal outcome, not an error.
        /// </summary>
        Task<VariantAnnotation?> GetAsync(string rsId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Fetches several variants. Implementations that support batch requests
        /// should override the default one-at-a-time behaviour, but must keep
        /// honouring the source's rate limits.
        /// </summary>
        Task<IReadOnlyDictionary<string, VariantAnnotation>> GetManyAsync(
            IEnumerable<string> rsIds,
            CancellationToken cancellationToken = default);
    }
}
