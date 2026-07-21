using AlphaLab.Core.Config;

namespace AlphaLab.Core.Tests;

/// <summary>
/// Guards the "defaults MIRROR CONFIG_REFERENCE" contract (rule 7 / the CostsOptions precedent) for the
/// five Phase-3 option sections. CONFIG_REFERENCE_v1.9.md is the single source of truth; if a default
/// here drifts from that file, this test is the tripwire. Section names must match the JSON keys the
/// consuming Program.cs binds (Gate/Populations/Allocator/Verdicts/Kpi).
/// </summary>
public class Phase3OptionsDefaultsTests
{
    [Fact]
    public void Gate_Defaults_MatchConfigReference()
    {
        Assert.Equal("Gate", GateOptions.SectionName);
        var o = new GateOptions();
        Assert.Equal(21, o.EvaluationCadenceDays);
        Assert.Equal(63, o.MinTrackDays);
        Assert.Equal(0.95, o.Confidence);
        Assert.Equal(0.80, o.Power);
        Assert.Equal(21, o.NwLagCapDays);
    }

    [Fact]
    public void Populations_Defaults_MatchConfigReference()
    {
        Assert.Equal("Populations", PopulationsOptions.SectionName);
        var o = new PopulationsOptions();
        Assert.Equal(200, o.Size);
        Assert.Equal(50, o.CostFreeSize);
        Assert.Equal(5, o.AuditFullLedgerSample);
        Assert.Equal(30.0, o.TurnoverMatchTolerancePct);
        Assert.Equal(1001, o.FamilySeeds.Daily);
        Assert.Equal(1002, o.FamilySeeds.Banded);
        Assert.Equal(1003, o.FamilySeeds.Monthly);
        Assert.Equal(1004, o.FamilySeeds.Quarterly);
    }

    [Fact]
    public void Allocator_Defaults_MatchConfigReference()
    {
        Assert.Equal("Allocator", AllocatorOptions.SectionName);
        var o = new AllocatorOptions();
        Assert.Equal(5.0, o.BandPts);
        Assert.Equal(21, o.CadenceDays);
        Assert.Equal(10.0, o.TooEarlyTiltCapPts);
        Assert.Equal(25.0, o.SuspectDecayPctPerEval);
        Assert.Equal(2.0, o.TemperaturePctAlpha);
        Assert.Equal(0.5, o.TauMinPctAlpha);
        Assert.Equal(5.0, o.WeightFloorPct);
        Assert.Equal(60.0, o.WeightCeilingPct);
    }

    [Fact]
    public void Verdicts_Defaults_MatchConfigReference()
    {
        Assert.Equal("Verdicts", VerdictsOptions.SectionName);
        var o = new VerdictsOptions();
        Assert.Equal(252, o.SeparationMinTrackDays);
        Assert.Equal(0.50, o.SeparationBandCentralFrac);
    }

    [Fact]
    public void Kpi_Defaults_MatchConfigReference()
    {
        Assert.Equal("Kpi", KpiOptions.SectionName);
        var o = new KpiOptions();
        Assert.Equal(6, o.CohortBucketMonths);
        Assert.Equal(3, o.CohortMinStrategies);
    }
}
