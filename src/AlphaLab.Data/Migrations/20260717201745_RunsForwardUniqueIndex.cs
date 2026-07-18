using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlphaLab.Data.Migrations
{
    /// <summary>
    /// M3 (checkpoint 2.10) — the forward-run uniqueness index (v1.9.7 finding 109; SCHEMA:341-348).
    /// Index-only, modelled on BarsDateIndex: a PARTIAL unique index so at most one status='ok' row
    /// exists per as_of among FORWARD kinds ('live','catchup'), while failed retries and replay runs
    /// stay exempt. Created here because SCHEMA:344 says it lands "when Stage-2 first writes runs" —
    /// which is this checkpoint. It is what makes catch-up idempotency and catchup_log(as_of PK)
    /// mutually consistent.
    /// </summary>
    public partial class RunsForwardUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ux_runs_ok_forward",
                table: "runs",
                column: "as_of",
                unique: true,
                filter: "status = 'ok' AND run_kind IN ('live','catchup')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_runs_ok_forward",
                table: "runs");
        }
    }
}
