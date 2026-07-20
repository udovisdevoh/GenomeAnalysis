using System;
using System.Collections.Generic;
using System.Linq;
using GenomeAnalysis.Core.Annotations;
using GenomeAnalysis.Core.Genome;

namespace GenomeAnalysis.Annotations.Snpedia
{
    /// <summary>
    /// Turns SNPedia semantic properties into domain annotations.
    /// </summary>
    /// <remarks>
    /// Kept separate from the HTTP client so the mapping can be tested against
    /// recorded fixtures without a network call.
    /// </remarks>
    public static class SnpediaMapper
    {
        public const string OrientationProperty = "Orientation";
        public const string StabilizedOrientationProperty = "StabilizedOrientation";
        public const string SummaryProperty = "Summary";
        public const string MagnitudeProperty = "Magnitude";
        public const string ReputeProperty = "Repute";
        public const string GeneProperty = "Gene";

        /// <summary>
        /// Reads a SNPedia orientation value. Anything unrecognised — including a
        /// missing property — maps to <see cref="Strand.Unknown"/> so that
        /// downstream code refuses to guess rather than defaulting to plus.
        /// </summary>
        public static Strand ParseStrand(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return Strand.Unknown;
            }

            switch (value!.Trim().ToLowerInvariant())
            {
                case "plus":
                case "+":
                case "forward":
                    return Strand.Plus;
                case "minus":
                case "-":
                case "reverse":
                    return Strand.Minus;
                default:
                    return Strand.Unknown;
            }
        }

        public static Repute ParseRepute(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return Repute.NotStated;
            }

            switch (value!.Trim().ToLowerInvariant())
            {
                case "good":
                    return Repute.Good;
                case "bad":
                    return Repute.Bad;
                default:
                    return Repute.NotStated;
            }
        }

        /// <summary>
        /// Finds the genotype pages a SNP page links to, by recognising their
        /// titles rather than by trusting a particular property name. SNPedia
        /// exposes these through <c>Geno1</c>/<c>Geno2</c>/<c>Geno3</c>, but
        /// discovering them by shape survives template changes.
        /// </summary>
        public static IReadOnlyList<string> DiscoverGenotypePageTitles(SemanticData snpPage)
        {
            if (snpPage == null)
            {
                throw new ArgumentNullException(nameof(snpPage));
            }

            return snpPage.AllValues
                .Where(SnpediaPageNames.IsGenotypePageTitle)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Builds the annotation for a single genotype page. Returns <c>null</c>
        /// when the page title is an indel genotype this tool does not interpret.
        /// </summary>
        public static GenotypeAnnotation? MapGenotypePage(string pageTitle, SemanticData genotypePage)
        {
            if (!SnpediaPageNames.TryParseGenotypePage(pageTitle, out _, out var genotype))
            {
                return null;
            }

            return new GenotypeAnnotation(
                genotype,
                genotypePage.GetString(SummaryProperty),
                genotypePage.GetDouble(MagnitudeProperty),
                ParseRepute(genotypePage.GetString(ReputeProperty)),
                SourceAttribution.Snpedia(pageTitle));
        }

        /// <summary>
        /// Assembles a variant annotation from a SNP page and the genotype pages
        /// it links to.
        /// </summary>
        public static VariantAnnotation MapSnpPage(
            string rsId,
            SemanticData snpPage,
            IEnumerable<KeyValuePair<string, SemanticData>> genotypePages)
        {
            if (snpPage == null)
            {
                throw new ArgumentNullException(nameof(snpPage));
            }

            var genotypes = new List<GenotypeAnnotation>();

            foreach (var page in genotypePages ?? Enumerable.Empty<KeyValuePair<string, SemanticData>>())
            {
                var mapped = MapGenotypePage(page.Key, page.Value);

                if (mapped != null)
                {
                    genotypes.Add(mapped);
                }
            }

            return new VariantAnnotation(
                rsId,
                ParseStrand(snpPage.GetString(OrientationProperty)),
                ParseStrand(snpPage.GetString(StabilizedOrientationProperty)),
                genotypes,
                snpPage.GetString(SummaryProperty),
                SourceAttribution.Snpedia(SnpediaPageNames.ForSnp(rsId)),
                snpPage.GetString(GeneProperty));
        }
    }
}
