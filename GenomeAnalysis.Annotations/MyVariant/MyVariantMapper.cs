using System;
using System.Collections.Generic;
using System.Linq;
using GenomeAnalysis.Core.Annotations;
using GenomeAnalysis.Core.Genome;
using Newtonsoft.Json.Linq;

namespace GenomeAnalysis.Annotations.MyVariant
{
    /// <summary>
    /// Maps MyVariant.info hits onto domain annotations.
    /// </summary>
    /// <remarks>
    /// Separated from the HTTP client so it can be tested against recorded
    /// responses without a network call.
    /// </remarks>
    public static class MyVariantMapper
    {
        /// <summary>The fields to request. Asking for everything wastes bandwidth on both ends.</summary>
        public const string RequestedFields =
            "dbsnp.rsid,clinvar.variant_id,clinvar.rcv,clinvar.gene.symbol,gnomad_genome.af.af,dbnsfp.genename";

        /// <summary>
        /// Maps one hit. Returns <c>null</c> for a <c>notfound</c> entry, which is
        /// an ordinary outcome for provider-internal or withdrawn identifiers.
        /// </summary>
        public static VariantAnnotation? MapHit(JObject hit)
        {
            if (hit == null)
            {
                return null;
            }

            if (hit["notfound"]?.Value<bool>() == true)
            {
                return null;
            }

            var rsId = hit.SelectToken("dbsnp.rsid")?.Value<string>()
                       ?? hit["query"]?.Value<string>();

            if (string.IsNullOrWhiteSpace(rsId))
            {
                return null;
            }

            var gene = hit.SelectToken("clinvar.gene.symbol")?.Value<string>()
                       ?? FirstScalar(hit.SelectToken("dbnsfp.genename"));

            return new VariantAnnotation(
                rsId!,
                // MyVariant reports on the reference (plus) strand of the build.
                // It carries no notion of SNPedia's stabilized orientation, so the
                // stabilized value stays Unknown rather than being assumed.
                Strand.Plus,
                Strand.Unknown,
                genotypes: null,
                summary: null,
                attribution: SourceAttribution.MyVariant(rsId),
                geneSymbol: gene,
                clinical: MapClinical(hit),
                minorAlleleFrequency: hit.SelectToken("gnomad_genome.af.af")?.Value<double?>());
        }

        private static ClinicalAnnotation? MapClinical(JObject hit)
        {
            var clinvar = hit["clinvar"] as JObject;

            if (clinvar == null)
            {
                return null;
            }

            // rcv is an object when there is one submission and an array when
            // there are several. Both shapes occur in real responses.
            var records = AsArray(clinvar["rcv"]).OfType<JObject>().ToList();

            if (records.Count == 0)
            {
                return null;
            }

            var significances = records
                .Select(r => ParseSignificance(r["clinical_significance"]?.Value<string>()))
                .ToList();

            var reviewStatuses = records
                .Select(r => ParseReviewStatus(r["review_status"]?.Value<string>()))
                .ToList();

            var conditions = records
                .SelectMany(r => ExtractConditionNames(r["conditions"]))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var variationId = clinvar["variant_id"]?.ToString();

            return new ClinicalAnnotation(
                ReconcileSignificance(significances),
                // Take the weakest review status across submissions rather than the
                // strongest. Presenting a variant at the level of its best-reviewed
                // submission overstates how settled the interpretation is.
                reviewStatuses.Count == 0 ? ClinVarReviewStatus.NoAssertionCriteria : reviewStatuses.Min(),
                conditions,
                SourceAttribution.ClinVar(variationId),
                variationId);
        }

        /// <summary>
        /// Combines the classifications of several submissions. Genuine
        /// disagreement is reported as a conflict rather than resolved by picking
        /// the most alarming or the most reassuring value.
        /// </summary>
        private static ClinicalSignificance ReconcileSignificance(IReadOnlyList<ClinicalSignificance> values)
        {
            var meaningful = values.Where(v => v != ClinicalSignificance.NotProvided).Distinct().ToList();

            if (meaningful.Count == 0)
            {
                return ClinicalSignificance.NotProvided;
            }

            if (meaningful.Count == 1)
            {
                return meaningful[0];
            }

            if (meaningful.Contains(ClinicalSignificance.ConflictingInterpretations))
            {
                return ClinicalSignificance.ConflictingInterpretations;
            }

            var pathogenicSide = meaningful.Any(v =>
                v == ClinicalSignificance.Pathogenic || v == ClinicalSignificance.LikelyPathogenic);

            var benignSide = meaningful.Any(v =>
                v == ClinicalSignificance.Benign || v == ClinicalSignificance.LikelyBenign);

            if (pathogenicSide && benignSide)
            {
                return ClinicalSignificance.ConflictingInterpretations;
            }

            // Same side of the line, differing only in confidence: keep the
            // stronger of the two, which is the more cautious reading.
            return meaningful.Max();
        }

        public static ClinicalSignificance ParseSignificance(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return ClinicalSignificance.NotProvided;
            }

            var normalized = value!.Trim().ToLowerInvariant();

            if (normalized.Contains("conflicting"))
            {
                return ClinicalSignificance.ConflictingInterpretations;
            }

            if (normalized.Contains("likely pathogenic"))
            {
                return ClinicalSignificance.LikelyPathogenic;
            }

            if (normalized.Contains("pathogenic"))
            {
                return ClinicalSignificance.Pathogenic;
            }

            if (normalized.Contains("likely benign"))
            {
                return ClinicalSignificance.LikelyBenign;
            }

            if (normalized.Contains("benign"))
            {
                return ClinicalSignificance.Benign;
            }

            if (normalized.Contains("uncertain"))
            {
                return ClinicalSignificance.UncertainSignificance;
            }

            if (normalized.Contains("drug response"))
            {
                return ClinicalSignificance.DrugResponse;
            }

            if (normalized.Contains("risk factor"))
            {
                return ClinicalSignificance.RiskFactor;
            }

            if (normalized.Contains("protective"))
            {
                return ClinicalSignificance.Protective;
            }

            if (normalized.Contains("association"))
            {
                return ClinicalSignificance.Association;
            }

            return ClinicalSignificance.Other;
        }

        public static ClinVarReviewStatus ParseReviewStatus(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return ClinVarReviewStatus.NoAssertionCriteria;
            }

            var normalized = value!.Trim().ToLowerInvariant();

            if (normalized.Contains("practice guideline"))
            {
                return ClinVarReviewStatus.PracticeGuideline;
            }

            if (normalized.Contains("expert panel"))
            {
                return ClinVarReviewStatus.ExpertPanel;
            }

            if (normalized.Contains("conflicting"))
            {
                return ClinVarReviewStatus.ConflictingSubmissions;
            }

            if (normalized.Contains("multiple submitters"))
            {
                return ClinVarReviewStatus.MultipleSubmittersNoConflicts;
            }

            if (normalized.Contains("single submitter"))
            {
                return ClinVarReviewStatus.SingleSubmitter;
            }

            return ClinVarReviewStatus.NoAssertionCriteria;
        }

        private static IEnumerable<string> ExtractConditionNames(JToken? conditions)
        {
            foreach (var entry in AsArray(conditions))
            {
                var name = entry is JObject obj ? obj["name"]?.Value<string>() : entry?.Value<string>();

                if (!string.IsNullOrWhiteSpace(name))
                {
                    yield return name!.Trim();
                }
            }
        }

        /// <summary>
        /// MyVariant collapses single-element arrays into bare objects. Normalising
        /// both shapes here keeps that quirk out of the calling code.
        /// </summary>
        private static IEnumerable<JToken> AsArray(JToken? token)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return Enumerable.Empty<JToken>();
            }

            return token is JArray array ? (IEnumerable<JToken>)array.Children() : new[] { token };
        }

        private static string? FirstScalar(JToken? token)
        {
            var first = AsArray(token).FirstOrDefault();
            return first?.Type == JTokenType.String ? first.Value<string>() : first?.ToString();
        }
    }
}
