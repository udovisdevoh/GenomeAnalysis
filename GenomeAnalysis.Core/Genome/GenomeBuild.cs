namespace GenomeAnalysis.Core.Genome
{
    /// <summary>
    /// The reference assembly a file's coordinates are expressed against.
    /// </summary>
    /// <remarks>
    /// A position is meaningless without its build: the same variant sits at
    /// different coordinates in GRCh37 and GRCh38. The build is read from the file
    /// header and carried on the model rather than assumed, because consumer files
    /// span both depending on when they were generated.
    /// </remarks>
    public enum GenomeBuild
    {
        /// <summary>The header did not state a build. Do not guess one.</summary>
        Unknown = 0,

        /// <summary>GRCh37, also published as hg19. Common in older files.</summary>
        GRCh37 = 37,

        /// <summary>GRCh38, also published as hg38.</summary>
        GRCh38 = 38
    }
}
