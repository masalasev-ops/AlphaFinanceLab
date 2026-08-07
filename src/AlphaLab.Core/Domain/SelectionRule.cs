using System.Text.Json.Serialization;

namespace AlphaLab.Core.Domain;

/// <summary>Stage-3 selection mode (catalog §3). The rule is itself a dimension candidates
/// differ on, so it lives in StrategyConfig rather than being a system-level knob.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SelectionMode>))]
public enum SelectionMode
{
    /// <summary>Keep the N highest-scored names passing the zero-score invariant.</summary>
    TopN,

    /// <summary>Keep names with score ≥ MinScore, capped at MaxConcurrent. Preferred for
    /// sparse-signal strategies.</summary>
    Threshold,

    /// <summary>Keep EVERY name that passes the zero-score invariant, capped at MaxConcurrent
    /// (catalog §3; §6.6 TimeSeriesMomentum is its only declared consumer). Not a third floor —
    /// a wrapper over the invariant, which is what makes a defensive strategy go partially to cash
    /// in a downturn instead of padding to a fixed breadth.</summary>
    AllPositive,
}

/// <summary>
/// The Stage-3 selection rule (catalog §3). Shared code for every strategy.
///
/// The invariant this type exists to protect: a name with score == 0 (or &lt; MinScore) is NEVER
/// selectable. Sparse days mean shorter wish lists and more cash — no padding, ever. That is
/// enforced in Selection, not here; this type only carries the parameters.
/// </summary>
public sealed record SelectionRule
{
    public required SelectionMode Mode { get; init; }

    /// <summary>Top-N breadth. Momentum's default is 40 (decile-like, so the factor is
    /// measurable over idiosyncratic noise). Ignored when Mode = Threshold.</summary>
    public int N { get; init; } = 40;

    /// <summary>Score floor. Catalog §3's Threshold default is 0.60. A name at or below this is
    /// not selectable under either mode — Guardrails.MinScore (default 0.0) is the system-level
    /// floor beneath it, and the zero-score invariant holds regardless of both.</summary>
    public double MinScore { get; init; } = 0.60;

    /// <summary>Cap on concurrent positions under Threshold mode.</summary>
    public int MaxConcurrent { get; init; } = 60;

    public static SelectionRule TopN(int n) => new() { Mode = SelectionMode.TopN, N = n };

    public static SelectionRule Threshold(double minScore, int maxConcurrent) =>
        new() { Mode = SelectionMode.Threshold, MinScore = minScore, MaxConcurrent = maxConcurrent };

    /// <summary>
    /// Catalog §3's AllPositive: keep every name passing the invariant, capped at
    /// <paramref name="maxConcurrent"/>.
    ///
    /// <see cref="MinScore"/> is set to 0.0 EXPLICITLY and that is the whole point of the factory: the
    /// record's default is 0.60, and <see cref="Funnel.Selection"/> applies the strategy floor in every
    /// mode — so a rule built without this line would silently be Threshold(0.60) wearing AllPositive's
    /// name, keeping only names above 0.60 while claiming to keep every positive one. "Every name
    /// passing the invariant" means the invariant (score &gt; 0), not a floor on top of it.
    /// </summary>
    public static SelectionRule AllPositive(int maxConcurrent) =>
        new() { Mode = SelectionMode.AllPositive, MinScore = 0.0, MaxConcurrent = maxConcurrent };
}
