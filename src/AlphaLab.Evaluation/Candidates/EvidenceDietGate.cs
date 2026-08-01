using AlphaLab.Core.Config;
using AlphaLab.Data;

namespace AlphaLab.Evaluation.Candidates;

/// <summary>The gate's verdict, and — when refused — everything the 422 must carry.</summary>
/// <param name="Admitted">False ⇒ the endpoint refuses and writes zero rows.</param>
/// <param name="OverdueCount">Outcomes past their declared evidence window, still unclosed.</param>
/// <param name="Bound">The derived bound the count was measured against.</param>
public sealed record EvidenceDietVerdict(bool Admitted, int OverdueCount, int Bound)
{
    /// <summary>The machine-readable refusal code (D60 error envelope).</summary>
    public const string RefusedCode = "evidence_diet_refused";

    /// <summary>
    /// The refusal message. **Written as a statement about the OPERATOR'S queue, not the researcher's
    /// behaviour** — D112 requires that framing because the mechanism is misreadable without it: unstated,
    /// a later reader concludes the seat is being penalised for something it did not do.
    /// </summary>
    public string Message =>
        $"The researcher seat is paused: {OverdueCount} journal outcome(s) are past their declared " +
        $"evidence window and still unclosed, which has reached the bound of {Bound} " +
        "(Research.MaxConcurrentCandidates — the number of claims the lab can honestly carry in flight). " +
        "This is a forcing function on outcome closure, not a fault of the seat: an unclosed outcome " +
        "starves the generator's evidence base, so the loop stops generating rather than compounding on " +
        "ground truth that was never established. Close the overdue outcomes and the seat resumes.";
}

/// <summary>
/// The D112 evidence diet (closes proposal P8).
///
/// The researcher seat refuses to propose once the **count** of overdue journal outcomes reaches
/// <c>Research.MaxConcurrentCandidates</c>.
///
/// **A COUNT with a DERIVED bound, not a grace period in days.** P8 proposed a grace window; the window's
/// SHAPE was right and its CONSTANT would have been wrong — a `Research.OutcomeOverdueGraceDays` key is an
/// undefended number of exactly the class finding 309 flagged in `Research.ForkBudgetPerYear`, and it
/// would have governed whether the researcher can propose at all. `MaxConcurrentCandidates` is derived:
/// CONFIG_REFERENCE ties it to the "1 Live + 2–3 Candidates" roster shape, and §8 says that shape is
/// *"bounded by statistical honesty, not compute"* — so it is the count of claims the lab can honestly
/// hold in flight, which is precisely the quantity a saturated evidence base must be measured against.
///
/// Tolerating one or two late outcomes while preventing the pile-up is the shape P8 asked for; the count
/// form delivers it and the day form does not.
/// </summary>
public sealed class EvidenceDietGate(AlphaLabDbContext db, ResearchOptions research)
{
    /// <summary>
    /// Assess as of <paramref name="asOf"/>.
    ///
    /// "Overdue" = a hypothesis whose declared <c>evidence_window_days</c> has elapsed since it was
    /// created and which has no recorded outcome. Note it counts HYPOTHESES awaiting a verdict, not
    /// outcome rows — an outcome that was never written is exactly the thing being counted, so counting
    /// outcome rows would count the wrong side of the gap.
    /// </summary>
    public EvidenceDietVerdict Assess(string asOf)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(asOf);

        var bound = Math.Max(1, research.MaxConcurrentCandidates);

        var candidates = db.JournalEntries
            .Where(j => j.Kind == "hypothesis"
                        && j.Outcome == null
                        && j.EvidenceWindowDays != null
                        && j.Locked)
            .Select(j => new { j.CreatedOn, j.EvidenceWindowDays })
            .ToList();

        var asOfDate = DateOnly.Parse(asOf, System.Globalization.CultureInfo.InvariantCulture);
        var overdue = candidates.Count(c =>
            DateOnly.TryParse(c.CreatedOn, System.Globalization.CultureInfo.InvariantCulture, out var created)
            && created.AddDays(c.EvidenceWindowDays!.Value) < asOfDate);

        return new EvidenceDietVerdict(overdue < bound, overdue, bound);
    }
}
