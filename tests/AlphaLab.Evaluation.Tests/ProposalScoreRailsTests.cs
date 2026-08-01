using System.Reflection;
using AlphaLab.Core.Llm;
using AlphaLab.Evaluation.Ai;
using AlphaLab.Evaluation.Candidates;

namespace AlphaLab.Evaluation.Tests;

/// <summary>
/// `FX-ProposalScoreIsMechanical` — D110's rails R1 and R2, asserted structurally (checkpoint 5.7).
///
/// **The scorer does not exist yet, and these tests are still the right ones to write now.** Both rails
/// are properties of the SHAPE the scorer must have, and both are enforced by things that exist today: the
/// pack whitelist (R1) and the reference graph plus the whitelist's absences (R2). Writing them now is
/// what makes the scorer, when it arrives, land inside rails rather than beside them.
/// </summary>
public class ProposalScoreRailsTests
{
    /// <summary>Names a score could plausibly enter the pack under. None may be admissible.</summary>
    private static readonly string[] ScoreShapedNames =
    [
        "proposal_score", "proposal_quality", "detectability_margin", "score_detect",
        "calibration_skill", "prior_clamp", "researcher_score", "improvement_trend",
    ];

    [Fact]
    public void R1_NoPackFieldCarriesTheResearchersOwnScore()
    {
        // "The researcher NEVER reads its own score." The whitelist is a CLOSURE, so a score added to the
        // builder FAILS rather than being silently dropped — which means this test asserts the absence
        // that does the work, not merely the absence of a current field.
        foreach (var name in ScoreShapedNames)
        {
            Assert.DoesNotContain(name, PackWhitelist.Allowed, StringComparer.Ordinal);
        }

        // The contrast that makes the rail meaningful rather than blanket: the arena's FLOOR is
        // admissible. R1 forbids reading a grade ON THE RESEARCHER; the floor is a measured property of
        // the arena — the bar every candidate faces. Different object, different rail.
        Assert.Contains(PackWhitelist.DetectabilityFloorAnn, PackWhitelist.Allowed, StringComparer.Ordinal);
    }

    [Fact]
    public void R1_AScoreFieldFailsConstruction_ProvenAgainstADeliberateViolation()
    {
        // The closure is PROVEN TO FIRE (finding 310's lesson: a check nobody proved fires is worth
        // little). A pack carrying a score-shaped field must throw, not be quietly filtered.
        var ex = Assert.Throws<PackViolationException>(() => new ContextPackBuilder("cp-1.0").Build(
            AiSeat.Researcher, "cand:a", "2026-08-01", "2026-08-01T22:00:00Z",
            [
                new PackField(PackWhitelist.AsOf, "2026-08-01"),
                new PackField("proposal_score", 0.42, "2026-08-01"),
            ]));

        Assert.Contains("proposal_score", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void R2_TheScoreParametersAreConfigRows_NotAppsettingsKeys()
    {
        // R2's precondition. They are score INPUTS, so a mid-experiment change breaks the
        // proposal-to-proposal comparability the chain depends on — and an appsettings value is not
        // as-of resolvable (the D106 GateOptions limit), so a later recomputation could not reproduce the
        // score a proposal was originally given.
        //
        // Asserted as: the key names are dotted CONFIG-ROW keys, and no bound Options class exposes them.
        Assert.Equal("Kpi.ProposalPriorClamp", ProposalScoreKeys.PriorClamp);
        Assert.Equal("Kpi.ProposalScoreMinClosed", ProposalScoreKeys.ScoreMinClosed);
        Assert.Equal(2, ProposalScoreKeys.All.Count);

        var kpiOptions = typeof(Core.Config.KpiOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        Assert.DoesNotContain(kpiOptions, p => p.Name is "ProposalPriorClamp" or "ProposalScoreMinClosed");
    }

    [Fact]
    public void R2_NothingInTheEvaluationAssemblyReachesAModelProviderToComputeAScore()
    {
        // "Mechanically computed, NEVER LLM-computed" — rule 32 bars AI from JUDGING, not from being
        // MEASURED. Enforced by the reference graph rather than by review: AlphaLab.Evaluation does not
        // reference AlphaLab.Llm at all, so the scorer, wherever it lands in this assembly, structurally
        // cannot call a model.
        //
        // IAnalysisProvider lives in Core (the finding-295 placement), so its mere visibility proves
        // nothing. What proves it is that no Evaluation type takes one.
        var offenders = typeof(ProposalScoreKeys).Assembly.GetTypes()
            .SelectMany(t => t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            .Where(c => c.GetParameters().Any(p =>
                p.ParameterType == typeof(IAnalysisProvider) || p.ParameterType == typeof(IModelTransport)))
            .Select(c => c.DeclaringType!.FullName!)
            .Distinct()
            .ToList();

        Assert.Empty(offenders);
    }
}
