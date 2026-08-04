using AlphaLab.Core.Config;
using AlphaLab.Core.Domain;
using AlphaLab.Core.Funnel;
using AlphaLab.Core.Ledger;
using AlphaLab.Core.Signals;
using AlphaLab.Evaluation.Metrics;
using AlphaLab.Evaluation.Power;

namespace AlphaLab.Evaluation.Construction;

/// <summary>
/// The adjusted-close panel the study realises returns against: latest version ≤ watermark, indexed by
/// session so a return is an array subtraction rather than a query.
///
/// WHY A PANEL IS LEGITIMATE HERE, and why it is NOT a second point-in-time view. The panel serves
/// exactly one purpose — realising the return a position ALREADY held earned between two past sessions.
/// That is a backward-looking read by definition; the decision it settles was made at the earlier date
/// by the scoring path, which uses the real <c>BarFeatureView</c> and never touches this. The BandInputs
/// precedent (MASTER §25.2) is the same shape: align once, slice by index, keep the point-in-time
/// decision in the one class that owns it.
///
/// A NULL ENTRY IS AN ANSWER. A name with no bar on a session simply has no price there, and the
/// basket renormalises across the names that do — the "absence is the answer" idiom the scorers already
/// follow, rather than a fabricated zero return that would understate volatility.
/// </summary>
public sealed class AdjClosePanel
{
    private readonly Dictionary<long, double?[]> _byId;

    public AdjClosePanel(IReadOnlyList<DateOnly> sessions, Dictionary<long, double?[]> byId)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(byId);
        Sessions = sessions;
        _byId = byId;
    }

    public IReadOnlyList<DateOnly> Sessions { get; }

    /// <summary>The adjusted close at session index <paramref name="i"/>, or null when unpriced.</summary>
    public double? At(long securityId, int i) =>
        _byId.TryGetValue(securityId, out var row) && i >= 0 && i < row.Length ? row[i] : null;

    /// <summary>
    /// Equal-weighted simple return of <paramref name="basket"/> from session <paramref name="iPrev"/>
    /// to <paramref name="i"/>, over the members priced at BOTH ends. Null when none is — the day then
    /// contributes no observation rather than a zero.
    /// </summary>
    public double? EqualWeightReturn(IReadOnlyList<SecurityId> basket, int iPrev, int i)
    {
        ArgumentNullException.ThrowIfNull(basket);

        var sum = 0.0;
        var n = 0;
        foreach (var id in basket)
        {
            if (At(id.Value, iPrev) is not { } p0 || p0 <= 0) continue;
            if (At(id.Value, i) is not { } p1) continue;
            sum += p1 / p0 - 1.0;
            n++;
        }
        return n > 0 ? sum / n : null;
    }
}

/// <summary>
/// Phase 5.5's measurement (D123): for each registered <see cref="ISignal"/>, build a monthly-rebalanced
/// TOP-tail portfolio and a BOTTOM-tail portfolio over the stored history, and report the tracking error
/// — and therefore the detectability floor — under two constructions:
///
///   • LONG-ONLY   active = r(top tail) − r(equal-weight scored universe)
///   • LONG-SHORT  active = r(top tail) − r(bottom tail),  dollar-neutral, benchmark cash
///
/// THE QUESTION IT ANSWERS is whether this arena can adjudicate a realistic edge at all. The floor is
/// ZSum·TE/√H, so it is TE that decides it, and a dollar-neutral book carries far less market risk than
/// a long-only tilt measured against a broad benchmark. Whether that difference is large enough to
/// matter is a measurement, and this is the instrument.
///
/// DESCRIPTIVE ONLY, and the rail is worth stating precisely because this is the closest the Signal
/// Library has come to informing anything. D91 forbids a signal's output from becoming an input to the
/// allocator, a gate, sizing or eligibility. Informing a BUILD decision — "should the lab implement
/// shorting?" — is none of those four: no strategy is selected, no order is sized, no candidate is
/// admitted or refused, and nothing here is ever read at runtime. The structural guard is that this
/// namespace is not among the consumer directories `ci.ps1` scans, and it must never be added to them.
///
/// THE OUTPUT MAY NEVER SET A PRE-REGISTERED CLAIM. Using a measured effect to choose the
/// `expected_effect_ann` one then pre-registers is precisely what pre-registration exists to prevent
/// (rule 16 / D52). The study answers "which construction?", never "what should I claim?", and the
/// rendered report repeats that where a future reader will meet it.
/// </summary>
public sealed class ConstructionStudyEngine(
    AdjClosePanel panel,
    Func<DateOnly, IFeatureView> featureViewAt,
    Func<DateOnly, IReadOnlyList<SecurityId>> rosterAsOf,
    CostModel costs,
    GateOptions gate,
    ConstructionStudyOptions options)
{
    /// <summary>A tail needs at least this many names to be a portfolio rather than an anecdote; a
    /// rebalance that cannot fill one keeps the previous book and is counted as skipped.</summary>
    public const int MinTailSize = 2;

    /// <summary>
    /// The rebalance sessions: the FIRST session of each calendar month in
    /// <paramref name="sessions"/>. Calendar-anchored rather than every-21st-session on purpose — a
    /// rolling counter drifts against the calendar over 20 years, so two runs over different windows
    /// would rebalance on different days and stop being comparable.
    /// </summary>
    public static HashSet<int> MonthlyRebalanceIndices(IReadOnlyList<DateOnly> sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        var set = new HashSet<int>();
        for (var i = 0; i < sessions.Count; i++)
        {
            if (i == 0 || sessions[i].Month != sessions[i - 1].Month || sessions[i].Year != sessions[i - 1].Year)
            {
                set.Add(i);
            }
        }
        return set;
    }

    /// <summary>Per-signal accumulator for the shared walk. One instance per signal, advanced together.</summary>
    private sealed class SignalState(ISignal signal, int capacity)
    {
        public ISignal Signal { get; } = signal;
        public IReadOnlyList<SecurityId> TopTail { get; set; } = [];
        public IReadOnlyList<SecurityId> BottomTail { get; set; } = [];
        public IReadOnlyList<SecurityId> Universe { get; set; } = [];
        public List<double> ActiveLongOnly { get; } = new(capacity);
        public List<double> ActiveLongShort { get; } = new(capacity);
        public int Rebalances { get; set; }
        public long TailSizeTotal { get; set; }
        public long ScoredTotal { get; set; }
        public int Uncosted;
        public double CostLongOnly { get; set; }
        public double CostLongShort { get; set; }
    }

    /// <summary>
    /// Measure EVERY signal in one pass over the sessions.
    ///
    /// ONE PASS IS NOT A MICRO-OPTIMISATION, it is what makes the study runnable. A per-signal pass
    /// builds a fresh <c>BarFeatureView</c> at every rebalance for every signal — seven times the views,
    /// none of them sharing the window memoization that view exists to provide. Over a twenty-year
    /// window that is ~5 million windowed bar queries instead of ~720 thousand. Sharing one view per
    /// rebalance date across all seven scorers also keeps memory flat: the view is built, used by
    /// everything that needs that date, and dropped before the next.
    /// </summary>
    public IReadOnlyList<SignalMeasurement> MeasureAll(
        IReadOnlyList<ISignal> signals, SignalContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(signals);

        var sessions = panel.Sessions;
        var rebalances = MonthlyRebalanceIndices(sessions);
        var state = signals.Select(s => new SignalState(s, sessions.Count)).ToList();

        for (var i = 1; i < sessions.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var prev = i - 1;

            // The book is chosen at the CLOSE of session `prev` from data ≤ `prev`, and earns the return
            // from `prev` to `i`. The scoring view is bounded at `prev`, so nothing about session `i` can
            // reach the decision — the leakage fixture asserts exactly this.
            if (rebalances.Contains(prev))
            {
                var asOf = sessions[prev];
                var view = featureViewAt(asOf);
                var pool = Eligibility.Resolve(rosterAsOf(asOf), asOf, view).Eligible;

                if (pool.Count > 0)
                {
                    foreach (var st in state)
                    {
                        if (SelectFrom(st.Signal, pool, view, context) is not { } sel) continue;

                        // The LONG leg's cost is charged to both constructions, so it is computed ONCE
                        // and added twice — pricing the same walk twice would also double-count
                        // `Uncosted`, quietly halving the reader's check on the cost figure.
                        var longCost = TurnoverCost(st.TopTail, sel.Top, view, sel.Top.Count, ref st.Uncosted);
                        var shortCost = TurnoverCost(st.BottomTail, sel.Bottom, view, sel.Bottom.Count, ref st.Uncosted);
                        st.CostLongOnly += longCost;
                        st.CostLongShort += longCost + shortCost;

                        st.TopTail = sel.Top;
                        st.BottomTail = sel.Bottom;
                        st.Universe = sel.Universe;
                        st.Rebalances++;
                        st.TailSizeTotal += sel.Top.Count;
                        st.ScoredTotal += sel.Universe.Count;
                    }
                }
            }

            foreach (var st in state)
            {
                if (st.TopTail.Count == 0) continue;

                var rTop = panel.EqualWeightReturn(st.TopTail, prev, i);
                var rBottom = panel.EqualWeightReturn(st.BottomTail, prev, i);
                var rUniverse = panel.EqualWeightReturn(st.Universe, prev, i);

                // Both legs advance together or neither does. Independently-lengthed series would compare
                // two constructions over different days, which is the one comparison this must not make.
                if (rTop is not { } top || rBottom is not { } bottom || rUniverse is not { } univ) continue;

                st.ActiveLongOnly.Add(top - univ);
                st.ActiveLongShort.Add(top - bottom);
            }
        }

        return state.Select(Finish).ToList();
    }

    /// <summary>Measure one signal — the single-signal convenience over <see cref="MeasureAll"/>.</summary>
    public SignalMeasurement Measure(ISignal signal, SignalContext context, CancellationToken ct = default) =>
        MeasureAll([signal], context, ct)[0];

    private SignalMeasurement Finish(SignalState st)
    {
        // NO OBSERVATIONS ⇒ NO COST DRAG. A signal can select books and still accumulate no returns (a
        // window with no priced sessions). Annualising a real summed cost over `max(1, 0)/252` years
        // would divide by 0.004 and print a cost drag in the thousands of percent — a fabricated number
        // beside an honestly empty measurement, which is worse than reporting nothing.
        var years = st.ActiveLongOnly.Count / MetricsConstants.TradingDaysPerYear;
        var (dragLo, dragLs) = st.ActiveLongOnly.Count > 0
            ? (st.CostLongOnly / years, st.CostLongShort / years)
            : (0.0, 0.0);

        return new SignalMeasurement(
            st.Signal.SignalId,
            st.Signal.Family,
            st.Rebalances,
            st.Rebalances > 0 ? (double)st.TailSizeTotal / st.Rebalances : 0.0,
            st.Rebalances > 0 ? (double)st.ScoredTotal / st.Rebalances : 0.0,
            st.Uncosted,
            BuildLeg("long_only", st.ActiveLongOnly, dragLo, borrowApplies: false),
            BuildLeg("long_short", st.ActiveLongShort, dragLs, borrowApplies: true));
    }

    /// <summary>
    /// Turn one active-return series into the leg's measurement.
    ///
    /// The floor is ZSum·σ_LR·252/√(H·252), the SAME expression <c>DetectabilityGate</c> evaluates —
    /// deliberately WITHOUT its Bonferroni trials haircut. The haircut is a property of how many
    /// candidates the arena has registered, and folding it in here would make the two constructions
    /// differ by the trials count as well as by their tracking error. This study isolates construction.
    /// </summary>
    private LegMeasurement BuildLeg(
        string construction, IReadOnlyList<double> active, double costDragAnn, bool borrowApplies)
    {
        var mde = MdeCalculator.Compute(active, options.HorizonSessions, gate);
        var teAnn = mde.SigmaLr * Math.Sqrt(MetricsConstants.TradingDaysPerYear);
        var grossAnn = active.Count > 0 ? active.Average() * MetricsConstants.TradingDaysPerYear : 0.0;

        var horizonSessions = Math.Max(1, gate.DetectabilityHorizonYears) * MetricsConstants.TradingDaysPerYear;
        var floor = MdeCalculator.ZSum(gate.Confidence, gate.Power) * mde.SigmaLr
                    * MetricsConstants.TradingDaysPerYear / Math.Sqrt(horizonSessions);

        // Borrow is a fraction of the SHORT book, which is 1.0× notional in a dollar-neutral long-short
        // and 0 in a long-only one. The long-only leg still renders a single 0 bp row so both legs read
        // alike and no one has to wonder whether borrow was quietly applied to it.
        //
        // Each row carries its own INFORMATION RATIO and YEARS-TO-DETECT, because those — not the floor —
        // are what compare across constructions. A long-short book is ~2× leverage on the same bet, so it
        // scales effect and TE together and leaves detectability untouched; only a change in IR is real.
        var zsum = MdeCalculator.ZSum(gate.Confidence, gate.Power);
        var nets = (borrowApplies ? options.BorrowBpPerYear : [0.0])
            .Select(bp =>
            {
                var net = grossAnn - costDragAnn - bp / 10_000.0;
                var ir = teAnn > 0 ? Math.Abs(net) / teAnn : 0.0;
                // A zero measured effect is never detectable at any horizon. Infinity says so; a large
                // finite number would invite someone to read it as "a long study would do it".
                var years = ir > 0 ? Math.Pow(zsum / ir, 2.0) : double.PositiveInfinity;
                return new NetEffect(bp, net, ir, years);
            })
            .ToList();

        return new LegMeasurement(
            construction, active.Count, mde.SigmaLr, mde.NwLag, teAnn, grossAnn, costDragAnn, nets, floor);
    }

    private sealed record Selection(
        IReadOnlyList<SecurityId> Top, IReadOnlyList<SecurityId> Bottom, IReadOnlyList<SecurityId> Universe);

    /// <summary>
    /// Score the eligible pool and cut the tails. Null when the signal scored too few names to form a
    /// tail — the caller then KEEPS the previous book, which is what a real portfolio would do, rather
    /// than liquidating into a data gap.
    ///
    /// THE BENCHMARK IS THE SCORED SET, not the eligible pool (finding 294's rule). A benchmark
    /// containing names the signal could not score would fold "names with thin history behaved
    /// differently" into what the report calls the signal's active return.
    /// </summary>
    private Selection? SelectFrom(
        ISignal signal, IReadOnlyList<SecurityId> pool, IFeatureView view, SignalContext context)
    {
        var scores = signal.Score(pool, view, context);
        if (scores.Count < MinTailSize * 2) return null;

        // Ties are ordinary — brk:L252 saturates at 1.0 for every name at its high — so the tie-break is
        // security_id, and the ordering is total. Without it two runs of the same day could cut different
        // tails from identical scores and the study would not be reproducible.
        var ranked = scores
            .OrderBy(kv => kv.Value)
            .ThenBy(kv => kv.Key.Value)
            .Select(kv => kv.Key)
            .ToList();

        var k = Math.Max(MinTailSize, (int)(ranked.Count * options.TailFraction));
        if (k * 2 > ranked.Count) return null;   // the tails would overlap — not two portfolios

        return new Selection(
            ranked.Skip(ranked.Count - k).ToList(),   // highest scores
            ranked.Take(k).ToList(),                  // lowest scores
            ranked);
    }

    /// <summary>
    /// The one-way cost of moving from <paramref name="from"/> to <paramref name="to"/>, as a FRACTION
    /// of the book: for each name entering or leaving, its weight times (half-spread + D43 impact).
    ///
    /// Cost is reported as a drag on the MEAN, never folded into the series the tracking error is
    /// measured from. A monthly cost lands as a lump on twelve days a year, so charging it to the series
    /// would add variance that is an artefact of the rebalance calendar rather than a property of the
    /// construction — and tracking error is the number this whole study turns on.
    ///
    /// A name whose ADV window is incomplete cannot be priced by D43. It is charged the WIDEST spread
    /// bucket (conservative) with no impact term, and counted in <paramref name="uncosted"/> so the
    /// reader can judge the cost figure rather than trust it.
    /// </summary>
    private double TurnoverCost(
        IReadOnlyList<SecurityId> from, IReadOnlyList<SecurityId> to, IFeatureView view, int bookSize, ref int uncosted)
    {
        if (bookSize <= 0) return 0.0;

        var fromSet = from.Select(s => s.Value).ToHashSet();
        var toSet = to.Select(s => s.Value).ToHashSet();
        var traded = fromSet.Except(toSet).Concat(toSet.Except(fromSet)).ToList();
        if (traded.Count == 0) return 0.0;

        var weight = 1.0 / bookSize;
        var notionalPerName = (double)options.Notional * weight;

        var total = 0.0;
        foreach (var id in traded)
        {
            var sec = new SecurityId(id);
            var advNotional = view.Adv21Notional(sec);
            var advShares = view.Adv21Shares(sec);
            var price = view.RawClose(sec, view.AsOf);
            var sigma = view.RealizedVolDaily(sec, options.HorizonSessions);

            if (advNotional is not { } notional)
            {
                uncosted++;
                total += weight * costs.HalfSpreadBp(LiquidityBucket.Other) / 10_000.0;
                continue;
            }

            var fraction = costs.HalfSpreadBp(costs.Bucket(notional)) / 10_000.0;
            if (advShares is { } shares && shares > 0 && price is { } p && p > 0 && sigma is { } s)
            {
                fraction += costs.ImpactFraction(notionalPerName / p, shares, s);
            }
            total += weight * fraction;
        }
        return total;
    }
}
