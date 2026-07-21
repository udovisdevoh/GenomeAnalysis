using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using GenomeAnalysis.Core.Rules;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GenomeAnalysis.Annotations.Local
{
    /// <summary>
    /// Reads the declarative rule file.
    /// </summary>
    /// <remarks>
    /// Lives here rather than in Core so that Core keeps no file or JSON
    /// dependency: the engine receives rule objects and does not know where they
    /// came from, which is what lets the tests build rules inline.
    /// </remarks>
    public static class RuleLoader
    {
        public static IReadOnlyList<RuleDefinition> Load(string path)
        {
            return File.Exists(path)
                ? Parse(File.ReadAllText(path, Encoding.UTF8))
                : new List<RuleDefinition>();
        }

        public static IReadOnlyList<RuleDefinition> Parse(string json)
        {
            var rules = new List<RuleDefinition>();

            if (string.IsNullOrWhiteSpace(json))
            {
                return rules;
            }

            JObject root;

            try
            {
                root = JObject.Parse(json);
            }
            catch (JsonException)
            {
                return rules;
            }

            foreach (var entry in (root["rules"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                var rule = ParseRule(entry);

                if (rule != null)
                {
                    rules.Add(rule);
                }
            }

            return rules;
        }

        private static RuleDefinition? ParseRule(JObject entry)
        {
            var id = entry["id"]?.Value<string>();
            var name = entry["name"]?.Value<string>();

            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            if (!TryParseKind(entry["kind"]?.Value<string>(), out var kind))
            {
                // An unknown rule kind is skipped rather than guessed at: running a
                // rule the engine does not understand would produce a result nobody
                // can account for.
                return null;
            }

            var required = (entry["requiredMarkers"] as JArray)
                ?.Select(t => t.Value<string>())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s!.Trim().ToLowerInvariant())
                .ToList() ?? new List<string>();

            if (required.Count == 0)
            {
                return null;
            }

            var haplotypes = (entry["haplotypes"] as JArray)
                ?.OfType<JObject>()
                .Select(ParseHaplotype)
                .Where(h => h != null)
                .Select(h => h!)
                .ToList() ?? new List<HaplotypeDefinition>();

            var variants = (entry["variants"] as JArray)
                ?.OfType<JObject>()
                .Select(ParseVariant)
                .Where(v => v != null)
                .Select(v => v!)
                .ToList() ?? new List<RuleVariant>();

            var interpretations = new Dictionary<string, string>(StringComparer.Ordinal);

            if (entry["interpretations"] is JObject interpretationObject)
            {
                foreach (var property in interpretationObject.Properties())
                {
                    var text = property.Value?.Value<string>();

                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        interpretations[property.Name] = text!;
                    }
                }
            }

            return new RuleDefinition(
                id!,
                name!,
                kind,
                entry["gene"]?.Value<string>(),
                required,
                haplotypes,
                variants,
                interpretations,
                entry["note"]?.Value<string>());
        }

        private static HaplotypeDefinition? ParseHaplotype(JObject entry)
        {
            var name = entry["name"]?.Value<string>();

            if (string.IsNullOrWhiteSpace(name) || !(entry["alleles"] is JObject alleles))
            {
                return null;
            }

            var map = new Dictionary<string, char>(StringComparer.OrdinalIgnoreCase);

            foreach (var property in alleles.Properties())
            {
                var value = property.Value?.Value<string>();

                if (!string.IsNullOrWhiteSpace(value) && value!.Length == 1)
                {
                    map[property.Name.Trim().ToLowerInvariant()] = char.ToUpperInvariant(value[0]);
                }
            }

            return map.Count == 0 ? null : new HaplotypeDefinition(name!, map);
        }

        private static RuleVariant? ParseVariant(JObject entry)
        {
            var rsId = entry["rsId"]?.Value<string>();
            var riskAllele = entry["riskAllele"]?.Value<string>();
            var label = entry["label"]?.Value<string>();

            if (string.IsNullOrWhiteSpace(rsId) ||
                string.IsNullOrWhiteSpace(riskAllele) ||
                riskAllele!.Length != 1)
            {
                return null;
            }

            return new RuleVariant(
                rsId!.Trim().ToLowerInvariant(),
                char.ToUpperInvariant(riskAllele[0]),
                string.IsNullOrWhiteSpace(label) ? rsId! : label!);
        }

        /// <summary>
        /// Reads the pharmacogene tables the harvester produced from CPIC.
        /// </summary>
        public static IReadOnlyList<Pharmacogene> LoadPharmacogenes(string path)
        {
            return File.Exists(path)
                ? ParsePharmacogenes(File.ReadAllText(path, Encoding.UTF8))
                : new List<Pharmacogene>();
        }

        public static IReadOnlyList<Pharmacogene> ParsePharmacogenes(string json)
        {
            var genes = new List<Pharmacogene>();

            if (string.IsNullOrWhiteSpace(json))
            {
                return genes;
            }

            JObject root;

            try
            {
                root = JObject.Parse(json);
            }
            catch (JsonException)
            {
                return genes;
            }

            if (!(root["genes"] is JObject geneObject))
            {
                return genes;
            }

            foreach (var property in geneObject.Properties())
            {
                if (!(property.Value is JObject entry))
                {
                    continue;
                }

                var alleles = new List<StarAllele>();

                foreach (var allele in (entry["starAlleles"] as JArray)?.OfType<JObject>()
                                       ?? Enumerable.Empty<JObject>())
                {
                    var name = allele["name"]?.Value<string>();

                    if (string.IsNullOrWhiteSpace(name) || !(allele["definitions"] is JObject definitions))
                    {
                        continue;
                    }

                    var map = new Dictionary<string, char>(StringComparer.OrdinalIgnoreCase);

                    foreach (var definition in definitions.Properties())
                    {
                        var value = definition.Value?.Value<string>();

                        if (!string.IsNullOrWhiteSpace(value) && value!.Length == 1)
                        {
                            map[definition.Name.Trim().ToLowerInvariant()] = char.ToUpperInvariant(value[0]);
                        }
                    }

                    if (map.Count > 0)
                    {
                        alleles.Add(new StarAllele(name!, allele["function"]?.Value<string>(), map));
                    }
                }

                var rules = new List<(string, string, string)>();

                foreach (var rule in (entry["phenotypeRules"] as JArray)?.OfType<JObject>()
                                     ?? Enumerable.Empty<JObject>())
                {
                    var first = rule["function1"]?.Value<string>();
                    var second = rule["function2"]?.Value<string>();
                    var phenotype = rule["phenotype"]?.Value<string>();

                    if (!string.IsNullOrWhiteSpace(first) &&
                        !string.IsNullOrWhiteSpace(second) &&
                        !string.IsNullOrWhiteSpace(phenotype))
                    {
                        rules.Add((first!, second!, phenotype!));
                    }
                }

                if (alleles.Count > 0)
                {
                    genes.Add(new Pharmacogene(property.Name, alleles, rules));
                }
            }

            return genes;
        }

        private static bool TryParseKind(string? value, out RuleKind kind)
        {
            switch ((value ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "haplotype":
                    kind = RuleKind.Haplotype;
                    return true;
                case "compoundheterozygosity":
                case "compound-heterozygosity":
                    kind = RuleKind.CompoundHeterozygosity;
                    return true;
                default:
                    kind = default;
                    return false;
            }
        }
    }
}
