using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GenomeAnalysis.Annotations.Http;
using GenomeAnalysis.Core.Annotations;

namespace GenomeAnalysis.Annotations.Cache
{
    /// <summary>
    /// Wraps an annotation source with the local cache, and can forbid network
    /// access outright.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every source should be consumed through this decorator rather than
    /// directly. It is what keeps request volume defensible, and what makes the
    /// offline guarantee enforceable in code instead of by convention.
    /// </para>
    /// <para>
    /// With <see cref="AllowNetwork"/> false, a cache miss returns nothing instead
    /// of reaching out. That is the mode to use while analysing a user's file: the
    /// cache is filled beforehand from the public chip manifest, so no request can
    /// be shaped by their genotype.
    /// </para>
    /// </remarks>
    public sealed class CachingAnnotationSource : IVariantAnnotationSource
    {
        private readonly IVariantAnnotationSource _inner;
        private readonly IAnnotationCache _cache;
        private readonly ThrottleOptions _options;

        public CachingAnnotationSource(
            IVariantAnnotationSource inner,
            IAnnotationCache cache,
            ThrottleOptions? options = null,
            bool allowNetwork = true)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _options = options ?? new ThrottleOptions();
            AllowNetwork = allowNetwork;
        }

        public string SourceName => _inner.SourceName;

        /// <summary>
        /// When false, cache misses stay misses and no request is issued. Set this
        /// for any lookup driven by the user's data.
        /// </summary>
        public bool AllowNetwork { get; }

        /// <summary>Lookups that were missed because the cache was cold and network was off.</summary>
        public int MissedWhileOffline { get; private set; }

        public async Task<VariantAnnotation?> GetAsync(
            string rsId,
            CancellationToken cancellationToken = default)
        {
            var cached = await _cache.TryGetAsync(SourceName, rsId, cancellationToken).ConfigureAwait(false);

            if (cached != null && !cached.IsExpired(DateTimeOffset.UtcNow))
            {
                return cached.Annotation;
            }

            if (!AllowNetwork)
            {
                // A stale entry still beats nothing, and refusing to refresh it is
                // the point of offline mode.
                if (cached != null)
                {
                    return cached.Annotation;
                }

                MissedWhileOffline++;
                return null;
            }

            var fetched = await _inner.GetAsync(rsId, cancellationToken).ConfigureAwait(false);
            await StoreAsync(rsId, fetched, cancellationToken).ConfigureAwait(false);

            return fetched;
        }

        public async Task<IReadOnlyDictionary<string, VariantAnnotation>> GetManyAsync(
            IEnumerable<string> rsIds,
            CancellationToken cancellationToken = default)
        {
            var results = new Dictionary<string, VariantAnnotation>(StringComparer.OrdinalIgnoreCase);
            var toFetch = new List<string>();
            var now = DateTimeOffset.UtcNow;

            foreach (var rsId in (rsIds ?? Enumerable.Empty<string>()).Where(id => !string.IsNullOrWhiteSpace(id)))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var cached = await _cache.TryGetAsync(SourceName, rsId, cancellationToken).ConfigureAwait(false);

                if (cached != null && !cached.IsExpired(now))
                {
                    if (cached.Annotation != null)
                    {
                        results[cached.Annotation.RsId] = cached.Annotation;
                    }

                    continue;
                }

                if (!AllowNetwork)
                {
                    if (cached?.Annotation != null)
                    {
                        results[cached.Annotation.RsId] = cached.Annotation;
                    }
                    else
                    {
                        MissedWhileOffline++;
                    }

                    continue;
                }

                toFetch.Add(rsId);
            }

            if (toFetch.Count == 0)
            {
                return results;
            }

            var fetched = await _inner.GetManyAsync(toFetch, cancellationToken).ConfigureAwait(false);

            foreach (var rsId in toFetch)
            {
                fetched.TryGetValue(rsId.Trim().ToLowerInvariant(), out var annotation);

                // Record absences too, so a variant the source does not know is not
                // asked for again on every run.
                await StoreAsync(rsId, annotation, cancellationToken).ConfigureAwait(false);

                if (annotation != null)
                {
                    results[annotation.RsId] = annotation;
                }
            }

            return results;
        }

        private Task StoreAsync(string rsId, VariantAnnotation? annotation, CancellationToken cancellationToken)
        {
            var lifetime = annotation == null ? _options.AbsentCacheLifetime : _options.CacheLifetime;

            return _cache.StoreAsync(
                SourceName,
                rsId,
                annotation,
                DateTimeOffset.UtcNow + lifetime,
                cancellationToken);
        }
    }
}
