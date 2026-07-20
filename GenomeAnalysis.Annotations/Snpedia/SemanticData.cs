using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace GenomeAnalysis.Annotations.Snpedia
{
    /// <summary>
    /// The Semantic MediaWiki properties attached to one wiki page, as returned by
    /// <c>action=browsebysubject</c>.
    /// </summary>
    /// <remarks>
    /// Reading the semantic properties is deliberate: the same values could be
    /// scraped out of the wikitext with regular expressions, but that breaks the
    /// moment an editor reformats a template. Properties are the structured
    /// interface the wiki actually offers.
    /// </remarks>
    public sealed class SemanticData
    {
        private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _properties;

        private SemanticData(IReadOnlyDictionary<string, IReadOnlyList<string>> properties, string subject)
        {
            _properties = properties;
            Subject = subject;
        }

        /// <summary>The page these properties belong to, with SMW's suffix stripped.</summary>
        public string Subject { get; }

        public bool IsEmpty => _properties.Count == 0;

        public IEnumerable<string> PropertyNames => _properties.Keys;

        /// <summary>
        /// Parses a <c>browsebysubject</c> response. Property names are matched
        /// case-insensitively, because SNPedia's own casing is not consistent.
        /// </summary>
        public static SemanticData Parse(string json)
        {
            var properties = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            var subject = string.Empty;

            if (string.IsNullOrWhiteSpace(json))
            {
                return new SemanticData(properties, subject);
            }

            JObject root;

            try
            {
                root = JObject.Parse(json);
            }
            catch (Newtonsoft.Json.JsonException)
            {
                return new SemanticData(properties, subject);
            }

            var query = root["query"];

            if (query == null)
            {
                return new SemanticData(properties, subject);
            }

            subject = StripSubjectSuffix(query["subject"]?.Value<string>() ?? string.Empty);

            if (!(query["data"] is JArray data))
            {
                return new SemanticData(properties, subject);
            }

            foreach (var entry in data.OfType<JObject>())
            {
                var name = entry["property"]?.Value<string>();

                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var values = new List<string>();

                if (entry["dataitem"] is JArray items)
                {
                    foreach (var item in items.OfType<JObject>())
                    {
                        var value = item["item"]?.Value<string>();

                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            values.Add(StripSubjectSuffix(value!));
                        }
                    }
                }

                if (values.Count > 0)
                {
                    properties[name!] = values;
                }
            }

            return new SemanticData(properties, subject);
        }

        /// <summary>
        /// SMW suffixes page references with serialisation metadata such as
        /// <c>#0##</c>. Strip it to recover the plain page title.
        /// </summary>
        private static string StripSubjectSuffix(string value)
        {
            var hash = value.IndexOf('#');
            var stripped = hash >= 0 ? value.Substring(0, hash) : value;
            return stripped.Replace('_', ' ').Trim();
        }

        public string? GetString(string propertyName)
        {
            return _properties.TryGetValue(propertyName, out var values) && values.Count > 0
                ? values[0]
                : null;
        }

        public IReadOnlyList<string> GetStrings(string propertyName)
        {
            return _properties.TryGetValue(propertyName, out var values)
                ? values
                : Array.Empty<string>();
        }

        public double? GetDouble(string propertyName)
        {
            var raw = GetString(propertyName);

            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? value
                : (double?)null;
        }

        /// <summary>All property values across every property, for discovery.</summary>
        public IEnumerable<string> AllValues => _properties.Values.SelectMany(v => v);
    }
}
