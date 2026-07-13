using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlphaLab.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "catchup_log",
                columns: table => new
                {
                    as_of = table.Column<string>(type: "TEXT", nullable: false),
                    recovered_at = table.Column<string>(type: "TEXT", nullable: false),
                    run_id = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_catchup_log", x => x.as_of);
                });

            migrationBuilder.CreateTable(
                name: "config",
                columns: table => new
                {
                    key = table.Column<string>(type: "TEXT", nullable: false),
                    version = table.Column<int>(type: "INTEGER", nullable: false),
                    value_json = table.Column<string>(type: "TEXT", nullable: false),
                    changed_on = table.Column<string>(type: "TEXT", nullable: false),
                    reason = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_config", x => new { x.key, x.version });
                });

            migrationBuilder.CreateTable(
                name: "jobs",
                columns: table => new
                {
                    // AlphaLab hand-edit (rule 14): the `Sqlite:Autoincrement` annotation is removed so
                    // job_id is a plain `INTEGER PRIMARY KEY` per SCHEMA. EF Core 10 adds AUTOINCREMENT to
                    // value-generated integer keys and its model snapshot cannot express "rowid without
                    // AUTOINCREMENT" (SqliteValueGenerationStrategy.None never round-trips), so this is edited
                    // post-scaffold; SQLite rowid auto-assignment is unchanged. Re-apply if InitialCreate is
                    // regenerated — SchemaFidelityTests.Schema_IntegerPrimaryKeys_HaveNoAutoincrement guards it.
                    job_id = table.Column<long>(type: "INTEGER", nullable: false),
                    kind = table.Column<string>(type: "TEXT", nullable: false),
                    status = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "queued"),
                    submitted_at = table.Column<string>(type: "TEXT", nullable: false),
                    started_at = table.Column<string>(type: "TEXT", nullable: true),
                    finished_at = table.Column<string>(type: "TEXT", nullable: true),
                    request_json = table.Column<string>(type: "TEXT", nullable: false),
                    result_ref = table.Column<string>(type: "TEXT", nullable: true),
                    error_json = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_jobs", x => x.job_id);
                    table.CheckConstraint("ck_jobs_kind", "kind IN ('replay','analysis_brief','analysis_skeptic')");
                    table.CheckConstraint("ck_jobs_status", "status IN ('queued','running','done','failed')");
                });

            migrationBuilder.CreateTable(
                name: "runs",
                columns: table => new
                {
                    // AlphaLab hand-edit (rule 14): `Sqlite:Autoincrement` removed so run_id is a plain
                    // `INTEGER PRIMARY KEY` per SCHEMA — see the job_id note above. Re-apply if regenerated.
                    run_id = table.Column<long>(type: "INTEGER", nullable: false),
                    as_of = table.Column<string>(type: "TEXT", nullable: false),
                    run_kind = table.Column<string>(type: "TEXT", nullable: false),
                    watermark = table.Column<string>(type: "TEXT", nullable: false),
                    started_at = table.Column<string>(type: "TEXT", nullable: false),
                    finished_at = table.Column<string>(type: "TEXT", nullable: true),
                    status = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "running"),
                    inputs_hash = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_runs", x => x.run_id);
                    table.CheckConstraint("ck_runs_run_kind", "run_kind IN ('live','catchup','replay')");
                });

            migrationBuilder.CreateTable(
                name: "worker_state",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false),
                    run_in_progress = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    current_run_id = table.Column<long>(type: "INTEGER", nullable: true),
                    heartbeat_at = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_worker_state", x => x.id);
                    table.CheckConstraint("ck_worker_state_id", "id = 1");
                });

            migrationBuilder.InsertData(
                table: "worker_state",
                columns: new[] { "id", "current_run_id", "heartbeat_at" },
                values: new object[] { 1, null, null });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "catchup_log");

            migrationBuilder.DropTable(
                name: "config");

            migrationBuilder.DropTable(
                name: "jobs");

            migrationBuilder.DropTable(
                name: "runs");

            migrationBuilder.DropTable(
                name: "worker_state");
        }
    }
}
