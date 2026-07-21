using System;
using System.Collections.Generic;
using System.Linq;
using GenomeAnalysis.Core.Annotations;
using GenomeAnalysis.Core.Genome;
using GenomeAnalysis.Core.Strands;

namespace GenomeAnalysis.Core.Analysis
{
    /// <summary>
    /// Matches the markers read from a file against the annotation source.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Single-marker interpretation only for now. The multi-marker rules —
    /// haplotypes, diplotypes, compound heterozygosity — layer on top of these
    /// findings rather than replacing them.
    /// </para>
    /// <para>
    /// The source is injected, so this runs against a test double with no network
    /// and no database. In the application it is given the local variant database,
    /// which makes analysis entirely offline.
    /// </para>
    /// </remarks>
    public sealed class GenomeAnalyzer
    {
        private readonly IVariantAnnotationSource _annotations;
        private readonly Strand _fileStrand;

        public GenomeAnalyzer(IVariantAnnotationSource annotations, Strand fileStrand = Strand.Plus)
        {
            _annotations = annotations ?? throw new ArgumentNullException(nameof(annotations));
            _fileStrand = fileStrand;
        }

        /// <summary>
        /// Produces one finding per marker that could carry information. No-calls
        /// and provider-internal identifiers are reported as
        /// <see cref="FindingStatus.NotApplicable"/> rather than dropped, because
        /// what a file failed to measure is part of reading the result.
        /// </summary>
        public IReadOnlyList<Finding> Analyze(IEnumerable<MarkerCall> calls)
        {
            var findings = new List<Finding>();

            foreach (var call in calls ?? Enumerable.Empty<MarkerCall>())
            {
                findings.Add(Evaluate(call));
            }

            return findings;
        }

        private Finding Evaluate(MarkerCall call)
        {
            if (call.IsNoCall)
            {
                return new Finding(
                    call,
                    FindingStatus.NotApplicable,
                    "The provider reported no genotype at this position" +
                    (call.RawGenotypeToken == null ? "." : " (" + call.RawGenotypeToken + ")."));
            }

            if (!call.IsRsId)
            {
                return new Finding(
                    call,
                    FindingStatus.NotApplicable,
                    "Provider-internal identifier with no counterpart in public databases.");
            }

            var annotation = _annotations.GetAsync(call.MarkerId).GetAwaiter().GetResult();

            if (annotation == null)
            {
                return new Finding(
                    call,
                    FindingStatus.NotApplicable,
                    "Not covered by the local variant database.");
            }

            var genotype = call.Genotype!.Value;

            // The orientation to reconcile against is the stabilized one where a
            // source publishes it; otherwise the plain orientation. Sources that
            // report neither yield Unknown, and resolution then refuses rather than
            // assuming plus.
            var annotationStrand = annotation.StabilizedOrientation != Strand.Unknown
                ? annotation.StabilizedOrientation
                : annotation.Orientation;

            var match = StrandResolver.Resolve(
                genotype,
                _fileStrand,
                annotationStrand,
                annotation.KnownAlleles);

            if (!match.IsResolved)
            {
                return new Finding(
                    call,
                    FindingStatus.Indeterminate,
                    match.Reason,
                    annotation,
                    null,
                    match);
            }

            var genotypeAnnotation = annotation.FindGenotype(match.ResolvedGenotype!.Value);

            return new Finding(
                call,
                FindingStatus.Determinate,
                match.Reason,
                annotation,
                genotypeAnnotation,
                match);
        }

        /// <summary>
        /// Orders findings for presentation: strongest evidence first, then
        /// indeterminates, then everything that carried no information.
        /// </summary>
        public static IReadOnlyList<Finding> Prioritise(IEnumerable<Finding> findings)
        {
            return (findings ?? Enumerable.Empty<Finding>())
                .OrderBy(f => f.Status)
                .ThenByDescending(f => f.PriorityScore)
                .ThenBy(f => f.Call.MarkerId, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    /// <summary>
    /// Counts describing an analysis run, for the "what was actually examined"
    /// section a report needs.
    /// </summary>
    /// <remarks>
    /// An array interrogates fixed, predefined positions. Without these numbers a
    /// reader cannot tell the difference between "nothing was found" and "little
    /// was looked at", and the first phrasing would be misleading.
    /// </remarks>
    public sealed class AnalysisSummary
    {
        public AnalysisSummary(IReadOnlyList<Finding> findings)
        {
            Total = findings.Count;
            Determinate = findings.Count(f => f.Status == FindingStatus.Determinate);
            Indeterminate = findings.Count(f => f.Status == FindingStatus.Indeterminate);
            NotApplicable = findings.Count(f => f.Status == FindingStatus.NotApplicable);
            AmbiguousFlips = findings.Count(f =>
                f.StrandMatch != null && f.StrandMatch.Outcome == StrandMatchOutcome.AmbiguousFlip);
            Complemented = findings.Count(f => f.StrandMatch != null && f.StrandMatch.WasComplemented);
            WithClinicalRecord = findings.Count(f =>
                f.Status == FindingStatus.Determinate && f.Clinical != null);
            RequiringConfirmation = findings.Count(f => f.RequiresConfirmatoryTesting);
        }

        public int Total { get; }

        public int Determinate { get; }

        public int Indeterminate { get; }

        public int NotApplicable { get; }

        /// <summary>Palindromic variants whose reading could not be decided.</summary>
        public int AmbiguousFlips { get; }

        /// <summary>Genotypes that needed complementing to match the annotation.</summary>
        public int Complemented { get; }

        public int WithClinicalRecord { get; }

        public int RequiringConfirmation { get; }
    }
}
