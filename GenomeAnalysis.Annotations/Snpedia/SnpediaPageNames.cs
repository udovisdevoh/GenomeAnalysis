using System;
using System.Text.RegularExpressions;
using GenomeAnalysis.Core.Genome;

namespace GenomeAnalysis.Annotations.Snpedia
{
    /// <summary>
    /// SNPedia page-title conventions.
    /// </summary>
    /// <remarks>
    /// A SNP page is titled with the rsID capitalised MediaWiki-style — leading
    /// capital, remainder lowercase — giving <c>Rs53576</c>. A genotype page
    /// appends the alleles in parentheses separated by a semicolon:
    /// <c>Rs53576(A;A)</c>.
    /// </remarks>
    public static class SnpediaPageNames
    {
        private static readonly Regex GenotypePageTitle = new Regex(
            @"^(?<rs>[Rr][Ss]\d+)\(\s*(?<a1>[A-Za-z\-]+)\s*;\s*(?<a2>[A-Za-z\-]+)\s*\)$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex SnpPageTitle = new Regex(
            @"^[Rr][Ss]\d+$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>Page title for a SNP, e.g. <c>rs53576</c> becomes <c>Rs53576</c>.</summary>
        public static string ForSnp(string rsId)
        {
            if (string.IsNullOrWhiteSpace(rsId))
            {
                throw new ArgumentException("rsId is required.", nameof(rsId));
            }

            var trimmed = rsId.Trim();

            if (!SnpPageTitle.IsMatch(trimmed))
            {
                throw new ArgumentException(
                    "Not an rsID: '" + trimmed + "'. Provider-internal identifiers have no SNPedia page.",
                    nameof(rsId));
            }

            return char.ToUpperInvariant(trimmed[0]) + trimmed.Substring(1).ToLowerInvariant();
        }

        /// <summary>Page title for a genotype, e.g. <c>Rs53576(A;G)</c>.</summary>
        public static string ForGenotype(string rsId, Genotype genotype)
        {
            return ForSnp(rsId) + genotype.ToSnpediaNotation();
        }

        public static bool IsSnpPageTitle(string? title) =>
            !string.IsNullOrWhiteSpace(title) && SnpPageTitle.IsMatch(title!.Trim());

        public static bool IsGenotypePageTitle(string? title) =>
            !string.IsNullOrWhiteSpace(title) && GenotypePageTitle.IsMatch(title!.Trim());

        /// <summary>
        /// Reads a genotype page title back into its parts. Returns false for
        /// insertion/deletion genotypes such as <c>Rs1799752(I;D)</c>, which this
        /// tool does not interpret.
        /// </summary>
        public static bool TryParseGenotypePage(string? title, out string rsId, out Genotype genotype)
        {
            rsId = string.Empty;
            genotype = default;

            if (string.IsNullOrWhiteSpace(title))
            {
                return false;
            }

            var match = GenotypePageTitle.Match(title!.Trim());

            if (!match.Success)
            {
                return false;
            }

            var allele1 = match.Groups["a1"].Value;
            var allele2 = match.Groups["a2"].Value;

            if (!Genotype.TryParsePair(allele1, allele2, out genotype))
            {
                return false;
            }

            rsId = match.Groups["rs"].Value.ToLowerInvariant();
            return true;
        }
    }
}
