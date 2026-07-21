using System;

namespace GenomeAnalysis.Annotations.Http
{
    /// <summary>
    /// Politeness settings for calls to a public annotation API.
    /// </summary>
    /// <remarks>
    /// These sources are free and community- or grant-funded. Treating them
    /// carelessly is how tools get blocked, and rightly so. The defaults here are
    /// deliberately conservative.
    /// </remarks>
    public sealed class ThrottleOptions
    {
        /// <summary>
        /// Minimum wall-clock gap between two requests to the same host. Requests
        /// are issued serially; there is no parallel fan-out.
        /// </summary>
        public TimeSpan MinimumInterval { get; set; } = TimeSpan.FromSeconds(1);

        /// <summary>How many times to retry a request that failed retryably.</summary>
        public int MaxRetries { get; set; } = 4;

        /// <summary>
        /// Delay before the first retry. Each subsequent retry doubles it, so a
        /// struggling source is backed off rather than hammered.
        /// </summary>
        public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromSeconds(2);

        public TimeSpan MaximumBackoff { get; set; } = TimeSpan.FromMinutes(2);

        public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Identifies this tool to the source, as public APIs ask clients to do.
        /// Contains no user or machine identifying information.
        /// </summary>
        public string UserAgent { get; set; } =
            "GenomeAnalysis/0.1 (personal genome analysis tool; local single-user)";

        /// <summary>How long a fetched annotation stays usable before refetching.</summary>
        public TimeSpan CacheLifetime { get; set; } = TimeSpan.FromDays(90);

        /// <summary>
        /// How long to remember that a source had no record of a variant. Shorter
        /// than <see cref="CacheLifetime"/>, since absent records do get added.
        /// </summary>
        public TimeSpan AbsentCacheLifetime { get; set; } = TimeSpan.FromDays(30);

        /// <summary>SNPedia asks automated clients to keep to roughly one request per second.</summary>
        public static ThrottleOptions ForSnpedia() => new ThrottleOptions
        {
            MinimumInterval = TimeSpan.FromSeconds(1)
        };

        /// <summary>MyVariant.info tolerates more, but batching is preferred over rate.</summary>
        public static ThrottleOptions ForMyVariant() => new ThrottleOptions
        {
            MinimumInterval = TimeSpan.FromMilliseconds(350),
            RequestTimeout = TimeSpan.FromMinutes(2)
        };

        /// <summary>
        /// Ensembl's batch endpoint is slow — several seconds for a handful of
        /// identifiers — so the timeout has to be generous. Too short a value fails
        /// as a bare "task was canceled" that looks like a network fault rather than
        /// a server that simply takes its time.
        /// </summary>
        public static ThrottleOptions ForEnsembl() => new ThrottleOptions
        {
            MinimumInterval = TimeSpan.FromMilliseconds(200),
            RequestTimeout = TimeSpan.FromMinutes(5)
        };
    }
}
