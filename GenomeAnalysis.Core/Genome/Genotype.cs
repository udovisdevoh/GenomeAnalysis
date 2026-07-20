using System;
using System.Collections.Generic;

namespace GenomeAnalysis.Core.Genome
{
    /// <summary>
    /// An observed genotype at one position: two alleles on an autosome, or a
    /// single allele where the locus is hemizygous (Y, MT, and X in males).
    /// </summary>
    /// <remarks>
    /// A genotype carries no meaning without the <see cref="Strand"/> it was read
    /// from. This type deliberately does not store a strand: the strand belongs to
    /// the source that reported it, and reconciling two sources is the job of
    /// <see cref="Strands.StrandResolver"/>.
    /// </remarks>
    public readonly struct Genotype : IEquatable<Genotype>
    {
        private readonly Nucleotide _first;
        private readonly Nucleotide _second;
        private readonly bool _isDiploid;

        /// <summary>Creates a hemizygous genotype (a single allele).</summary>
        public Genotype(Nucleotide single)
        {
            _first = single;
            _second = single;
            _isDiploid = false;
        }

        /// <summary>Creates a diploid genotype (two alleles).</summary>
        public Genotype(Nucleotide first, Nucleotide second)
        {
            _first = first;
            _second = second;
            _isDiploid = true;
        }

        public Nucleotide First => _first;

        /// <summary>The second allele, or <c>null</c> when hemizygous.</summary>
        public Nucleotide? Second => _isDiploid ? _second : (Nucleotide?)null;

        public int AlleleCount => _isDiploid ? 2 : 1;

        public bool IsHemizygous => !_isDiploid;

        public bool IsHomozygous => _isDiploid && _first == _second;

        public bool IsHeterozygous => _isDiploid && _first != _second;

        public IReadOnlyList<Nucleotide> Alleles =>
            _isDiploid ? new[] { _first, _second } : new[] { _first };

        /// <summary>
        /// The same genotype read from the opposite DNA strand (A&lt;-&gt;T, C&lt;-&gt;G).
        /// </summary>
        public Genotype Complement()
        {
            return _isDiploid
                ? new Genotype(_first.Complement(), _second.Complement())
                : new Genotype(_first.Complement());
        }

        /// <summary>
        /// The same genotype with its alleles in a canonical (alphabetical) order,
        /// so that <c>A;G</c> and <c>G;A</c> compare and look up as one value.
        /// </summary>
        public Genotype Normalized()
        {
            if (!_isDiploid || _first <= _second)
            {
                return this;
            }

            return new Genotype(_second, _first);
        }

        /// <summary>
        /// True when this genotype is unchanged by complementing it. A heterozygous
        /// palindromic genotype such as <c>A;T</c> is its own complement, which is
        /// precisely why it stays usable when a homozygous one does not.
        /// </summary>
        public bool IsSelfComplementary()
        {
            return Complement().Normalized().Equals(Normalized());
        }

        /// <summary>
        /// SNPedia genotype-page notation, e.g. <c>(A;G)</c>. Hemizygous calls are
        /// written with the single allele repeated, which is how SNPedia records
        /// them on Y and MT pages.
        /// </summary>
        public string ToSnpediaNotation()
        {
            var normalized = Normalized();
            return "(" + normalized._first.ToChar() + ";" + normalized._second.ToChar() + ")";
        }

        /// <summary>Provider notation, e.g. <c>AG</c> or <c>A</c> when hemizygous.</summary>
        public override string ToString()
        {
            return _isDiploid
                ? new string(new[] { _first.ToChar(), _second.ToChar() })
                : _first.ToChar().ToString();
        }

        /// <summary>
        /// Tokens a provider writes when it has no genotype to report. These are
        /// not genotypes and must be excluded from analysis rather than parsed.
        /// <c>--</c> is 23andMe's no-call, <c>0</c> is AncestryDNA's, and
        /// <c>I</c>/<c>D</c> denote insertions and deletions that this tool does
        /// not interpret.
        /// </summary>
        public static bool IsNoCallToken(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return true;
            }

            var token = raw!.Trim().ToUpperInvariant();

            switch (token)
            {
                case "--":
                case "-":
                case "0":
                case "00":
                case "NN":
                case "N":
                case "I":
                case "D":
                case "II":
                case "DD":
                case "ID":
                case "DI":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Parses a provider genotype such as <c>AG</c>, or <c>A</c> for a
        /// hemizygous call. Returns false for no-calls and indel markers; callers
        /// must treat that as "no data", never as a genotype.
        /// </summary>
        public static bool TryParse(string? raw, out Genotype genotype)
        {
            genotype = default;

            if (IsNoCallToken(raw))
            {
                return false;
            }

            var token = raw!.Trim();

            if (token.Length == 1)
            {
                if (!NucleotideExtensions.TryParse(token[0], out var single))
                {
                    return false;
                }

                genotype = new Genotype(single);
                return true;
            }

            if (token.Length == 2)
            {
                if (!NucleotideExtensions.TryParse(token[0], out var first) ||
                    !NucleotideExtensions.TryParse(token[1], out var second))
                {
                    return false;
                }

                genotype = new Genotype(first, second);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Parses a pair of alleles held in separate columns, as AncestryDNA
        /// reports them. Returns false when either allele is a no-call.
        /// </summary>
        public static bool TryParsePair(string? allele1, string? allele2, out Genotype genotype)
        {
            genotype = default;

            if (IsNoCallToken(allele1) || IsNoCallToken(allele2))
            {
                return false;
            }

            var a = allele1!.Trim();
            var b = allele2!.Trim();

            if (a.Length != 1 || b.Length != 1)
            {
                return false;
            }

            if (!NucleotideExtensions.TryParse(a[0], out var first) ||
                !NucleotideExtensions.TryParse(b[0], out var second))
            {
                return false;
            }

            genotype = new Genotype(first, second);
            return true;
        }

        public bool Equals(Genotype other)
        {
            if (_isDiploid != other._isDiploid)
            {
                return false;
            }

            return _isDiploid
                ? _first == other._first && _second == other._second
                : _first == other._first;
        }

        public override bool Equals(object? obj) => obj is Genotype other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = (int)_first * 397;
                hash = (hash ^ (_isDiploid ? (int)_second : -1)) * 397;
                return hash ^ _isDiploid.GetHashCode();
            }
        }

        public static bool operator ==(Genotype left, Genotype right) => left.Equals(right);

        public static bool operator !=(Genotype left, Genotype right) => !left.Equals(right);
    }
}
