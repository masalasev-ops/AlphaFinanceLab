using AlphaLab.Core.Config;

namespace AlphaLab.Evaluation.Allocator;

/// <summary>One strategy's allocator input. Alpha + SE are in %/yr (matching the config's λ/τ units).
/// <see cref="PriorWeight"/> is the last applied weight (fraction 0..1) for the band hysteresis; null on
/// the first allocation.</summary>
public readonly record struct AllocationInput(string StrategyId, double AlphaHatPct, double SePct, bool TooEarly, bool Suspect, double? PriorWeight);

/// <summary>The full reconstructible per-strategy allocation vector (D51/NFR-2) — every intermediate so
/// allocation_log can be replayed. Weights are fractions (0..1).</summary>
public readonly record struct AllocationRow(
    string StrategyId, double AlphaHatPct, double SePct, double ShrinkWeight, double AlphaTildePct,
    double Target, double Applied, double Weight, IReadOnlyList<string> ClampsBound);

/// <summary>The allocation for one evaluation.</summary>
public readonly record struct AllocationOutcome(IReadOnlyList<AllocationRow> Rows, string Reason);

/// <summary>
/// The D51 ensemble allocator (MASTER §20.2 / DESIGN_IMPROVEMENTS §3.5): shrinkage → softmax → ordered
/// clamps → renormalize. Pure and deterministic. Baselines and control populations never reach here — the
/// caller passes only the allocable (candidate/live) set.
///
///  shrinkage:  w_i = τ²/(τ²+se_i²),  α̃_i = w_i·α̂_i + (1−w_i)·ᾱ,  τ² = max(Var(α̂), τ_min²)
///  softmax:    t_i = softmax(α̃_i / λ)
///  clamps (in order): floor/ceiling → TooEarly cap → Suspect decay → band hysteresis → renormalize
///
/// finding 116: floors apply PRE-renormalization and scale down proportionally when Σfloors > 100% (so a
/// roster larger than ⌊100/WeightFloorPct⌋ = 20 degrades gracefully instead of over-allocating).
///
/// D150 (finding 416): the renormalization is FLOOR-AWARE — the rows the floor clamp produced keep the floor
/// afterwards. There is deliberately NO symmetric ceiling treatment: §3.5 orders clamps at step 3 and
/// renormalization at step 5, so a post-renormalization weight MAY exceed the ceiling, and
/// `FR27_D150_TheCeilingIsDeliberatelyNotSymmetric` pins that so it is not "completed" by a later reader.
/// </summary>
public static class EnsembleAllocator
{
    public static AllocationOutcome Allocate(IReadOnlyList<AllocationInput> inputs, AllocatorOptions opts)
    {
        if (inputs.Count == 0) return new AllocationOutcome([], "empty_roster");

        var n = inputs.Count;
        var floor = opts.WeightFloorPct / 100.0;
        var effectiveFloor = Math.Min(floor, 1.0 / n);            // finding 116: scale floors below 1/N when the roster > cap
        var ceiling = opts.WeightCeilingPct / 100.0;
        var tooEarlyMoveCap = opts.TooEarlyTiltCapPts / 100.0;   // a MOVE cap: |t_i − prior_i| ≤ this
        var suspectMult = 1.0 - opts.SuspectDecayPctPerEval / 100.0;
        var band = opts.BandPts / 100.0;

        // ---- shrinkage ----
        var alphaBar = inputs.Average(x => x.AlphaHatPct);
        var variance = n > 1 ? inputs.Sum(x => (x.AlphaHatPct - alphaBar) * (x.AlphaHatPct - alphaBar)) / (n - 1) : 0.0;
        var tau2 = Math.Max(variance, opts.TauMinPctAlpha * opts.TauMinPctAlpha);

        var shrink = new double[n];
        var alphaTilde = new double[n];
        for (var i = 0; i < n; i++)
        {
            var se2 = inputs[i].SePct * inputs[i].SePct;
            shrink[i] = tau2 / (tau2 + se2);                      // → 1 with a tight SE, → 0 with a loose one
            alphaTilde[i] = shrink[i] * inputs[i].AlphaHatPct + (1.0 - shrink[i]) * alphaBar;
        }

        // ---- softmax (temperature λ, in %/yr α̃) ----
        var maxTilde = alphaTilde.Max();                          // shift for numerical stability
        var exps = new double[n];
        var expSum = 0.0;
        for (var i = 0; i < n; i++) { exps[i] = Math.Exp((alphaTilde[i] - maxTilde) / opts.TemperaturePctAlpha); expSum += exps[i]; }
        var target = new double[n];
        for (var i = 0; i < n; i++) target[i] = exps[i] / expSum;

        // ---- ordered clamps ----
        var applied = new double[n];
        var clamps = new List<string>[n];
        for (var i = 0; i < n; i++)
        {
            clamps[i] = [];
            var w = target[i];

            // 1. floor / ceiling (pre-renormalization).
            if (w < effectiveFloor) { w = effectiveFloor; clamps[i].Add("floor"); }
            else if (w > ceiling) { w = ceiling; clamps[i].Add("ceiling"); }

            // 2. TooEarly tilt cap — bound the MOVE from the current (prior) weight, |t_i − prior| ≤ cap
            // (MASTER §20.2 cl.3), in BOTH directions. On the first allocation the prior is the floor a new
            // strategy enters at, so the bound degenerates to [0, floor + cap] (the old absolute behaviour).
            if (inputs[i].TooEarly)
            {
                var basis = inputs[i].PriorWeight ?? effectiveFloor;
                var lo = Math.Max(0.0, basis - tooEarlyMoveCap);
                var hi = basis + tooEarlyMoveCap;
                if (w < lo) { w = lo; clamps[i].Add("too_early_cap"); }
                else if (w > hi) { w = hi; clamps[i].Add("too_early_cap"); }
            }

            // 3. Suspect decay — a hard decay of the PRIOR weight ("decay only, never a new tilt", so a
            // Suspect strategy can never GAIN weight). On the first allocation there is no prior to gain
            // against, so the target is decayed instead.
            if (inputs[i].Suspect)
            {
                w = (inputs[i].PriorWeight ?? w) * suspectMult;
                clamps[i].Add("suspect_decay");
            }

            // 4. band hysteresis — a sub-band move HOLDS the prior (no churn on softmax noise); a supra-band
            // move steps only to the band EDGE (prior ± band), never the full target (continuous, banded,
            // slow). A Suspect decay is EXEMPT from the hold: it is a deliberate de-risk, not noise, so it
            // always applies — the band only rate-limits a large decay to the edge, never reverts a small one.
            if (inputs[i].PriorWeight is { } prior)
            {
                var d = w - prior;
                if (inputs[i].Suspect)
                {
                    if (Math.Abs(d) > band) { w = prior + Math.Sign(d) * band; clamps[i].Add("band"); }
                }
                else if (Math.Abs(d) < band) { w = prior; clamps[i].Add("band"); }
                else { w = prior + Math.Sign(d) * band; clamps[i].Add("band"); }
            }

            applied[i] = w;
        }

        // ---- renormalize, FLOOR-AWARE (D150) ----
        // MASTER §20.2 clause 3 ends "renormalization never pushes a floored weight back below its (scaled)
        // floor". A plain applied[i]/Σapplied breaks that whenever Σapplied > 1 — which is the ordinary case,
        // because the floor ADDS mass and the band HOLDS priors. So the rows the floor clamp produced are
        // PINNED at effectiveFloor and the remainder is spread pro rata over the rest. One pass, no iteration.
        //
        // THE PIN IS ON THE VALUE, NOT ON THE TOKEN, and that distinction is the whole safety of this step:
        // a row whose applied was subsequently moved off the floor by the suspect decay still CARRIES the
        // "floor" token, and lifting it back would reverse a deliberate de-risk — DESIGN_IMPROVEMENTS §3.5
        // step 3 clause 3, "decay only, never a new tilt". `applied[i] == effectiveFloor` is exact equality on
        // purpose: it is the very double the clamp assigned at step 1, so the bits match iff nothing later
        // overwrote it. Feasibility is guaranteed upstream — :39 caps effectiveFloor at 1/n, so k·effectiveFloor
        // ≤ 1 for any k ≤ n and the remainder can never be negative.
        var appliedSum = applied.Sum();
        var weight = new double[n];

        var pinned = new bool[n];
        var pinnedCount = 0;
        for (var i = 0; i < n; i++)
            if (applied[i] == effectiveFloor && clamps[i].Contains("floor")) { pinned[i] = true; pinnedCount++; }

        var freeSum = 0.0;
        for (var i = 0; i < n; i++) if (!pinned[i]) freeSum += applied[i];

        // Fall back to the plain rescale when the pin cannot express anything: nothing floored (invariant is
        // vacuous), every row floored (Σtarget = 1 makes that unreachable, but 1/n each satisfies the floor
        // anyway), or no free mass to spread the remainder over.
        if (pinnedCount == 0 || pinnedCount == n || freeSum <= 0)
        {
            for (var i = 0; i < n; i++) weight[i] = appliedSum > 0 ? applied[i] / appliedSum : 1.0 / n;
        }
        else
        {
            var remainder = 1.0 - pinnedCount * effectiveFloor;
            for (var i = 0; i < n; i++)
                weight[i] = pinned[i] ? effectiveFloor : remainder * applied[i] / freeSum;
        }

        var rows = new AllocationRow[n];
        for (var i = 0; i < n; i++)
            rows[i] = new AllocationRow(inputs[i].StrategyId, inputs[i].AlphaHatPct, inputs[i].SePct,
                shrink[i], alphaTilde[i], target[i], applied[i], weight[i], clamps[i]);

        return new AllocationOutcome(rows, "ok");
    }
}
