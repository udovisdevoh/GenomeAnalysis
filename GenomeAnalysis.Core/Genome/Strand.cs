namespace GenomeAnalysis.Core.Genome
{
    /// <summary>
    /// Which DNA strand a genotype is reported on.
    /// </summary>
    /// <remarks>
    /// Consumer test providers and annotation sources do not always report on the
    /// same strand, so a genotype is only meaningful together with its strand.
    /// SNPedia exposes this as two separate properties: <c>Orientation</c> (the
    /// orientation in the current reference build) and <c>StabilizedOrientation</c>
    /// (the orientation consistent with the linked genotype pages). Matching a
    /// genotype against a genotype page must use <c>StabilizedOrientation</c>.
    /// </remarks>
    public enum Strand
    {
        /// <summary>Not reported by the source. Never assume a default.</summary>
        Unknown = 0,
        Plus = 1,
        Minus = 2
    }
}
