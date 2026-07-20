using System;
using GenomeAnalysis.Core.Genome;

namespace GenomeAnalysis.Core.Annotations
{
    /// <summary>
    /// SNPedia's editorial view of a genotype: is it generally considered good or
    /// bad to carry. Community-assigned and frequently absent.
    /// </summary>
    public enum Repute
    {
        NotStated = 0,
        Good = 1,
        Bad = 2
    }

    /// <summary>
    /// What a source says about one specific genotype of a variant — the content
    /// of a SNPedia genotype page such as <c>Rs53576(A;A)</c>.
    /// </summary>
    public sealed class GenotypeAnnotation
    {
        public GenotypeAnnotation(
            Genotype genotype,
            string? summary,
            double? magnitude,
            Repute repute,
            SourceAttribution attribution)
        {
            Genotype = genotype;
            Summary = summary;
            Magnitude = magnitude;
            Repute = repute;
            Attribution = attribution ?? throw new ArgumentNullException(nameof(attribution));
        }

        /// <summary>
        /// The genotype this record describes, in the source's own orientation.
        /// Matching an observed genotype against it must go through
        /// <see cref="Strands.StrandResolver"/>.
        /// </summary>
        public Genotype Genotype { get; }

        public string? Summary { get; }

        /// <summary>
        /// SNPedia's <c>Magnitude</c>: a subjective 0-10 interest score set by
        /// editors. It is not a risk estimate, an effect size or a probability,
        /// and must never be presented as one.
        /// </summary>
        public double? Magnitude { get; }

        public Repute Repute { get; }

        public SourceAttribution Attribution { get; }
    }
}
