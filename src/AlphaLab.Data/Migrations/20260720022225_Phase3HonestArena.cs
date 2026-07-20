using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlphaLab.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase3HonestArena : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "allocation_log",
                columns: table => new
                {
                    // AlphaLab hand-edit (rule 14): `Sqlite:Autoincrement` removed so event_id is a plain
                    // `INTEGER PRIMARY KEY` per SCHEMA — see 20260712205739_InitialCreate.cs for the full
                    // rationale. Re-apply if regenerated; SchemaFidelityTests guards it.
                    event_id = table.Column<long>(type: "INTEGER", nullable: false),
                    as_of = table.Column<string>(type: "TEXT", nullable: false),
                    weights_json = table.Column<string>(type: "TEXT", nullable: false),
                    reason = table.Column<string>(type: "TEXT", nullable: false),
                    run_kind = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "live")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_allocation_log", x => x.event_id);
                });

            migrationBuilder.CreateTable(
                name: "control_equity",
                columns: table => new
                {
                    population_id = table.Column<long>(type: "INTEGER", nullable: false),
                    member_index = table.Column<int>(type: "INTEGER", nullable: false),
                    as_of = table.Column<string>(type: "TEXT", nullable: false),
                    run_kind = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "live"),
                    equity = table.Column<decimal>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_control_equity", x => new { x.population_id, x.member_index, x.as_of, x.run_kind });
                });

            migrationBuilder.CreateTable(
                name: "control_populations",
                columns: table => new
                {
                    // AlphaLab hand-edit (rule 14): `Sqlite:Autoincrement` removed so population_id is a plain
                    // `INTEGER PRIMARY KEY` per SCHEMA — see 20260712205739_InitialCreate.cs for the full
                    // rationale. Re-apply if regenerated; SchemaFidelityTests guards it.
                    population_id = table.Column<long>(type: "INTEGER", nullable: false),
                    family = table.Column<string>(type: "TEXT", nullable: false),
                    family_seed = table.Column<int>(type: "INTEGER", nullable: false),
                    m = table.Column<int>(type: "INTEGER", nullable: false),
                    costs_on = table.Column<bool>(type: "INTEGER", nullable: false),
                    matched_params_json = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_control_populations", x => x.population_id);
                });

            migrationBuilder.CreateTable(
                name: "go_live_log",
                columns: table => new
                {
                    // AlphaLab hand-edit (rule 14): `Sqlite:Autoincrement` removed so event_id is a plain
                    // `INTEGER PRIMARY KEY` per SCHEMA — see 20260712205739_InitialCreate.cs for the full
                    // rationale. Re-apply if regenerated; SchemaFidelityTests guards it.
                    event_id = table.Column<long>(type: "INTEGER", nullable: false),
                    as_of = table.Column<string>(type: "TEXT", nullable: false),
                    promoted = table.Column<string>(type: "TEXT", nullable: true),
                    demoted = table.Column<string>(type: "TEXT", nullable: true),
                    verdict = table.Column<string>(type: "TEXT", nullable: false),
                    evidence_json = table.Column<string>(type: "TEXT", nullable: false),
                    run_kind = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "live")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_go_live_log", x => x.event_id);
                });

            migrationBuilder.CreateTable(
                name: "journal_entries",
                columns: table => new
                {
                    // AlphaLab hand-edit (rule 14): `Sqlite:Autoincrement` removed so entry_id is a plain
                    // `INTEGER PRIMARY KEY` per SCHEMA — see 20260712205739_InitialCreate.cs for the full
                    // rationale. Re-apply if regenerated; SchemaFidelityTests guards it.
                    entry_id = table.Column<long>(type: "INTEGER", nullable: false),
                    created_on = table.Column<string>(type: "TEXT", nullable: false),
                    kind = table.Column<string>(type: "TEXT", nullable: false),
                    title = table.Column<string>(type: "TEXT", nullable: false),
                    body_md = table.Column<string>(type: "TEXT", nullable: false),
                    strategy_id = table.Column<string>(type: "TEXT", nullable: true),
                    linked_entry_id = table.Column<long>(type: "INTEGER", nullable: true),
                    metric = table.Column<string>(type: "TEXT", nullable: true),
                    evidence_window_days = table.Column<int>(type: "INTEGER", nullable: true),
                    outcome = table.Column<string>(type: "TEXT", nullable: true),
                    locked = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_journal_entries", x => x.entry_id);
                    table.CheckConstraint("ck_journal_entries_kind", "kind IN ('hypothesis','observation','decision_note','skeptic_review','outcome')");
                    table.CheckConstraint("ck_journal_entries_outcome", "outcome IN ('confirmed','refuted','inconclusive')");
                });

            migrationBuilder.CreateTable(
                name: "overfitting_checks",
                columns: table => new
                {
                    // AlphaLab hand-edit (rule 14): `Sqlite:Autoincrement` removed so check_id is a plain
                    // `INTEGER PRIMARY KEY` per SCHEMA — see 20260712205739_InitialCreate.cs for the full
                    // rationale. Re-apply if regenerated; SchemaFidelityTests guards it.
                    check_id = table.Column<long>(type: "INTEGER", nullable: false),
                    strategy_id = table.Column<string>(type: "TEXT", nullable: false),
                    as_of = table.Column<string>(type: "TEXT", nullable: false),
                    signal = table.Column<string>(type: "TEXT", nullable: false),
                    value = table.Column<double>(type: "REAL", nullable: true),
                    threshold_json = table.Column<string>(type: "TEXT", nullable: false),
                    contribution = table.Column<string>(type: "TEXT", nullable: false),
                    run_kind = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "live")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_overfitting_checks", x => x.check_id);
                });

            migrationBuilder.CreateTable(
                name: "overfitting_status",
                columns: table => new
                {
                    strategy_id = table.Column<string>(type: "TEXT", nullable: false),
                    as_of = table.Column<string>(type: "TEXT", nullable: false),
                    run_kind = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "live"),
                    status = table.Column<string>(type: "TEXT", nullable: false),
                    trigger_json = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_overfitting_status", x => new { x.strategy_id, x.as_of, x.run_kind });
                    table.CheckConstraint("ck_overfitting_status_status", "status IN ('healthy','warning','suspect','retired')");
                });

            migrationBuilder.CreateTable(
                name: "power_reports",
                columns: table => new
                {
                    // AlphaLab hand-edit (rule 14): `Sqlite:Autoincrement` removed so report_id is a plain
                    // `INTEGER PRIMARY KEY` per SCHEMA — see 20260712205739_InitialCreate.cs for the full
                    // rationale. Re-apply if regenerated; SchemaFidelityTests guards it.
                    report_id = table.Column<long>(type: "INTEGER", nullable: false),
                    as_of = table.Column<string>(type: "TEXT", nullable: false),
                    strategy_a = table.Column<string>(type: "TEXT", nullable: false),
                    strategy_b = table.Column<string>(type: "TEXT", nullable: false),
                    t_days = table.Column<int>(type: "INTEGER", nullable: false),
                    sigma_lr = table.Column<double>(type: "REAL", nullable: false),
                    nw_lag = table.Column<int>(type: "INTEGER", nullable: false),
                    mde_ann = table.Column<double>(type: "REAL", nullable: false),
                    observed_gap_ann = table.Column<double>(type: "REAL", nullable: true),
                    verdict = table.Column<string>(type: "TEXT", nullable: true),
                    run_kind = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "live")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_power_reports", x => x.report_id);
                });

            migrationBuilder.CreateTable(
                name: "trials_registry",
                columns: table => new
                {
                    // AlphaLab hand-edit (rule 14): `Sqlite:Autoincrement` removed so trial_id is a plain
                    // `INTEGER PRIMARY KEY` per SCHEMA — see 20260712205739_InitialCreate.cs for the full
                    // rationale. Re-apply if regenerated; SchemaFidelityTests guards it.
                    trial_id = table.Column<long>(type: "INTEGER", nullable: false),
                    strategy_id = table.Column<string>(type: "TEXT", nullable: false),
                    registered_on = table.Column<string>(type: "TEXT", nullable: false),
                    kind = table.Column<string>(type: "TEXT", nullable: false),
                    run_kind = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "live")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_trials_registry", x => x.trial_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_overfitting_checks_path",
                table: "overfitting_checks",
                columns: new[] { "strategy_id", "signal", "as_of" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "allocation_log");

            migrationBuilder.DropTable(
                name: "control_equity");

            migrationBuilder.DropTable(
                name: "control_populations");

            migrationBuilder.DropTable(
                name: "go_live_log");

            migrationBuilder.DropTable(
                name: "journal_entries");

            migrationBuilder.DropTable(
                name: "overfitting_checks");

            migrationBuilder.DropTable(
                name: "overfitting_status");

            migrationBuilder.DropTable(
                name: "power_reports");

            migrationBuilder.DropTable(
                name: "trials_registry");
        }
    }
}
