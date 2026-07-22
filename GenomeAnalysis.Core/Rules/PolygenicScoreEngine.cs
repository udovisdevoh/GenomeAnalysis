using System;
using System.Collections.Generic;
using System.Linq;
using GenomeAnalysis.Core.Analysis;
using GenomeAnalysis.Core.Genome;
using GenomeAnalysis.Core.Strands;

namespace GenomeAnalysis.Core.Rules
{
    /// <summary>
    /// How far a polygenic score could be taken for this file.
    /// </summary>
    public enum PolygenicScoreOutcome
    {
        /// <summary>Enough of the score was covered to place it on a reference distribution.</summary>
        Placed = 0,

        /// <summary>
        /// The score was computed over the covered variants, but no defensible
        /// percentile could be produced — too little coverage, or no reference
        /// distribution available. The raw sum is reported without a population
        /// interpretation.
        /// </summary>
        Partial = 1,

        /// <summary>None of the score's variants were present in the file.</summary>
        NotApplicable = 2
    }

    /// <summary>
    /// The result of evaluating one polygenic score against a file.
    /// </summary>
    public sealed class PolygenicScoreResult
    {
        public PolygenicScoreResult(
            PolygenicScore score,
            PolygenicScoreOutcome outcome,
            double rawScore,
            int variantsCovered,
            int variantsExcludedAmbiguous,
            int variantsExcludedMismatch,
            double coveredWeightFraction,
            double? percentile,
            double? zScore,
            string reason)
        {
            Score = score;
            Outcome = outcome;
            RawScore = rawScore;
            VariantsCovered = variantsCovered;
            VariantsExcludedAmbiguous = variantsExcludedAmbiguous;
            VariantsExcludedMismatch = variantsExcludedMismatch;
            CoveredWeightFraction = coveredWeightFraction;
            Percentile = percentile;
            ZScore = zScore;
            Reason = reason;
        }

        public PolygenicScore Score { get; }

        public PolygenicScoreOutcome Outcome { get; }

        /// <summary>
        /// The weighted sum over the covered variants. A partial sum unless
        /// coverage is near complete — not comparable to a full-score reference.
        /// </summary>
        public double RawScore { get; }

        public int VariantsCovered { get; }

        public int VariantsInScore => Score.Variants.Count;

        /// <summary>Palindromic covered variants dropped because the reading could not be decided.</summary>
        public int VariantsExcludedAmbiguous { get; }

        /// <summary>Covered variants whose genotype did not match the score's alleles.</summary>
        public int VariantsExcludedMismatch { get; }

        /// <summary>
        /// Fraction of the score's total absolute weight that the covered variants
        /// account for. The honest measure of coverage: covering many tiny-weight
        /// variants and missing the few large ones is poor coverage even at a high
        /// variant count.
        /// </summary>
        public double CoveredWeightFraction { get; }

        /// <summary>
        /// Population percentile, only when one could be justified. Null otherwise —
        /// and null must never be shown as "average".
        /// </summary>
        public double? Percentile { get; }

        public double? ZScore { get; }

        public string Reason { get; }
    }

    /// <summary>
    /// Computes published polygenic scores from the variants a file actually
    /// covers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The sum itself is elementary — dosage times weight, added up. Everything
    /// that matters is in the honesty around it: reconciling each effect allele to
    /// the observed strand, excluding palindromic variants that cannot be resolved,
    /// counting coverage by weight rather than by variant count, and refusing to
    /// turn a partial sum into a percentile unless a reference distribution and
    /// adequate coverage both exist.
    /// </para>
    /// <para>
    /// It never multiplies odds ratios, never imputes a missing genotype to zero
    /// dosage, and never composes a score the source did not publish.
    /// </para>
    /// </remarks>
    public sealed class PolygenicScoreEngine
    {
        /// <summary>
        /// Below this fraction of covered weight, a percentile is withheld: a
        /// precise-looking percentile from a small slice of a score misleads.
        /// </summary>
        public const double MinimumCoverageForPercentile = 0.80;

        private readonly IReadOnlyList<PolygenicScore> _scores;
        private readonly Strand _fileStrand;

        public PolygenicScoreEngine(IReadOnlyList<PolygenicScore> scores, Strand fileStrand = Strand.Plus)
        {
            _scores = scores ?? throw new ArgumentNullException(nameof(scores));
            _fileStrand = fileStrand;
        }

        public IReadOnlyList<PolygenicScoreResult> Evaluate(IEnumerable<Finding> findings)
        {
            // The score supplies its own effect and other alleles, so it resolves
            // strand against those and needs no annotation-database entry. It
            // therefore works from the observed call directly — a score variant is
            // scored whether or not the annotation layer knows it. Only a no-call
            // has no genotype to use.
            var genotypes = (findings ?? Enumerable.Empty<Finding>())
                .Where(f => f.Call.Genotype.HasValue)
                .GroupBy(f => f.Call.MarkerId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Call.Genotype!.Value,
                    StringComparer.OrdinalIgnoreCase);

            return _scores.Select(score => Evaluate(score, genotypes)).ToList();
        }

        private PolygenicScoreResult Evaluate(
            PolygenicScore score,
            IReadOnlyDictionary<string, Genotype> genotypes)
        {
            double rawScore = 0;
            double coveredWeight = 0;
            var covered = 0;
            var ambiguous = 0;
            var mismatch = 0;

            // Reference-distribution accumulators, valid only if every covered
            // variant carries an effect-allele frequency. Computed over the covered
            // set so the z-score stays internally consistent under missingness.
            double referenceMean = 0;
            double referenceVariance = 0;
            var haveAllFrequencies = true;

            var totalWeight = score.Variants.Sum(v => Math.Abs(v.Weight));

            foreach (var variant in score.Variants)
            {
                if (!genotypes.TryGetValue(variant.RsId, out var observed))
                {
                    // Missing is missing — not zero dosage. Excluding it keeps the
                    // sum from being biased toward the other allele.
                    haveAllFrequencies = false;
                    continue;
                }

                var dosage = EffectAlleleDosage(variant, observed, out var status);

                if (status == DosageStatus.Ambiguous)
                {
                    ambiguous++;
                    continue;
                }

                if (status == DosageStatus.Mismatch)
                {
                    mismatch++;
                    continue;
                }

                rawScore += dosage * variant.Weight;
                coveredWeight += Math.Abs(variant.Weight);
                covered++;

                if (variant.EffectAlleleFrequency.HasValue)
                {
                    var f = variant.EffectAlleleFrequency.Value;
                    referenceMean += 2 * f * variant.Weight;
                    referenceVariance += 2 * f * (1 - f) * variant.Weight * variant.Weight;
                }
                else
                {
                    haveAllFrequencies = false;
                }
            }

            var coveredFraction = totalWeight > 0 ? coveredWeight / totalWeight : 0;

            if (covered == 0)
            {
                return new PolygenicScoreResult(
                    score, PolygenicScoreOutcome.NotApplicable, 0, 0, ambiguous, mismatch, 0, null, null,
                    "Aucun des " + score.Variants.Count + " variants de ce score n'est présent dans ce fichier.");
            }

            var (percentile, zScore, placed, note) =
                Place(score, rawScore, covered, coveredFraction, referenceMean, referenceVariance, haveAllFrequencies);

            var coverageText =
                covered + " des " + score.Variants.Count + " variants lus (" +
                (coveredFraction * 100).ToString("0.#") + " % du poids total du score)" +
                (ambiguous > 0 ? ", " + ambiguous + " exclus pour flip ambigu" : string.Empty) +
                (mismatch > 0 ? ", " + mismatch + " exclus pour discordance d'allèles" : string.Empty) + ".";

            return new PolygenicScoreResult(
                score,
                placed ? PolygenicScoreOutcome.Placed : PolygenicScoreOutcome.Partial,
                rawScore,
                covered,
                ambiguous,
                mismatch,
                coveredFraction,
                percentile,
                zScore,
                coverageText + " " + note);
        }

        private static (double? Percentile, double? Z, bool Placed, string Note) Place(
            PolygenicScore score,
            double rawScore,
            int covered,
            double coveredFraction,
            double referenceMean,
            double referenceVariance,
            bool haveAllFrequencies)
        {
            if (coveredFraction < MinimumCoverageForPercentile)
            {
                return (null, null, false,
                    "La couverture est trop faible pour situer ce score dans une population : une puce grand " +
                    "public ne teste qu'une fraction des variants du score. Le score brut ci-dessus est partiel " +
                    "et ne peut pas être converti en percentile ni en risque.");
            }

            // A documented reference sample takes precedence over the model.
            if (score.ReferenceMean.HasValue &&
                score.ReferenceStandardDeviation.HasValue &&
                score.ReferenceStandardDeviation.Value > 0)
            {
                var z = (rawScore - score.ReferenceMean.Value) / score.ReferenceStandardDeviation.Value;
                return (NormalCdf(z) * 100, z, true, ReferenceNote(score));
            }

            if (haveAllFrequencies && referenceVariance > 0)
            {
                var z = (rawScore - referenceMean) / Math.Sqrt(referenceVariance);
                return (NormalCdf(z) * 100, z, true,
                    "Le percentile est estimé par un modèle : moyenne et variance du score sous équilibre de " +
                    "Hardy-Weinberg et indépendance des variants (approximativement vraie pour un score élagué), " +
                    "avec les fréquences alléliques du score. " + ReferenceNote(score));
            }

            return (null, null, false,
                "Le score brut est calculé, mais aucune distribution de référence n'est disponible pour le " +
                "situer dans une population : ni percentile ni risque ne peuvent en être déduits. " +
                ReferenceNote(score));
        }

        private static string ReferenceNote(PolygenicScore score) =>
            "Ce score a été développé en population « " + score.Ancestry + " » et se transpose mal aux autres " +
            "ascendances ; s'il ne correspond pas à la vôtre, le résultat est mal calibré, voire dénué de sens. " +
            "Un score polygénique est une moyenne statistique de population, non un diagnostic.";

        private enum DosageStatus
        {
            Ok,
            Ambiguous,
            Mismatch
        }

        /// <summary>
        /// Copies of the effect allele in the observed genotype, after reconciling
        /// strand against the score's two alleles.
        /// </summary>
        private int EffectAlleleDosage(ScoreVariant variant, Genotype observed, out DosageStatus status)
        {
            var alleles = new List<Nucleotide>();

            if (NucleotideExtensions.TryParse(variant.EffectAllele, out var effect))
            {
                alleles.Add(effect);
            }

            if (NucleotideExtensions.TryParse(variant.OtherAllele, out var other))
            {
                alleles.Add(other);
            }

            // Harmonized scoring files report on the build's plus strand, as does a
            // consumer file by convention. Resolution still guards the palindromic
            // case and any genuine allele mismatch.
            var match = StrandResolver.Resolve(observed, _fileStrand, Strand.Plus, alleles);

            if (match.Outcome == StrandMatchOutcome.AmbiguousFlip)
            {
                status = DosageStatus.Ambiguous;
                return 0;
            }

            if (!match.IsResolved || match.ResolvedGenotype == null)
            {
                status = DosageStatus.Mismatch;
                return 0;
            }

            status = DosageStatus.Ok;
            return match.ResolvedGenotype.Value.Alleles.Count(n => n == effect);
        }

        /// <summary>
        /// Standard normal CDF via a numerical error-function approximation
        /// (Abramowitz &amp; Stegun 7.1.26), accurate to about 1e-7 — ample for a
        /// percentile that is already an approximation.
        /// </summary>
        public static double NormalCdf(double z)
        {
            var sign = z < 0 ? -1 : 1;
            var x = Math.Abs(z) / Math.Sqrt(2);

            var t = 1.0 / (1.0 + 0.3275911 * x);
            var y = 1.0 - (((((1.061405429 * t - 1.453152027) * t) + 1.421413741) * t - 0.284496736) * t + 0.254829592)
                * t * Math.Exp(-x * x);

            return 0.5 * (1.0 + sign * y);
        }
    }
}
