using System;

namespace GenomeAnalysis.Core.Genome
{
    /// <summary>
    /// A chromosome identifier: autosomes 1-22, the sex chromosomes X and Y, and
    /// the mitochondrial genome MT.
    /// </summary>
    /// <remarks>
    /// Providers spell these inconsistently: 23andMe writes <c>MT</c>, some files
    /// use <c>M</c>, <c>25</c> or <c>XY</c> (the pseudoautosomal region). Parsing
    /// is centralised here so the rest of the domain never sees provider spelling.
    /// </remarks>
    public readonly struct Chromosome : IEquatable<Chromosome>
    {
        private readonly byte _code;

        private const byte XCode = 23;
        private const byte YCode = 24;
        private const byte MitochondrialCode = 25;
        private const byte PseudoautosomalCode = 26;

        private Chromosome(byte code)
        {
            _code = code;
        }

        public static Chromosome X => new Chromosome(XCode);

        public static Chromosome Y => new Chromosome(YCode);

        public static Chromosome Mitochondrial => new Chromosome(MitochondrialCode);

        /// <summary>The pseudoautosomal region shared by X and Y.</summary>
        public static Chromosome Pseudoautosomal => new Chromosome(PseudoautosomalCode);

        public static Chromosome Autosome(int number)
        {
            if (number < 1 || number > 22)
            {
                throw new ArgumentOutOfRangeException(nameof(number), number, "Autosomes are numbered 1 to 22.");
            }

            return new Chromosome((byte)number);
        }

        public bool IsAutosome => _code >= 1 && _code <= 22;

        public bool IsSexChromosome => _code == XCode || _code == YCode || _code == PseudoautosomalCode;

        public bool IsMitochondrial => _code == MitochondrialCode;

        /// <summary>
        /// True where a single allele is expected rather than two. Y and MT are
        /// always hemizygous; X is hemizygous in males, which cannot be determined
        /// from a single call and is therefore not decided here.
        /// </summary>
        public bool IsAlwaysHemizygous => _code == YCode || _code == MitochondrialCode;

        /// <summary>
        /// True where a single-allele call is legitimate rather than malformed.
        /// Includes X, because a male sample reports one allele there.
        /// </summary>
        public bool AllowsHemizygousCall => IsAlwaysHemizygous || _code == XCode;

        public static bool TryParse(string? raw, out Chromosome chromosome)
        {
            chromosome = default;

            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            var token = raw!.Trim().ToUpperInvariant();

            if (token.StartsWith("CHR", StringComparison.Ordinal))
            {
                token = token.Substring(3);
            }

            switch (token)
            {
                case "X":
                    chromosome = X;
                    return true;
                case "Y":
                    chromosome = Y;
                    return true;
                case "M":
                case "MT":
                    chromosome = Mitochondrial;
                    return true;
                case "XY":
                case "PAR":
                    chromosome = Pseudoautosomal;
                    return true;
            }

            if (!int.TryParse(token, out var number))
            {
                return false;
            }

            // Some providers number the non-autosomes rather than naming them.
            switch (number)
            {
                case XCode:
                    chromosome = X;
                    return true;
                case YCode:
                    chromosome = Y;
                    return true;
                case MitochondrialCode:
                    chromosome = Mitochondrial;
                    return true;
            }

            if (number < 1 || number > 22)
            {
                return false;
            }

            chromosome = new Chromosome((byte)number);
            return true;
        }

        public override string ToString()
        {
            switch (_code)
            {
                case XCode: return "X";
                case YCode: return "Y";
                case MitochondrialCode: return "MT";
                case PseudoautosomalCode: return "XY";
                default: return _code.ToString();
            }
        }

        public bool Equals(Chromosome other) => _code == other._code;

        public override bool Equals(object? obj) => obj is Chromosome other && Equals(other);

        public override int GetHashCode() => _code.GetHashCode();

        public static bool operator ==(Chromosome left, Chromosome right) => left.Equals(right);

        public static bool operator !=(Chromosome left, Chromosome right) => !left.Equals(right);
    }
}
