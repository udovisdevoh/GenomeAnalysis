using System.Collections.Generic;
using GenomeAnalysis.Core.Genome;

namespace GenomeAnalysis.Core.Parsing
{
    /// <summary>
    /// Consumer providers whose raw export formats this tool reads.
    /// </summary>
    public enum GenomeFileProvider
    {
        Unknown = 0,
        TwentyThreeAndMe = 1,
        AncestryDna = 2,
        MyHeritage = 3,
        FamilyTreeDna = 4
    }

    /// <summary>
    /// What the file's own header says about itself.
    /// </summary>
    /// <remarks>
    /// The provider is detected from the header text, never from the file
    /// extension: every one of these formats ships as <c>.txt</c>, and users rename
    /// them freely.
    /// </remarks>
    public sealed class GenomeFileHeader
    {
        public GenomeFileHeader(
            GenomeFileProvider provider,
            GenomeBuild build,
            IReadOnlyList<string> columns,
            IReadOnlyList<string> warnings)
        {
            Provider = provider;
            Build = build;
            Columns = columns;
            Warnings = warnings;
        }

        public GenomeFileProvider Provider { get; }

        /// <summary>
        /// The reference build the positions are expressed against. A position
        /// without its build is meaningless, so this travels with every call.
        /// </summary>
        public GenomeBuild Build { get; }

        /// <summary>Column names as found, used to locate fields by name rather than by index.</summary>
        public IReadOnlyList<string> Columns { get; }

        /// <summary>
        /// Things worth telling the user about the file itself — an unrecognised
        /// provider, a missing build. Surfaced, not swallowed.
        /// </summary>
        public IReadOnlyList<string> Warnings { get; }

        public bool IsRecognised => Provider != GenomeFileProvider.Unknown;
    }

    /// <summary>
    /// Counts gathered while streaming a file, for the report's "what was actually
    /// read" section.
    /// </summary>
    /// <remarks>
    /// Contains counts only — never a genotype. An array interrogates fixed
    /// positions, so knowing how many were readable is part of interpreting any
    /// absence of findings.
    /// </remarks>
    public sealed class ParseStatistics
    {
        public int TotalRows { get; internal set; }

        public int CalledGenotypes { get; internal set; }

        public int NoCalls { get; internal set; }

        public int MalformedRows { get; internal set; }

        /// <summary>Provider-internal identifiers, which no public database knows.</summary>
        public int ProviderInternalIds { get; internal set; }

        public int HemizygousCalls { get; internal set; }

        public override string ToString() =>
            TotalRows + " rows, " + CalledGenotypes + " called, " + NoCalls + " no-calls, " +
            MalformedRows + " malformed, " + ProviderInternalIds + " provider-internal ids";
    }
}
