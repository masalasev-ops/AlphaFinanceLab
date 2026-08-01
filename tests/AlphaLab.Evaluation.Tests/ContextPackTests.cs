using AlphaLab.Core.Llm;
using AlphaLab.Core.ReadModels;
using AlphaLab.Evaluation.Ai;

namespace AlphaLab.Evaluation.Tests;

/// <summary>
/// The D80/D104 context-pack contract. **`FX-PackNoLeak` is, per §23.8.2, the single highest-value check
/// in the AI-seat design** — leakage into a pack is invisible in every downstream number and would
/// invalidate the twin comparison that prices the seat.
/// </summary>
public class ContextPackTests
{
    private static ContextPackBuilder Builder() => new("cp-1.0");

    private static PackField[] MinimalFields(string asOf) =>
    [
        new(PackWhitelist.AsOf, asOf, asOf),
        new(PackWhitelist.RegimeLabel, "bull/normal_vol", asOf),
    ];

    // ---------- FX-PackWatermark: STABILITY ----------

    [Fact]
    public void FX_PackWatermark_SamePackEveryBuild()
    {
        var a = Builder().Build(AiSeat.Researcher, null, "2026-08-03", "w1", MinimalFields("2026-08-03"));
        var b = Builder().Build(AiSeat.Researcher, null, "2026-08-03", "w1", MinimalFields("2026-08-03"));

        Assert.Equal(a.PackHash, b.PackHash);
        Assert.Equal(a.PackJson, b.PackJson);
    }

    [Fact]
    public void FX_PackWatermark_FieldOrderDoesNotChangeTheBytes()
    {
        // Fields are emitted in WHITELIST order, not insertion order. Insertion order is a property of
        // the builder's control flow, which a refactor can change without changing a single value — and
        // byte identity is a requirement, not a nicety.
        var forward = Builder().Build(AiSeat.Researcher, null, "2026-08-03", "w1", MinimalFields("2026-08-03"));
        var reversed = Builder().Build(
            AiSeat.Researcher, null, "2026-08-03", "w1", [.. MinimalFields("2026-08-03").Reverse()]);

        Assert.Equal(forward.PackHash, reversed.PackHash);
    }

    // ---------- FX-PackNoLeak: ADMISSIBILITY ----------

    [Fact]
    public void FX_PackNoLeak_AFieldObservedAfterTheAsOf_FailsConstruction()
    {
        var ex = Assert.Throws<PackViolationException>(() => Builder().Build(
            AiSeat.Researcher, null, "2026-08-03", "w1",
            [
                new(PackWhitelist.AsOf, "2026-08-03", "2026-08-03"),
                new(PackWhitelist.RegimeLabel, "bull/normal_vol", "2026-08-04"),   // tomorrow's fact
            ]));

        Assert.Contains("observed_at", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FX_PackNoLeak_IsPerField_NotPerPack()
    {
        // §23.8.2's specific warning: a pack assembled at the RIGHT watermark can still hold ONE field
        // resolved through a path that ignored it. Here every other field is admissible and the pack as a
        // whole looks correct — a pack-level check passes on exactly this pack.
        var fields = new PackField[]
        {
            new(PackWhitelist.AsOf, "2026-08-03", "2026-08-01"),
            new(PackWhitelist.RegimeLabel, "bull/normal_vol", "2026-08-02"),
            new(PackWhitelist.TrialsCount, 7, "2026-08-03"),
            new(PackWhitelist.ForkBudgetRemaining, 4, "2026-09-01"),   // the single leaked field
        };

        Assert.Throws<PackViolationException>(() =>
            Builder().Build(AiSeat.Researcher, null, "2026-08-03", "w1", fields));
    }

    [Fact]
    public void FX_PackNoLeak_ANullObservedAtIsTimeless_NotUnknown()
    {
        // Null means "this fact has no observation date" (a config constant, a definition) — NOT "we do
        // not know", which would be an opt-out from the leak check disguised as a missing value.
        var pack = Builder().Build(
            AiSeat.Researcher, null, "2026-08-03", "w1",
            [new(PackWhitelist.AsOf, "2026-08-03", null)]);

        Assert.NotNull(pack.PackHash);
    }

    // ---------- FX-PackNoLeak: CLOSURE ----------

    [Fact]
    public void FX_PackNoLeak_AFieldNotOnTheWhitelist_FAILS_RatherThanBeingDropped()
    {
        // Closure, not filtering. Filtering is the more forgiving design and the wrong one: it makes the
        // pack quietly INCOMPLETE instead of loudly WRONG, and the invariant would decay silently as the
        // pack grew — the exact failure mode §23.8.2 names.
        var ex = Assert.Throws<PackViolationException>(() => Builder().Build(
            AiSeat.Researcher, null, "2026-08-03", "w1",
            [new("some_new_feature", 1.23, "2026-08-03")]));

        Assert.Contains("whitelist", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void D110_R1_TheResearcherCannotBeShownItsOwnScore()
    {
        // R1: the researcher never reads its own score. The whitelist has no proposal-score field, so a
        // score added to the builder fails HERE rather than passing silently — the same closure shape as
        // the leak check, applied to a different rail. This is what FX-ProposalScoreIsMechanical relies on.
        foreach (var name in new[] { "proposal_score", "detectability_margin", "calibration_skill" })
        {
            Assert.Throws<PackViolationException>(() => Builder().Build(
                AiSeat.Researcher, null, "2026-08-03", "w1", [new(name, 0.5, "2026-08-03")]));
        }
    }

    [Fact]
    public void Rule32_APriorAiDecisionCannotBeFedBackIntoAPack()
    {
        // Rule 32's corollary: feeding a prior AI decision into a pack routes AI output straight into the
        // thing that prices AI output.
        foreach (var name in new[] { "ai_decision", "ai_context_pack", "last_decision" })
        {
            Assert.Throws<PackViolationException>(() => Builder().Build(
                AiSeat.Researcher, null, "2026-08-03", "w1", [new(name, "x", "2026-08-03")]));
        }
    }

    [Fact]
    public void ByteIdentityDoesNotImplyLeakFreedom_WhichIsWhyBOTHChecksExist()
    {
        // §23.8.2's distinctness argument, made executable: a pack that deterministically includes a
        // post-as-of fact is byte-identical on every build AND STILL LEAKS. If the two checks were merged,
        // this pack would pass.
        var leaky = new PackField[] { new(PackWhitelist.RegimeLabel, "bull/normal_vol", "2026-08-04") };

        // Stable: the same inputs serialize identically every time...
        var json1 = System.Text.Json.JsonSerializer.Serialize(leaky.Select(f => f.Value));
        var json2 = System.Text.Json.JsonSerializer.Serialize(leaky.Select(f => f.Value));
        Assert.Equal(json1, json2);

        // ...and still inadmissible.
        Assert.Throws<PackViolationException>(() => ContextPackBuilder.Validate("2026-08-03", leaky));
    }

    // ---------- Other construction rails ----------

    [Fact]
    public void UnknownSeat_FailsConstruction()
    {
        Assert.Throws<PackViolationException>(() => Builder().Build(
            "trader", null, "2026-08-03", "w1", MinimalFields("2026-08-03")));
    }

    [Fact]
    public void DuplicateField_FailsConstruction()
    {
        // Two values for one name would make the serialization depend on which copy won — a byte-identity
        // hazard, not merely untidy input.
        Assert.Throws<PackViolationException>(() => Builder().Build(
            AiSeat.Researcher, null, "2026-08-03", "w1",
            [
                new(PackWhitelist.RegimeLabel, "bull/normal_vol", "2026-08-03"),
                new(PackWhitelist.RegimeLabel, "bear/high_vol", "2026-08-03"),
            ]));
    }
}

/// <summary>The evidence-prior seam (§24.6) and its three modes — what D113's control arm is built from.</summary>
public class EvidencePriorSeamTests
{
    private static SignalLibraryReadModel ReadModel(string asOf = "2026-08-03") => new()
    {
        Stamp = ReadModelStamp.NoRunYet,
        AsOf = asOf,
        Signals =
        [
            Row("mom:L252s21", 21, 0.030, 0.010, "stable"),
            Row("bab:L252", 21, -0.005, -0.002, "gone"),
            Row("rev:L21", 63, 0.001, 0.004, "decaying"),
        ],
    };

    private static SignalPanelRow Row(string id, int h, double ic1, double ic5, string flag) =>
        new(id, "fam", h, "v1",
            [
                new SignalWindowGrade(1, ic1, null, null, 0, 0),
                new SignalWindowGrade(5, ic5, null, null, 0, 0),
            ],
            flag, null, null, null, null);

    [Fact]
    public void On_ProducesTheRealDigest()
    {
        var field = new EvidencePriorSeam(EvidencePriorMode.On).BuildDigestField(ReadModel());

        Assert.NotNull(field);
        Assert.Equal(PackWhitelist.SignalDigest, field!.Name);
        var rows = Assert.IsAssignableFrom<IEnumerable<EvidencePriorSeam.DigestRow>>(field.Value).ToList();
        Assert.Equal(3, rows.Count);
        Assert.Contains(rows, r => r.SignalId == "mom:L252s21" && r.Ic1y == 0.030 && r.Flag == "stable");
    }

    [Fact]
    public void Disabled_ProducesNoField()
    {
        Assert.Null(new EvidencePriorSeam(EvidencePriorMode.Disabled).BuildDigestField(ReadModel()));
    }

    [Fact]
    public void Placebo_HoldsShapeAndSizeConstant_AndOnlyChangesTheInformation()
    {
        // The reason placebo is the DEFAULT control rather than disable: it holds everything constant
        // except the information, so a measured arm difference cannot be an artefact of prompt length.
        var real = new EvidencePriorSeam(EvidencePriorMode.On).BuildDigestField(ReadModel())!;
        var placebo = new EvidencePriorSeam(EvidencePriorMode.Placebo).BuildDigestField(ReadModel(), seed: 7)!;

        var realRows = ((IEnumerable<EvidencePriorSeam.DigestRow>)real.Value!).ToList();
        var placeboRows = ((IEnumerable<EvidencePriorSeam.DigestRow>)placebo.Value!).ToList();

        Assert.Equal(realRows.Count, placeboRows.Count);
        Assert.Equal(
            realRows.Select(r => (r.SignalId, r.HorizonDays)),
            placeboRows.Select(r => (r.SignalId, r.HorizonDays)));   // same rows, same order

        // The multiset of grades is preserved — only their attachment to signals is scrambled.
        Assert.Equal(
            realRows.Select(r => r.Flag).Order(),
            placeboRows.Select(r => r.Flag).Order());
    }

    [Fact]
    public void Placebo_IsDeterministic_BecauseAnIrreproducibleControlIsNotAControl()
    {
        // A control arm whose pack changed between two builds of the same day would break
        // FX-PackWatermark for the control.
        var a = new EvidencePriorSeam(EvidencePriorMode.Placebo).BuildDigestField(ReadModel(), seed: 42)!;
        var b = new EvidencePriorSeam(EvidencePriorMode.Placebo).BuildDigestField(ReadModel(), seed: 42)!;

        Assert.Equal(
            ((IEnumerable<EvidencePriorSeam.DigestRow>)a.Value!).Select(r => (r.SignalId, r.Flag)),
            ((IEnumerable<EvidencePriorSeam.DigestRow>)b.Value!).Select(r => (r.SignalId, r.Flag)));
    }

    [Fact]
    public void Digest_IsStampedWithTheReadModelsAsOf_SoTheLeakCheckCanSeeIt()
    {
        // The digest is knowable exactly when the grades it summarises are; stamping it makes the field
        // subject to FX-PackNoLeak like any other rather than exempt from it.
        var field = new EvidencePriorSeam().BuildDigestField(ReadModel("2026-07-31"))!;
        Assert.Equal("2026-07-31", field.ObservedAt);

        // And a digest resolved LATER than the pack's as-of is caught.
        Assert.Throws<PackViolationException>(() => ContextPackBuilder.Validate("2026-07-01", [field]));
    }
}
