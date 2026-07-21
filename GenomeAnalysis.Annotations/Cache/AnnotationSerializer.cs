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
        public const int SchemaVersion = 4;

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
                ["alleles"] = string.Join("/", annotation.KnownAlleles.Select(a => a.ToChar())),
                ["mergedRsIds"] = new JArray(annotation.MergedRsIds),
                ["consequence"] = annotation.MostSevereConsequence,
                ["referenceAllele"] = annotation.ReferenceAllele?.ToChar().ToString(),
                ["attribution"] = AttributionToJson(annotation.Attribution),
                ["genotypes"] = new JArray(annotation.Genotypes.Select(GenotypeToJson)),
                ["traits"] = new JArray(annotation.TraitAssociations.Select(TraitToJson))
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
                payload["maf"]?.Value<double?>(),
                ParseAlleles(payload["alleles"]?.Value<string>()),
                (payload["mergedRsIds"] as JArray)?.Select(t => t.Value<string>())
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => s!)
                    .ToList(),
                payload["consequence"]?.Value<string>(),
                (payload["traits"] as JArray)?.OfType<JObject>()
                    .Select(TraitFromJson)
                    .Where(t => t != null)
                    .Select(t => t!)
                    .ToList(),
                ParseReferenceAllele(payload["referenceAllele"]?.Value<string>()));
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

        private static JObject TraitToJson(TraitAssociation trait) => new JObject
        {
            ["trait"] = trait.Trait,
            ["traitUri"] = trait.TraitUri,
            ["oddsRatio"] = trait.OddsRatio,
            ["beta"] = trait.Beta,
            ["betaUnit"] = trait.BetaUnit,
            ["pValue"] = trait.PValue,
            ["riskAllele"] = trait.RiskAllele,
            ["pubMedId"] = trait.PubMedId,
            ["sampleSize"] = trait.SampleSize,
            ["attribution"] = AttributionToJson(trait.Attribution)
        };

        private static TraitAssociation? TraitFromJson(JObject json)
        {
            var trait = json["trait"]?.Value<string>();

            if (string.IsNullOrWhiteSpace(trait))
            {
                return null;
            }

            var attribution = AttributionFromJson(json["attribution"] as JObject);

            if (attribution == null)
            {
                return null;
            }

            return new TraitAssociation(
                trait!,
                json["oddsRatio"]?.Value<double?>(),
                json["beta"]?.Value<double?>(),
                json["betaUnit"]?.Value<string>(),
                json["pValue"]?.Value<double?>(),
                json["riskAllele"]?.Value<string>(),
                json["pubMedId"]?.Value<string>(),
                json["sampleSize"]?.Value<int?>(),
                attribution,
                json["traitUri"]?.Value<string>());
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

        private static IReadOnlyCollection<Nucleotide> ParseAlleles(string? alleleString)
        {
            var alleles = new List<Nucleotide>();

            if (string.IsNullOrWhiteSpace(alleleString))
            {
                return alleles;
            }

            foreach (var part in alleleString!.Split('/'))
            {
                var token = part.Trim();

                if (token.Length == 1 && NucleotideExtensions.TryParse(token[0], out var nucleotide))
                {
                    alleles.Add(nucleotide);
                }
            }

            return alleles.Distinct().ToList();
        }

        private static Nucleotide? ParseReferenceAllele(string? value)
        {
            if (string.IsNullOrWhiteSpace(value) || value!.Length != 1)
            {
                return null;
            }

            return NucleotideExtensions.TryParse(value[0], out var nucleotide) ? nucleotide : (Nucleotide?)null;
        }

        private static Strand ParseStrand(string? value) => ParseEnum(value, Strand.Unknown);

        private static TEnum ParseEnum<TEnum>(string? value, TEnum fallback) where TEnum : struct
        {
            return Enum.TryParse<TEnum>(value, ignoreCase: true, result: out var parsed) ? parsed : fallback;
        }
    }
}
