using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GenomeAnalysis.Annotations.Cache;
using GenomeAnalysis.Core.Annotations;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GenomeAnalysis.Annotations.Local
{
    /// <summary>
    /// A local, file-backed variant database that answers lookups with no network
    /// access whatsoever.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what makes the privacy guarantee real rather than aspirational. The
    /// file is built ahead of time by the harvester from a public seed list, then
    /// committed. At analysis time the tool reads it and nothing else, so no
    /// request can be shaped by the contents of a user's genome — there are no
    /// requests.
    /// </para>
    /// <para>
    /// It also resolves merged rsIDs: a lookup for a withdrawn identifier finds the
    /// record it was merged into, which is what lets an older file match at all.
    /// </para>
    /// </remarks>
    public sealed class VariantDatabase : IVariantAnnotationSource
    {
        public const int SchemaVersion = 1;

        private readonly Dictionary<string, VariantAnnotation> _byRsId;
        private readonly Dictionary<string, string> _mergedToCurrent;

        private VariantDatabase(
            Dictionary<string, VariantAnnotation> byRsId,
            Dictionary<string, string> mergedToCurrent,
            DateTimeOffset? generatedAt,
            IReadOnlyList<string> sourceNames)
        {
            _byRsId = byRsId;
            _mergedToCurrent = mergedToCurrent;
            GeneratedAt = generatedAt;
            SourceNames = sourceNames;
        }

        public string SourceName => "Local variant database";

        public int Count => _byRsId.Count;

        public DateTimeOffset? GeneratedAt { get; }

        public IReadOnlyList<string> SourceNames { get; }

        public IEnumerable<VariantAnnotation> Variants => _byRsId.Values;

        public static VariantDatabase Empty() => new VariantDatabase(
            new Dictionary<string, VariantAnnotation>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            null,
            new List<string>());

        public static VariantDatabase Load(string path)
        {
            if (!File.Exists(path))
            {
                return Empty();
            }

            return Parse(File.ReadAllText(path, Encoding.UTF8));
        }

        public static VariantDatabase Parse(string json)
        {
            var byRsId = new Dictionary<string, VariantAnnotation>(StringComparer.OrdinalIgnoreCase);
            var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(json))
            {
                return Empty();
            }

            JObject root;

            try
            {
                root = JObject.Parse(json);
            }
            catch (JsonException)
            {
                return Empty();
            }

            if (root["variants"] is JObject variants)
            {
                foreach (var property in variants.Properties())
                {
                    var annotation = AnnotationSerializer.FromJson(property.Value.ToString(Formatting.None));

                    if (annotation == null)
                    {
                        continue;
                    }

                    byRsId[annotation.RsId] = annotation;

                    foreach (var old in annotation.MergedRsIds)
                    {
                        // Keep the first mapping seen. A collision would mean two
                        // current variants claim the same withdrawn identifier,
                        // which should not happen and must not be papered over by
                        // silently reassigning it.
                        if (!merged.ContainsKey(old) && !byRsId.ContainsKey(old))
                        {
                            merged[old] = annotation.RsId;
                        }
                    }
                }
            }

            DateTimeOffset? generatedAt = null;
            var rawDate = root["generatedAt"]?.Value<string>();

            if (!string.IsNullOrWhiteSpace(rawDate) && DateTimeOffset.TryParse(rawDate, out var parsed))
            {
                generatedAt = parsed;
            }

            var sourceNames = (root["sources"] as JArray)
                ?.OfType<JObject>()
                .Select(s => s["name"]?.Value<string>())
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n!)
                .ToList() ?? new List<string>();

            return new VariantDatabase(byRsId, merged, generatedAt, sourceNames);
        }

        /// <summary>
        /// Finds a variant, following a merged identifier to its current record.
        /// Returns <c>null</c> when the database has no entry, which is a normal
        /// outcome for a marker outside the seed list.
        /// </summary>
        public VariantAnnotation? Find(string? rsId)
        {
            if (string.IsNullOrWhiteSpace(rsId))
            {
                return null;
            }

            var key = rsId!.Trim().ToLowerInvariant();

            if (_byRsId.TryGetValue(key, out var direct))
            {
                return direct;
            }

            return _mergedToCurrent.TryGetValue(key, out var current) && _byRsId.TryGetValue(current, out var viaMerge)
                ? viaMerge
                : null;
        }

        public Task<VariantAnnotation?> GetAsync(string rsId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Find(rsId));

        public Task<IReadOnlyDictionary<string, VariantAnnotation>> GetManyAsync(
            IEnumerable<string> rsIds,
            CancellationToken cancellationToken = default)
        {
            var results = new Dictionary<string, VariantAnnotation>(StringComparer.OrdinalIgnoreCase);

            foreach (var rsId in rsIds ?? Enumerable.Empty<string>())
            {
                var found = Find(rsId);

                if (found != null)
                {
                    results[found.RsId] = found;
                }
            }

            return Task.FromResult<IReadOnlyDictionary<string, VariantAnnotation>>(results);
        }

        /// <summary>
        /// Writes the database, with the provenance block that keeps the file
        /// self-describing and licence-compliant.
        /// </summary>
        public static void Save(
            string path,
            IEnumerable<VariantAnnotation> annotations,
            IEnumerable<SourceAttribution> sources)
        {
            var variants = new JObject();

            foreach (var annotation in annotations.OrderBy(a => a.RsId, StringComparer.OrdinalIgnoreCase))
            {
                variants[annotation.RsId] = JObject.Parse(AnnotationSerializer.ToJson(annotation));
            }

            var sourceArray = new JArray();

            foreach (var source in sources.GroupBy(s => s.SourceName).Select(g => g.First()))
            {
                sourceArray.Add(new JObject
                {
                    ["name"] = source.SourceName,
                    ["licence"] = source.Licence,
                    ["licenceUrl"] = source.LicenceUrl
                });
            }

            var root = new JObject
            {
                ["schemaVersion"] = SchemaVersion,
                ["generatedAt"] = DateTimeOffset.UtcNow.ToString("O"),
                ["notice"] =
                    "Reference data about public variant identifiers only. Contains no genotype " +
                    "and no data derived from any individual's genome. Built by " +
                    "GenomeAnalysis.Harvester from a public seed list. Attribution for each source " +
                    "is listed below and must be reproduced in any report built from this file.",
                ["sources"] = sourceArray,
                ["variantCount"] = variants.Count,
                ["variants"] = variants
            };

            var directory = Path.GetDirectoryName(Path.GetFullPath(path));

            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory!);
            }

            File.WriteAllText(path, root.ToString(Formatting.Indented), new UTF8Encoding(false));
        }
    }
}
