using System;

namespace GenomeAnalysis.Core.Annotations
{
    /// <summary>
    /// Where a piece of annotation came from and under what licence.
    /// </summary>
    /// <remarks>
    /// Attribution is not decorative. SNPedia is CC BY-NC-SA 3.0 US, which makes
    /// attribution a licence condition, and every claim shown to the user has to
    /// be traceable back to a citable source. Annotations without attribution must
    /// not be displayed.
    /// </remarks>
    public sealed class SourceAttribution
    {
        public SourceAttribution(string sourceName, string licence, string? licenceUrl, string? recordUrl)
        {
            if (string.IsNullOrWhiteSpace(sourceName))
            {
                throw new ArgumentException("Source name is required.", nameof(sourceName));
            }

            SourceName = sourceName;
            Licence = licence;
            LicenceUrl = licenceUrl;
            RecordUrl = recordUrl;
        }

        public string SourceName { get; }

        public string Licence { get; }

        public string? LicenceUrl { get; }

        /// <summary>Link to the specific record, for drill-down from the report.</summary>
        public string? RecordUrl { get; }

        public static SourceAttribution Snpedia(string? pageTitle = null) =>
            new SourceAttribution(
                "SNPedia",
                "CC BY-NC-SA 3.0 US",
                "https://creativecommons.org/licenses/by-nc-sa/3.0/us/",
                pageTitle == null ? "https://www.snpedia.com" : "https://www.snpedia.com/index.php/" + pageTitle);

        public static SourceAttribution ClinVar(string? variationId = null) =>
            new SourceAttribution(
                "ClinVar (NCBI)",
                "Public domain",
                null,
                variationId == null
                    ? "https://www.ncbi.nlm.nih.gov/clinvar/"
                    : "https://www.ncbi.nlm.nih.gov/clinvar/variation/" + variationId);

        public static SourceAttribution GnomAd(string? rsId = null) =>
            new SourceAttribution(
                "gnomAD (Broad Institute)",
                "CC0",
                "https://creativecommons.org/publicdomain/zero/1.0/",
                rsId == null ? "https://gnomad.broadinstitute.org" : "https://gnomad.broadinstitute.org/variant/" + rsId);

        public static SourceAttribution Ensembl(string? rsId = null) =>
            new SourceAttribution(
                "Ensembl",
                "Apache 2.0 / open data",
                null,
                rsId == null ? "https://rest.ensembl.org" : "https://www.ensembl.org/Homo_sapiens/Variation/Explore?v=" + rsId);

        public static SourceAttribution DbSnp(string? rsId = null) =>
            new SourceAttribution(
                "dbSNP (NCBI)",
                "Public domain",
                null,
                rsId == null ? "https://www.ncbi.nlm.nih.gov/snp/" : "https://www.ncbi.nlm.nih.gov/snp/" + rsId);

        /// <summary>
        /// MyVariant.info aggregates other databases rather than curating its own.
        /// Where a claim originates from ClinVar or gnomAD, attribute it to those
        /// instead — this factory is for the aggregate record itself.
        /// </summary>
        public static SourceAttribution MyVariant(string? rsId = null) =>
            new SourceAttribution(
                "MyVariant.info (BioThings)",
                "Aggregated; per-source licences apply",
                null,
                rsId == null ? "https://myvariant.info" : "https://myvariant.info/v1/query?q=dbsnp.rsid:" + rsId);

        public override string ToString() => SourceName + " (" + Licence + ")";
    }
}
