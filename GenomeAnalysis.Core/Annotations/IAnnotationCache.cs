using System;
using System.Threading;
using System.Threading.Tasks;

namespace GenomeAnalysis.Core.Annotations
{
    /// <summary>
    /// A cached annotation, including the case where the source is known to have
    /// no record of the variant.
    /// </summary>
    public sealed class CachedAnnotation
    {
        public CachedAnnotation(
            VariantAnnotation? annotation,
            DateTimeOffset retrievedAt,
            DateTimeOffset expiresAt)
        {
            Annotation = annotation;
            RetrievedAt = retrievedAt;
            ExpiresAt = expiresAt;
        }

        /// <summary><c>null</c> when the source is known to have no record.</summary>
        public VariantAnnotation? Annotation { get; }

        /// <summary>
        /// True when we have already asked and the source had nothing. Cached so
        /// the same fruitless lookup is not repeated for every run.
        /// </summary>
        public bool IsKnownAbsent => Annotation == null;

        public DateTimeOffset RetrievedAt { get; }

        public DateTimeOffset ExpiresAt { get; }

        public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;
    }

    /// <summary>
    /// Local persistence for annotations fetched from external sources.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The cache is a requirement, not an optimisation. A genome carries hundreds
    /// of thousands of markers; querying a public API for each one is both
    /// impractical and abusive toward the source.
    /// </para>
    /// <para>
    /// It also has a privacy role. A cache populated ahead of time — from the chip
    /// manifest or a bulk export — means no network request is ever driven by the
    /// contents of the user's file, which is what keeps the request pattern from
    /// leaking their genotype.
    /// </para>
    /// <para>
    /// Only public annotation data belongs here. Never store genotypes, marker
    /// calls, or anything else derived from the user's file.
    /// </para>
    /// </remarks>
    public interface IAnnotationCache
    {
        Task<CachedAnnotation?> TryGetAsync(
            string sourceName,
            string rsId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Stores an annotation, or records a known absence when
        /// <paramref name="annotation"/> is <c>null</c>.
        /// </summary>
        Task StoreAsync(
            string sourceName,
            string rsId,
            VariantAnnotation? annotation,
            DateTimeOffset expiresAt,
            CancellationToken cancellationToken = default);

        /// <summary>Number of entries held for a source, for diagnostics.</summary>
        Task<int> CountAsync(string sourceName, CancellationToken cancellationToken = default);
    }
}
