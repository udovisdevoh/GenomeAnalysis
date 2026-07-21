using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using GenomeAnalysis.Core.Genome;

namespace GenomeAnalysis.Core.Parsing
{
    /// <summary>
    /// Streams a raw consumer DNA file into <see cref="MarkerCall"/> values.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These files run 600 000 to a million lines. Rows are yielded one at a time
    /// and never accumulated, so memory stays flat regardless of file size.
    /// </para>
    /// <para>
    /// Fields are located by column name rather than by position. The formats agree
    /// on the first three columns and diverge after that — AncestryDNA splits the
    /// genotype across <c>allele1</c> and <c>allele2</c> — and providers have
    /// reordered columns between export versions.
    /// </para>
    /// <para>
    /// The file is health data. Nothing here writes a genotype to a log or an
    /// exception message.
    /// </para>
    /// </remarks>
    public sealed class GenomeFileReader : IDisposable
    {
        private static readonly Regex BuildPattern = new Regex(
            @"build\s*(3[678])|GRCh(3[78])",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private readonly TextReader _reader;
        private readonly bool _ownsReader;
        private readonly int _rsIdColumn;
        private readonly int _chromosomeColumn;
        private readonly int _positionColumn;
        private readonly int _genotypeColumn;
        private readonly int _allele1Column;
        private readonly int _allele2Column;
        private readonly char _separator;
        private string? _firstDataLine;
        private bool _consumed;
        private bool _disposed;

        private GenomeFileReader(
            TextReader reader,
            bool ownsReader,
            GenomeFileHeader header,
            char separator,
            string? firstDataLine)
        {
            _reader = reader;
            _ownsReader = ownsReader;
            _separator = separator;
            _firstDataLine = firstDataLine;
            Header = header;
            Statistics = new ParseStatistics();

            _rsIdColumn = IndexOfAny(header.Columns, "rsid", "rs", "snp", "snpid", "marker");
            _chromosomeColumn = IndexOfAny(header.Columns, "chromosome", "chr");
            _positionColumn = IndexOfAny(header.Columns, "position", "pos");
            _genotypeColumn = IndexOfAny(header.Columns, "genotype", "result", "call");
            _allele1Column = IndexOfAny(header.Columns, "allele1");
            _allele2Column = IndexOfAny(header.Columns, "allele2");
        }

        public GenomeFileHeader Header { get; }

        public ParseStatistics Statistics { get; }

        public static GenomeFileReader Open(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("Path is required.", nameof(path));
            }

            if (!File.Exists(path))
            {
                throw new FileNotFoundException("Genome file not found.", path);
            }

            // Explicit buffer: the default is small for files this size.
            var stream = new StreamReader(path, System.Text.Encoding.UTF8, true, 65536);

            try
            {
                return Create(stream, ownsReader: true);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        public static GenomeFileReader Create(TextReader reader, bool ownsReader = false)
        {
            if (reader == null)
            {
                throw new ArgumentNullException(nameof(reader));
            }

            var metadata = new List<string>();
            var warnings = new List<string>();
            string? columnLine = null;
            string? firstDataLine = null;

            // Comment lines carry the provider name and the build. The last one is
            // the column header in 23andMe files; AncestryDNA puts its column header
            // on the first uncommented line instead.
            string? line;

            while ((line = reader.ReadLine()) != null)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                if (line[0] == '#')
                {
                    metadata.Add(line.TrimStart('#').Trim());
                    continue;
                }

                if (LooksLikeColumnHeader(line))
                {
                    columnLine = line;
                }
                else
                {
                    firstDataLine = line;
                }

                break;
            }

            // A 23andMe column header is the final comment line.
            if (columnLine == null && metadata.Count > 0 && LooksLikeColumnHeader(metadata[metadata.Count - 1]))
            {
                columnLine = metadata[metadata.Count - 1];
                metadata.RemoveAt(metadata.Count - 1);
            }

            var metadataText = string.Join(" ", metadata);
            var separator = DetectSeparator(columnLine ?? firstDataLine ?? string.Empty);
            var columns = columnLine == null
                ? new List<string>()
                : SplitLine(columnLine, separator).Select(NormaliseColumn).ToList();

            var provider = DetectProvider(metadataText, columns);
            var build = DetectBuild(metadataText);

            if (provider == GenomeFileProvider.Unknown)
            {
                warnings.Add(
                    "Provider not recognised from the header. Columns were matched by name, " +
                    "but nothing confirms the file's layout or reference build.");
            }

            if (build == GenomeBuild.Unknown)
            {
                warnings.Add(
                    "No reference build stated in the header. Positions cannot be compared " +
                    "across builds, so any position-based lookup is unsafe for this file.");
            }

            if (columns.Count == 0)
            {
                warnings.Add("No column header found; the file may be truncated or not a genome export.");
            }

            var header = new GenomeFileHeader(provider, build, columns, warnings);

            return new GenomeFileReader(reader, ownsReader, header, separator, firstDataLine);
        }

        /// <summary>
        /// Streams the calls. Enumerable once — the underlying reader moves forward
        /// and cannot rewind.
        /// </summary>
        public IEnumerable<MarkerCall> ReadCalls()
        {
            if (_consumed)
            {
                throw new InvalidOperationException(
                    "This reader has already been enumerated. Open the file again to re-read it.");
            }

            _consumed = true;

            if (_firstDataLine != null)
            {
                var first = ParseLine(_firstDataLine);
                _firstDataLine = null;

                if (first != null)
                {
                    yield return first;
                }
            }

            string? line;

            while ((line = _reader.ReadLine()) != null)
            {
                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }

                var call = ParseLine(line);

                if (call != null)
                {
                    yield return call;
                }
            }
        }

        private MarkerCall? ParseLine(string line)
        {
            Statistics.TotalRows++;

            var fields = SplitLine(line, _separator);

            if (_rsIdColumn < 0 || _rsIdColumn >= fields.Length)
            {
                Statistics.MalformedRows++;
                return null;
            }

            var markerId = fields[_rsIdColumn].Trim();

            if (markerId.Length == 0)
            {
                Statistics.MalformedRows++;
                return null;
            }

            if (!TryGetField(fields, _chromosomeColumn, out var chromosomeText) ||
                !Chromosome.TryParse(chromosomeText, out var chromosome))
            {
                Statistics.MalformedRows++;
                return null;
            }

            if (!TryGetField(fields, _positionColumn, out var positionText) ||
                !int.TryParse(positionText, out var position))
            {
                Statistics.MalformedRows++;
                return null;
            }

            var parsed = TryReadGenotype(fields, out var genotype, out var rawToken);

            if (!parsed)
            {
                Statistics.MalformedRows++;
                return null;
            }

            if (genotype.HasValue)
            {
                Statistics.CalledGenotypes++;

                if (genotype.Value.IsHemizygous)
                {
                    Statistics.HemizygousCalls++;
                }
            }
            else
            {
                Statistics.NoCalls++;
            }

            var call = new MarkerCall(markerId, chromosome, position, genotype, rawToken);

            if (!call.IsRsId)
            {
                Statistics.ProviderInternalIds++;
            }

            return call;
        }

        /// <summary>
        /// Reads the genotype from whichever layout this file uses. A no-call is a
        /// successful read that produced no genotype — distinct from a malformed row.
        /// </summary>
        private bool TryReadGenotype(string[] fields, out Genotype? genotype, out string? rawToken)
        {
            genotype = null;
            rawToken = null;

            // AncestryDNA: two allele columns.
            if (_allele1Column >= 0 && _allele2Column >= 0)
            {
                if (!TryGetField(fields, _allele1Column, out var allele1) ||
                    !TryGetField(fields, _allele2Column, out var allele2))
                {
                    return false;
                }

                if (Genotype.TryParsePair(allele1, allele2, out var pair))
                {
                    genotype = pair;
                    return true;
                }

                rawToken = allele1 + allele2;
                return true;
            }

            // 23andMe, MyHeritage, FTDNA: one genotype column.
            if (!TryGetField(fields, _genotypeColumn, out var token))
            {
                return false;
            }

            if (Genotype.TryParse(token, out var single))
            {
                genotype = single;
                return true;
            }

            rawToken = token;
            return true;
        }

        private static bool TryGetField(string[] fields, int index, out string value)
        {
            if (index < 0 || index >= fields.Length)
            {
                value = string.Empty;
                return false;
            }

            value = fields[index].Trim();
            return true;
        }

        private static string[] SplitLine(string line, char separator)
        {
            var fields = line.Split(separator);

            // MyHeritage and FTDNA quote every field.
            for (var i = 0; i < fields.Length; i++)
            {
                fields[i] = fields[i].Trim().Trim('"');
            }

            return fields;
        }

        private static char DetectSeparator(string line) => line.Contains("\t") ? '\t' : ',';

        private static bool LooksLikeColumnHeader(string line) =>
            line.IndexOf("rsid", StringComparison.OrdinalIgnoreCase) >= 0 ||
            line.IndexOf("snp", StringComparison.OrdinalIgnoreCase) >= 0;

        private static string NormaliseColumn(string column) =>
            column.Trim().Trim('"').Replace(" ", string.Empty).ToLowerInvariant();

        private static int IndexOfAny(IReadOnlyList<string> columns, params string[] candidates)
        {
            foreach (var candidate in candidates)
            {
                for (var i = 0; i < columns.Count; i++)
                {
                    if (string.Equals(columns[i], candidate, StringComparison.OrdinalIgnoreCase))
                    {
                        return i;
                    }
                }
            }

            return -1;
        }

        private static GenomeFileProvider DetectProvider(string metadata, IReadOnlyList<string> columns)
        {
            if (metadata.IndexOf("23andMe", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return GenomeFileProvider.TwentyThreeAndMe;
            }

            if (metadata.IndexOf("AncestryDNA", StringComparison.OrdinalIgnoreCase) >= 0 ||
                metadata.IndexOf("Ancestry.com", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return GenomeFileProvider.AncestryDna;
            }

            if (metadata.IndexOf("MyHeritage", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return GenomeFileProvider.MyHeritage;
            }

            if (metadata.IndexOf("Family Tree DNA", StringComparison.OrdinalIgnoreCase) >= 0 ||
                metadata.IndexOf("FTDNA", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return GenomeFileProvider.FamilyTreeDna;
            }

            // Fall back on layout: only AncestryDNA splits the alleles into two
            // columns, so that shape identifies it even from a stripped header.
            var hasAlleleColumns =
                columns.Any(c => string.Equals(c, "allele1", StringComparison.OrdinalIgnoreCase)) &&
                columns.Any(c => string.Equals(c, "allele2", StringComparison.OrdinalIgnoreCase));

            return hasAlleleColumns ? GenomeFileProvider.AncestryDna : GenomeFileProvider.Unknown;
        }

        private static GenomeBuild DetectBuild(string metadata)
        {
            var match = BuildPattern.Match(metadata);

            if (!match.Success)
            {
                return GenomeBuild.Unknown;
            }

            var value = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;

            switch (value)
            {
                // Providers write "build 36" for NCBI36, "build 37" for GRCh37 and
                // "build 38" for GRCh38.
                case "37":
                    return GenomeBuild.GRCh37;
                case "38":
                    return GenomeBuild.GRCh38;
                default:
                    return GenomeBuild.Unknown;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (_ownsReader)
            {
                _reader.Dispose();
            }
        }
    }
}
