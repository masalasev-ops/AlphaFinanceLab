namespace AlphaLab.Data;

/// <summary>
/// Trading-calendar configuration (CONFIG_REFERENCE "Calendar", D54). At launch the exchange is NYSE
/// and the Scheduled-mode orchestrator triggers at session close (ET) + <see cref="RunAfterCloseOffsetMinutes"/>
/// (the trigger itself is Phase 2). Follows the …Options convention (SectionName + mutable get/set
/// defaults matching CONFIG), like ArenaOptions / UniverseOptions.
/// </summary>
public sealed class CalendarOptions
{
    public const string SectionName = "Calendar";

    public string Exchange { get; set; } = "NYSE";

    /// <summary>Trigger = session close (ET) + this offset, converted to machine-local at runtime so DST
    /// never shifts the run relative to the market (MASTER §20.5). Used only in Scheduled mode (Phase 2).</summary>
    public int RunAfterCloseOffsetMinutes { get; set; } = 150;
}
