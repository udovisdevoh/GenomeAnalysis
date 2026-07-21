using System;
using System.Collections.Generic;
using System.Linq;
using GenomeAnalysis.Core.Genome;

namespace GenomeAnalysis.Core.Annotations
{
    /// <summary>
    /// Everything a source knows about one variant, independent of any particular
    /// person's genotype.
    /// </summary>
    public sealed class VariantAnnotation
    {
        public VariantAnnotation(
            string rsId,
            Strand orientation,
            Strand stabilizedOrientation,
            IReadOnlyList<GenotypeAnnotation>? genotypes,
            string? summary,
            SourceAttribution attribution,
            string? geneSymbol = null,
            ClinicalAnnotation? clinical = null,
            double? minorAlleleFrequency = null,
            IReadOnlyCollection<Nucleotide>? knownAlleles = null,
            IReadOnlyList<string>? mergedRsIds = null,
            string? mostSevereConsequence = null)
        {
            if (string.IsNullOrWhiteSpace(rsId))
            {
                throw new ArgumentException("rsId is required.", nameof(rsId));
            }

            RsId = rsId.Trim().ToLowerInvariant();
            Orientation = orientation;
            StabilizedOrientation = stabilizedOrientation;
            Genotypes = genotypes ?? new List<GenotypeAnnotation>();
            Summary = summary;
            Attribution = attribution ?? throw new ArgumentNullException(nameof(attribution));
            GeneSymbol = geneSymbol;
            Clinical = clinical;
            MinorAlleleFrequency = minorAlleleFrequency;
            _knownAlleles = knownAlleles;
            MergedRsIds = mergedRsIds ?? new List<string>();
            MostSevereConsequence = mostSevereConsequence;
        }

        private readonly IReadOnlyCollection<Nucleotide>? _knownAlleles;

        public string RsId { get; }

        /// <summary>
        /// Orientation in the current reference build. Present for completeness;
        /// it is <em>not</em> the value to use when matching genotypes.
        /// </summary>
        public Strand Orientation { get; }

        /// <summary>
        /// The orientation kept consistent with the genotype records. This is the
        /// one to pass to <see cref="Strands.StrandResolver"/>; using
        /// <see cref="Orientation"/> instead produces incorrect matches.
        /// </summary>
        public Strand StabilizedOrientation { get; }

        public IReadOnlyList<GenotypeAnnotation> Genotypes { get; }

        public string? Summary { get; }

        public string? GeneSymbol { get; }

        public ClinicalAnnotation? Clinical { get; }

        /// <summary>
        /// Frequency of the minor allele in the general population, where the
        /// source reports it.
        /// </summary>
        /// <remarks>
        /// This is what keeps a report honest. A variant carried by 40% of people
        /// is not a discovery, and presenting it with the same weight as a rare
        /// one misleads the reader far more than omitting it would.
        /// </remarks>
        public double? MinorAlleleFrequency { get; }

        /// <summary>
        /// True when the variant is common enough that presenting it as a personal
        /// finding would overstate it.
        /// </summary>
        public bool IsCommon => MinorAlleleFrequency.HasValue && MinorAlleleFrequency.Value >= 0.05;

        public SourceAttribution Attribution { get; }

        /// <summary>
        /// Old rsIDs that dbSNP has merged into this one. A file from 2013 may
        /// carry an identifier that no longer resolves; without this, real variants
        /// come back "unknown".
        /// </summary>
        public IReadOnlyList<string> MergedRsIds { get; }

        /// <summary>Ensembl's most severe predicted consequence, where known.</summary>
        public string? MostSevereConsequence { get; }

        /// <summary>
        /// The distinct alleles this variant is known to have. Taken from the
        /// reference source when available (Ensembl reports them directly),
        /// otherwise derived from the genotype records.
        /// </summary>
        /// <remarks>
        /// Strand resolution depends on this. Without an allele set, a palindromic
        /// variant cannot be told apart from a resolvable one, so
        /// <see cref="Strands.StrandResolver"/> refuses to map anything at all.
        /// </remarks>
        public IReadOnlyCollection<Nucleotide> KnownAlleles =>
            _knownAlleles != null && _knownAlleles.Count > 0
                ? _knownAlleles
                : Genotypes
                    .SelectMany(g => g.Genotype.Alleles)
                    .Distinct()
                    .ToList();

        /// <summary>
        /// True when this variant's alleles are complementary (A/T or C/G), so a
        /// homozygous call on it can never be strand-resolved.
        /// </summary>
        public bool IsPalindromic => Strands.StrandResolver.IsPalindromicVariant(KnownAlleles);

        /// <summary>
        /// Finds the record for a genotype already mapped into this annotation's
        /// orientation. Callers must resolve strand first; this method does not,
        /// and will simply not match a genotype from the opposite strand.
        /// </summary>
        public GenotypeAnnotation? FindGenotype(Genotype resolved)
        {
            var target = resolved.Normalized();
            return Genotypes.FirstOrDefault(g => g.Genotype.Normalized().Equals(target));
        }
    }
}
