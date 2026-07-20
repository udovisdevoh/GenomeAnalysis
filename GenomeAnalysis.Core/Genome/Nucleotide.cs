using System;

namespace GenomeAnalysis.Core.Genome
{
    /// <summary>
    /// A single DNA base. Only the four canonical bases are modelled; insertion,
    /// deletion and no-call markers are handled by <see cref="Genotype"/> parsing
    /// and never reach this type.
    /// </summary>
    public enum Nucleotide
    {
        A = 0,
        C = 1,
        G = 2,
        T = 3
    }

    public static class NucleotideExtensions
    {
        /// <summary>
        /// The base this one pairs with on the opposite DNA strand: A&lt;-&gt;T, C&lt;-&gt;G.
        /// </summary>
        public static Nucleotide Complement(this Nucleotide nucleotide)
        {
            switch (nucleotide)
            {
                case Nucleotide.A: return Nucleotide.T;
                case Nucleotide.T: return Nucleotide.A;
                case Nucleotide.C: return Nucleotide.G;
                case Nucleotide.G: return Nucleotide.C;
                default:
                    throw new ArgumentOutOfRangeException(nameof(nucleotide), nucleotide, "Unknown nucleotide.");
            }
        }

        public static char ToChar(this Nucleotide nucleotide)
        {
            switch (nucleotide)
            {
                case Nucleotide.A: return 'A';
                case Nucleotide.C: return 'C';
                case Nucleotide.G: return 'G';
                case Nucleotide.T: return 'T';
                default:
                    throw new ArgumentOutOfRangeException(nameof(nucleotide), nucleotide, "Unknown nucleotide.");
            }
        }

        public static bool TryParse(char value, out Nucleotide nucleotide)
        {
            switch (char.ToUpperInvariant(value))
            {
                case 'A': nucleotide = Nucleotide.A; return true;
                case 'C': nucleotide = Nucleotide.C; return true;
                case 'G': nucleotide = Nucleotide.G; return true;
                case 'T': nucleotide = Nucleotide.T; return true;
                default: nucleotide = default; return false;
            }
        }

        /// <summary>
        /// True when the two alleles of a SNP are each other's complement (A/T or C/G).
        /// Such a SNP is <em>palindromic</em>: its alleles look identical whichever
        /// strand they are read from, so strand cannot be inferred from the alleles.
        /// See <see cref="Strands.StrandResolver"/> for why this matters.
        /// </summary>
        public static bool IsPalindromicPair(Nucleotide first, Nucleotide second)
        {
            return first.Complement() == second;
        }
    }
}
