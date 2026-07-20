using System;
using System.Collections.Generic;
using System.Linq;
using GenomeAnalysis.Core.Annotations;
using GenomeAnalysis.Core.Genome;
using Newtonsoft.Json.Linq;

namespace GenomeAnalysis.Annotations.Cache
{
    /// <summary>
    /// Explicit JSON conversion for cached annotations.
    /// </summary>
    /// <remarks>
    /// Written by hand rather than by reflection so that Core stays free of
    /// serialisation attributes and a schema change fails loudly here instead of
    /// silently dropping fields.
    /// </remarks>
    public static class AnnotationSerializer
    {
        /// <summary>Bumped when the payload shape changes, to invalidate stale rows.</summary>
        public const int SchemaVersion = 1;

        public static string ToJson(VariantAnnotation annotation)
        {
            if (annotation == null)
            {
                throw new ArgumentNullException(nameof(annotation));
            }

            var payload = new JObject
            {
                ["v"] = SchemaVersion,
                ["rsId"] = annotation.RsId,
                ["orientation"] = annotation.Orientation.ToString(),
                ["stabilizedOrientation"] = annotation.StabilizedOrientation.ToString(),
                ["summary"] = annotation.Summary,
                ["gene"] = annotation.GeneSymbol,
                ["maf"] = annotation.MinorAlleleFrequency,
                ["attribution"] = AttributionToJson(annotation.Attribution),
                ["genotypes"] = new JArray(annotation.Genotypes.Select(GenotypeToJson))
            };

            if (annotation.Clinical != null)
            {
                payload["clinical"] = ClinicalToJson(annotation.Clinical);
            }

            return payload.ToString(Newtonsoft.Json.Formatting.None);
        }

        public static VariantAnnotation? FromJson(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            JObject payload;

            try
            {
                payload = JObject.Parse(json!);
            }
            catch (Newtonsoft.Json.JsonException)
            {
                return null;
            }

            if (payload["v"]?.Value<int>() != SchemaVersion)
            {
                // Written by an older layout: treat as a cache miss rather than
                // risk misreading fields.
                return null;
            }

            var rsId = payload["rsId"]?.Value<string>();

            if (string.IsNullOrWhiteSpace(rsId))
            {
                return null;
            }

            var attribution = AttributionFromJson(payload["attribution"] as JObject);

            if (attribution == null)
            {
                // Annotations without attribution must not be displayed, so a row
                // that lost it is unusable.
                return null;
            }

            return new VariantAnnotation(
                rsId!,
                ParseStrand(payload["orientation"]?.Value<string>()),
                ParseStrand(payload["stabilizedOrientation"]?.Value<string>()),
                (payload["genotypes"] as JArray)?.OfType<JObject>()
                    .Select(GenotypeFromJson)
                    .Where(g => g != null)
                    .Select(g => g!)
                    .ToList(),
                payload["summary"]?.Value<string>(),
                attribution,
                payload["gene"]?.Value<string>(),
                ClinicalFromJson(payload["clinical"] as JObject),
                payload["maf"]?.Value<double?>());
        }

        private static JObject GenotypeToJson(GenotypeAnnotation genotype) => new JObject
        {
            ["genotype"] = genotype.Genotype.ToString(),
            ["summary"] = genotype.Summary,
            ["magnitude"] = genotype.Magnitude,
            ["repute"] = genotype.Repute.ToString(),
            ["attribution"] = AttributionToJson(genotype.Attribution)
        };

        private static GenotypeAnnotation? GenotypeFromJson(JObject json)
        {
            if (!Genotype.TryParse(json["genotype"]?.Value<string>(), out var genotype))
            {
                return null;
            }

            var attribution = AttributionFromJson(json["attribution"] as JObject);

            if (attribution == null)
            {
                return null;
            }

            return new GenotypeAnnotation(
                genotype,
                json["summary"]?.Value<string>(),
                json["magnitude"]?.Value<double?>(),
                ParseEnum(json["repute"]?.Value<string>(), Repute.NotStated),
                attribution);
        }

        private static JObject ClinicalToJson(ClinicalAnnotation clinical) => new JObject
        {
            ["significance"] = clinical.Significance.ToString(),
            ["reviewStatus"] = clinical.ReviewStatus.ToString(),
            ["conditions"] = new JArray(clinical.Conditions),
            ["variationId"] = clinical.VariationId,
            ["lastEvaluated"] = clinical.LastEvaluated?.ToString("O"),
            ["attribution"] = AttributionToJson(clinical.Attribution)
        };

        private static ClinicalAnnotation? ClinicalFromJson(JObject? json)
        {
            if (json == null)
            {
                return null;
            }

            var attribution = AttributionFromJson(json["attribution"] as JObject);

            if (attribution == null)
            {
                return null;
            }

            DateTimeOffset? lastEvaluated = null;
            var rawDate = json["lastEvaluated"]?.Value<string>();

            if (!string.IsNullOrWhiteSpace(rawDate) &&
                DateTimeOffset.TryParse(rawDate, out var parsedDate))
            {
                lastEvaluated = parsedDate;
            }

            return new ClinicalAnnotation(
                ParseEnum(json["significance"]?.Value<string>(), ClinicalSignificance.NotProvided),
                ParseEnum(json["reviewStatus"]?.Value<string>(), ClinVarReviewStatus.NoAssertionCriteria),
                (json["conditions"] as JArray)?.Select(c => c.Value<string>())
                    .Where(c => !string.IsNullOrWhiteSpace(c))
                    .Select(c => c!)
                    .ToList(),
                attribution,
                json["variationId"]?.Value<string>(),
                lastEvaluated);
        }

        private static JObject AttributionToJson(SourceAttribution attribution) => new JObject
        {
            ["source"] = attribution.SourceName,
            ["licence"] = attribution.Licence,
            ["licenceUrl"] = attribution.LicenceUrl,
            ["recordUrl"] = attribution.RecordUrl
        };

        private static SourceAttribution? AttributionFromJson(JObject? json)
        {
            var source = json?["source"]?.Value<string>();

            if (string.IsNullOrWhiteSpace(source))
            {
                return null;
            }

            return new SourceAttribution(
                source!,
                json!["licence"]?.Value<string>() ?? string.Empty,
                json["licenceUrl"]?.Value<string>(),
                json["recordUrl"]?.Value<string>());
        }

        private static Strand ParseStrand(string? value) => ParseEnum(value, Strand.Unknown);

        private static TEnum ParseEnum<TEnum>(string? value, TEnum fallback) where TEnum : struct
        {
            return Enum.TryParse<TEnum>(value, ignoreCase: true, result: out var parsed) ? parsed : fallback;
        }
    }
}
