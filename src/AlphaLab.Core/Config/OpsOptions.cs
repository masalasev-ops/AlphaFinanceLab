namespace AlphaLab.Core.Config;

/// <summary>
/// Operations knobs (CONFIG_REFERENCE "Ops") consumed by the D72 launch order — currently the
/// per-launch local backup (RUNBOOK §3). Every default here MIRRORS CONFIG_REFERENCE, the single
/// source of truth; never hard-code a value that belongs there.
///
/// Follows the …Options convention (SectionName + mutable get/set defaults). The OFF-machine copy
/// of the backup stays a manual operator action (RUNBOOK §3) — it becomes mandatory at the first
/// Phase-2 write, but the lab never ships credentials to reach an off-machine target.
/// </summary>
public sealed class OpsOptions
{
    public const string SectionName = "Ops";

    /// <summary>Local backups older than this many days are pruned after each launch's backup lands.
    /// The retention window bounds disk use; the copies are same-drive convenience snapshots, not the
    /// off-machine safeguard.</summary>
    public int BackupRetentionDays { get; set; } = 30;
}
