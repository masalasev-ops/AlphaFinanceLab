using AlphaLab.Core.Llm;
using AlphaLab.Core.ReadModels;

namespace AlphaLab.Evaluation.Ai;

/// <summary>
/// How the evidence-prior seam is serving the digest on this run (§24.6; D113).
///
/// **These three modes are not decoration.** §24.6 requires the seam to be "swappable, disableable and
/// placebo-able", and D113 builds its control arm out of exactly that property: the treatment arm runs
/// <see cref="On"/>, the control runs <see cref="Placebo"/> (default) or <see cref="Disabled"/>. Without
/// a real difference between the arms, two identical researchers produce a margin difference of zero by
/// construction and the control controls for nothing.
/// </summary>
public enum EvidencePriorMode
{
    /// <summary>The real digest — the treatment arm.</summary>
    On,

    /// <summary>No digest field at all. Cheapest control, but it changes pack SHAPE and token count as
    /// well as content, so a measured difference could be an artefact of prompt length.</summary>
    Disabled,

    /// <summary>
    /// A digest of identical shape and token count carrying **shuffled** grades — the default control.
    ///
    /// Stronger than <see cref="Disabled"/> for one specific reason: it holds everything constant except
    /// the information. If the treatment arm proposes better claims, a placebo control makes "because the
    /// digest told it something true" the only surviving explanation, where a disabled control leaves
    /// "because the prompt was longer" open.
    /// </summary>
    Placebo,
}

/// <summary>
/// The researcher's evidence-prior seam: one digest line per signal, from the FR-46 read-model into the
/// context pack (§24.6, D91/D82).
///
/// **As-of resolved, always.** The read-model is built with an explicit as-of so both the
/// <c>signal_ic</c> rows and the pinned <c>SignalLibrary.*</c> significance levels resolve through D96's
/// <c>ResolveAsOf</c> (finding 292). A digest computed from a window ending *now* would leak into a pack
/// whose as-of is earlier, and `FX-PackNoLeak` would (correctly) redden.
///
/// **Consumes the read-model's DTO, never `ISignal`.** `DescriptiveOnlyGuardTests` is an assembly-scoped,
/// default-deny closure over Core + Evaluation: any type outside the library's own namespaces that
/// exposes `ISignal`, `SignalGrade` or `SignalContext` in a signature reddens CI. The digest needs the
/// grades, not the scorers.
/// </summary>
public sealed class EvidencePriorSeam(EvidencePriorMode mode = EvidencePriorMode.On)
{
    public EvidencePriorMode Mode { get; } = mode;

    /// <summary>
    /// Build the digest field, or null when the seam is disabled.
    ///
    /// <paramref name="seed"/> makes the placebo DETERMINISTIC — a control arm whose pack changed between
    /// two builds of the same day would break `FX-PackWatermark` for the control, and an irreproducible
    /// control is not a control.
    /// </summary>
    public PackField? BuildDigestField(SignalLibraryReadModel readModel, int seed = 0)
    {
        ArgumentNullException.ThrowIfNull(readModel);

        if (Mode == EvidencePriorMode.Disabled) return null;

        var lines = readModel.Signals
            .Select(DigestLine)
            .OrderBy(l => l.SignalId, StringComparer.Ordinal)
            .ThenBy(l => l.HorizonDays)
            .ToList();

        if (Mode == EvidencePriorMode.Placebo)
        {
            // Shuffle the GRADES across the rows, keeping the row set, the ordering and every string
            // length identical. The pack's shape and token count are therefore unchanged and only the
            // information content differs — which is the whole point of a placebo over a disable.
            var grades = lines.Select(l => (l.Ic1y, l.Ic5y, l.Flag)).ToList();
            var rng = new Random(seed);
            for (var i = grades.Count - 1; i > 0; i--)
            {
                var j = rng.Next(i + 1);
                (grades[i], grades[j]) = (grades[j], grades[i]);
            }
            lines = [.. lines.Select((l, i) => l with { Ic1y = grades[i].Ic1y, Ic5y = grades[i].Ic5y, Flag = grades[i].Flag })];
        }

        // observed_at is the read-model's as-of: the digest is knowable exactly when the grades it
        // summarises are, and stamping it lets FX-PackNoLeak check this field like any other.
        return new PackField(PackWhitelist.SignalDigest, lines, readModel.AsOf);
    }

    /// <summary>One digest line. Deliberately three numbers and a flag — §23.2's economics depend on the
    /// pack being "a few hundred numbers", not a table.</summary>
    public sealed record DigestRow(string SignalId, int HorizonDays, double? Ic1y, double? Ic5y, string Flag);

    private static DigestRow DigestLine(SignalPanelRow row)
    {
        double? Window(int years) => row.Windows
            .FirstOrDefault(w => w.WindowYears == years)?.MeanRankIc;

        return new DigestRow(row.SignalId, row.HorizonDays, Window(1), Window(5), row.Flag);
    }
}
