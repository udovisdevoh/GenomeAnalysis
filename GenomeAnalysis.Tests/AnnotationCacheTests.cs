using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GenomeAnalysis.Annotations.Cache;
using GenomeAnalysis.Annotations.Http;
using GenomeAnalysis.Core.Annotations;
using GenomeAnalysis.Core.Genome;
using Xunit;

namespace GenomeAnalysis.Tests
{
    /// <summary>
    /// A stand-in source that records what was asked of it, so tests can assert on
    /// whether a lookup would have reached the network.
    /// </summary>
    internal sealed class RecordingAnnotationSource : IVariantAnnotationSource
    {
        private readonly Dictionary<string, VariantAnnotation> _known;

        public RecordingAnnotationSource(params VariantAnnotation[] known)
        {
            _known = known.ToDictionary(a => a.RsId, StringComparer.OrdinalIgnoreCase);
        }

        public string SourceName => "Test";

        public List<string> Requested { get; } = new List<string>();

        public Task<VariantAnnotation?> GetAsync(string rsId, CancellationToken cancellationToken = default)
        {
            Requested.Add(rsId);
            _known.TryGetValue(rsId, out var annotation);
            return Task.FromResult<VariantAnnotation?>(annotation);
        }

        public Task<IReadOnlyDictionary<string, VariantAnnotation>> GetManyAsync(
            IEnumerable<string> rsIds,
            CancellationToken cancellationToken = default)
        {
            var results = new Dictionary<string, VariantAnnotation>(StringComparer.OrdinalIgnoreCase);

            foreach (var rsId in rsIds)
            {
                Requested.Add(rsId);

                if (_known.TryGetValue(rsId, out var annotation))
                {
                    results[annotation.RsId] = annotation;
                }
            }

            return Task.FromResult<IReadOnlyDictionary<string, VariantAnnotation>>(results);
        }
    }

    public class AnnotationSerializerTests
    {
        private static VariantAnnotation SampleAnnotation()
        {
            Genotype.TryParse("AG", out var genotype);

            return new VariantAnnotation(
                "rs1800562",
                Strand.Plus,
                Strand.Minus,
                new[]
                {
                    new GenotypeAnnotation(
                        genotype, "Carrier", 2.5, Repute.Bad, SourceAttribution.Snpedia("Rs1800562(A;G)"))
                },
                "HFE C282Y",
                SourceAttribution.Snpedia("Rs1800562"),
                "HFE",
                new ClinicalAnnotation(
                    ClinicalSignificance.Pathogenic,
                    ClinVarReviewStatus.MultipleSubmittersNoConflicts,
                    new[] { "Hemochromatosis type 1" },
                    SourceAttribution.ClinVar("9"),
                    "9"),
                0.038);
        }

        [Fact]
        public void RoundTrip_PreservesEveryFieldTheReportDependsOn()
        {
            var original = SampleAnnotation();

            var restored = AnnotationSerializer.FromJson(AnnotationSerializer.ToJson(original));

            Assert.NotNull(restored);
            Assert.Equal(original.RsId, restored!.RsId);
            Assert.Equal(original.Orientation, restored.Orientation);
            Assert.Equal(original.StabilizedOrientation, restored.StabilizedOrientation);
            Assert.Equal(original.GeneSymbol, restored.GeneSymbol);
            Assert.Equal(original.MinorAlleleFrequency, restored.MinorAlleleFrequency);
            Assert.Equal(original.Attribution.SourceName, restored.Attribution.SourceName);
            Assert.Equal(original.Attribution.Licence, restored.Attribution.Licence);

            Assert.Single(restored.Genotypes);
            Assert.Equal(2.5, restored.Genotypes[0].Magnitude);
            Assert.Equal(Repute.Bad, restored.Genotypes[0].Repute);

            Assert.Equal(ClinicalSignificance.Pathogenic, restored.Clinical!.Significance);
            Assert.Equal(ClinVarReviewStatus.MultipleSubmittersNoConflicts, restored.Clinical.ReviewStatus);
            Assert.Contains("Hemochromatosis type 1", restored.Clinical.Conditions);
        }

        [Fact]
        public void PayloadFromAnIncompatibleSchema_IsTreatedAsAMiss()
        {
            Assert.Null(AnnotationSerializer.FromJson(@"{""v"":99,""rsId"":""rs1""}"));
            Assert.Null(AnnotationSerializer.FromJson("not json"));
        }
    }

    public class SqliteAnnotationCacheTests
    {
        private static VariantAnnotation Annotation(string rsId) =>
            new VariantAnnotation(rsId, Strand.Plus, Strand.Plus, null, "summary", SourceAttribution.Snpedia(), "GENE");

        [Fact]
        public async Task StoreThenRetrieve_ReturnsTheAnnotation()
        {
            using (var cache = SqliteAnnotationCache.InMemory())
            {
                await cache.StoreAsync("SNPedia", "rs53576", Annotation("rs53576"), DateTimeOffset.UtcNow.AddDays(1));

                var cached = await cache.TryGetAsync("SNPedia", "rs53576");

                Assert.NotNull(cached);
                Assert.False(cached!.IsKnownAbsent);
                Assert.Equal("rs53576", cached.Annotation!.RsId);
            }
        }

        [Fact]
        public async Task RecordedAbsence_IsDistinctFromNeverAsked()
        {
            using (var cache = SqliteAnnotationCache.InMemory())
            {
                await cache.StoreAsync("SNPedia", "rs999999", null, DateTimeOffset.UtcNow.AddDays(1));

                var asked = await cache.TryGetAsync("SNPedia", "rs999999");
                var neverAsked = await cache.TryGetAsync("SNPedia", "rs111111");

                Assert.NotNull(asked);
                Assert.True(asked!.IsKnownAbsent);
                Assert.Null(neverAsked);
            }
        }

        [Fact]
        public async Task LookupIsCaseInsensitiveOnRsId()
        {
            using (var cache = SqliteAnnotationCache.InMemory())
            {
                await cache.StoreAsync("SNPedia", "RS53576", Annotation("rs53576"), DateTimeOffset.UtcNow.AddDays(1));
                Assert.NotNull(await cache.TryGetAsync("SNPedia", "rs53576"));
            }
        }

        [Fact]
        public async Task SourcesAreKeptSeparate()
        {
            using (var cache = SqliteAnnotationCache.InMemory())
            {
                await cache.StoreAsync("SNPedia", "rs53576", Annotation("rs53576"), DateTimeOffset.UtcNow.AddDays(1));

                Assert.Null(await cache.TryGetAsync("MyVariant.info", "rs53576"));
                Assert.Equal(1, await cache.CountAsync("SNPedia"));
                Assert.Equal(0, await cache.CountAsync("MyVariant.info"));
            }
        }

        [Fact]
        public async Task StoringTwice_UpdatesRatherThanDuplicating()
        {
            using (var cache = SqliteAnnotationCache.InMemory())
            {
                await cache.StoreAsync("SNPedia", "rs53576", Annotation("rs53576"), DateTimeOffset.UtcNow.AddDays(1));
                await cache.StoreAsync("SNPedia", "rs53576", Annotation("rs53576"), DateTimeOffset.UtcNow.AddDays(2));

                Assert.Equal(1, await cache.CountAsync("SNPedia"));
            }
        }
    }

    public class CachingAnnotationSourceTests
    {
        private static VariantAnnotation Annotation(string rsId) =>
            new VariantAnnotation(rsId, Strand.Plus, Strand.Plus, null, "summary", SourceAttribution.Snpedia());

        [Fact]
        public async Task SecondLookup_IsServedFromCache_WithoutAskingTheSource()
        {
            var inner = new RecordingAnnotationSource(Annotation("rs53576"));

            using (var cache = SqliteAnnotationCache.InMemory())
            {
                var source = new CachingAnnotationSource(inner, cache);

                await source.GetAsync("rs53576");
                await source.GetAsync("rs53576");

                Assert.Single(inner.Requested);
            }
        }

        [Fact]
        public async Task AbsenceIsCached_SoAFruitlessLookupIsNotRepeated()
        {
            var inner = new RecordingAnnotationSource();

            using (var cache = SqliteAnnotationCache.InMemory())
            {
                var source = new CachingAnnotationSource(inner, cache);

                Assert.Null(await source.GetAsync("rs999999"));
                Assert.Null(await source.GetAsync("rs999999"));

                Assert.Single(inner.Requested);
            }
        }

        [Fact]
        public async Task OfflineMode_NeverReachesTheSource_EvenOnACacheMiss()
        {
            // This is the guarantee that keeps the request pattern from leaking a
            // genotype: with the network off, nothing the user's file contains can
            // cause a request.
            var inner = new RecordingAnnotationSource(Annotation("rs53576"));

            using (var cache = SqliteAnnotationCache.InMemory())
            {
                var source = new CachingAnnotationSource(inner, cache, allowNetwork: false);

                var result = await source.GetAsync("rs53576");

                Assert.Null(result);
                Assert.Empty(inner.Requested);
                Assert.Equal(1, source.MissedWhileOffline);
            }
        }

        [Fact]
        public async Task OfflineMode_StillServesPrePopulatedEntries()
        {
            var inner = new RecordingAnnotationSource(Annotation("rs53576"));

            using (var cache = SqliteAnnotationCache.InMemory())
            {
                // Warm the cache the way a bulk import would.
                await cache.StoreAsync("Test", "rs53576", Annotation("rs53576"), DateTimeOffset.UtcNow.AddDays(30));

                var source = new CachingAnnotationSource(inner, cache, allowNetwork: false);
                var result = await source.GetAsync("rs53576");

                Assert.NotNull(result);
                Assert.Empty(inner.Requested);
                Assert.Equal(0, source.MissedWhileOffline);
            }
        }

        [Fact]
        public async Task OfflineMode_BatchLookupsAreAlsoBlocked()
        {
            var inner = new RecordingAnnotationSource(Annotation("rs1"), Annotation("rs2"));

            using (var cache = SqliteAnnotationCache.InMemory())
            {
                var source = new CachingAnnotationSource(inner, cache, allowNetwork: false);

                var results = await source.GetManyAsync(new[] { "rs1", "rs2" });

                Assert.Empty(results);
                Assert.Empty(inner.Requested);
                Assert.Equal(2, source.MissedWhileOffline);
            }
        }

        [Fact]
        public async Task ExpiredEntry_IsRefetchedWhenNetworkIsAllowed()
        {
            var inner = new RecordingAnnotationSource(Annotation("rs53576"));

            using (var cache = SqliteAnnotationCache.InMemory())
            {
                await cache.StoreAsync("Test", "rs53576", Annotation("rs53576"), DateTimeOffset.UtcNow.AddDays(-1));

                var source = new CachingAnnotationSource(inner, cache);
                await source.GetAsync("rs53576");

                Assert.Single(inner.Requested);
            }
        }

        [Fact]
        public async Task AbsentEntriesExpireSoonerThanPresentOnes()
        {
            var options = new ThrottleOptions
            {
                CacheLifetime = TimeSpan.FromDays(90),
                AbsentCacheLifetime = TimeSpan.FromDays(30)
            };

            using (var cache = SqliteAnnotationCache.InMemory())
            {
                var source = new CachingAnnotationSource(new RecordingAnnotationSource(), cache, options);

                await source.GetAsync("rs999999");

                var cached = await cache.TryGetAsync("Test", "rs999999");

                Assert.NotNull(cached);
                Assert.True(cached!.ExpiresAt < DateTimeOffset.UtcNow.AddDays(31));
            }
        }
    }
}
