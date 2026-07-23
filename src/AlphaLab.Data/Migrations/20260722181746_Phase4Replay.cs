using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlphaLab.Data.Migrations
{
    /// <summary>
    /// M5 (Phase 4): (1) regime_labels PK → (as_of, run_kind) + regime_episodes.run_kind — the D93
    /// regime quarantine (P6 resolved: a replay recompute can no longer overwrite a forward label);
    /// (2) journal_entries.expected_effect_ann — the D89/FR-40 pre-declared field; (3) the D89/FR-41
    /// replay_regime_outcomes table; (4) DROP corporate_actions.processed_on — D94 (P5 resolved),
    /// guarded by a fail-loud precondition below. Hand-checked: no AUTOINCREMENT (rule 14; the one
    /// new table has a composite PK).
    /// </summary>
    public partial class Phase4Replay : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // D94 PRECONDITION (hand-edit): processed_on must be provably ALWAYS-NULL before it is
            // dropped. SQLite has no RAISE outside a trigger, so the guard is a CHECK violation: if any
            // non-NULL processed_on exists the INSERT writes 0 into a CHECK(ok = 1) column and the
            // migration fails loudly inside its transaction — nothing is dropped, nothing is lost.
            migrationBuilder.Sql(
                "CREATE TABLE _d94_fail_if_processed_on_was_ever_written (ok INTEGER NOT NULL CHECK (ok = 1));\n" +
                "INSERT INTO _d94_fail_if_processed_on_was_ever_written (ok)\n" +
                "SELECT CASE WHEN EXISTS (SELECT 1 FROM corporate_actions WHERE processed_on IS NOT NULL) THEN 0 ELSE 1 END;\n" +
                "DROP TABLE _d94_fail_if_processed_on_was_ever_written;");

            // D94 drop (hand-edit): a plain DROP COLUMN, NOT the EF DropColumn operation. EF turns
            // DropColumn into a whole-table REBUILD whose regenerated DDL re-adds AUTOINCREMENT to
            // action_id — exactly the annotation the rule-14 hand-edit stripped (the
            // Schema_IntegerPrimaryKeys_HaveNoAutoincrement guard caught it). processed_on is
            // unindexed and unconstrained, so SQLite drops it in place; the hand-edited DDL survives.
            // Placed BEFORE the regime_labels PK operations so no half-registered rebuild is flushed.
            migrationBuilder.Sql("ALTER TABLE corporate_actions DROP COLUMN processed_on;");

            migrationBuilder.DropPrimaryKey(
                name: "PK_regime_labels",
                table: "regime_labels");

            migrationBuilder.AddColumn<string>(
                name: "run_kind",
                table: "regime_labels",
                type: "TEXT",
                nullable: false,
                defaultValue: "live");

            migrationBuilder.AddColumn<string>(
                name: "run_kind",
                table: "regime_episodes",
                type: "TEXT",
                nullable: false,
                defaultValue: "live");

            migrationBuilder.AddColumn<double>(
                name: "expected_effect_ann",
                table: "journal_entries",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddPrimaryKey(
                name: "PK_regime_labels",
                table: "regime_labels",
                columns: new[] { "as_of", "run_kind" });

            migrationBuilder.CreateTable(
                name: "replay_regime_outcomes",
                columns: table => new
                {
                    strategy_id = table.Column<string>(type: "TEXT", nullable: false),
                    regime_episode_id = table.Column<long>(type: "INTEGER", nullable: false),
                    run_kind = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "replay"),
                    edge_ann = table.Column<double>(type: "REAL", nullable: true),
                    median_percentile = table.Column<double>(type: "REAL", nullable: true),
                    n_days = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_replay_regime_outcomes", x => new { x.strategy_id, x.regime_episode_id, x.run_kind });
                });

            migrationBuilder.CreateIndex(
                name: "ix_regime_episodes_kind_start",
                table: "regime_episodes",
                columns: new[] { "run_kind", "start_date" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "replay_regime_outcomes");

            migrationBuilder.DropPrimaryKey(
                name: "PK_regime_labels",
                table: "regime_labels");

            migrationBuilder.DropIndex(
                name: "ix_regime_episodes_kind_start",
                table: "regime_episodes");

            migrationBuilder.DropColumn(
                name: "run_kind",
                table: "regime_labels");

            migrationBuilder.DropColumn(
                name: "run_kind",
                table: "regime_episodes");

            migrationBuilder.DropColumn(
                name: "expected_effect_ann",
                table: "journal_entries");

            migrationBuilder.AddColumn<string>(
                name: "processed_on",
                table: "corporate_actions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddPrimaryKey(
                name: "PK_regime_labels",
                table: "regime_labels",
                column: "as_of");
        }
    }
}
