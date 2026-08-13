using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlphaLab.Data.Migrations
{
    /// <summary>
    /// M13 (Phase 6 checkpoint 6.6, D41/FR-5): the Ken French factor tables — `factor_returns` (the daily
    /// series) and `factor_refresh_log` (one row per refresh that wrote).
    ///
    /// **SCHEMA_v1.9 §157-164 VERBATIM.** Three columns and a composite PK on the first, four columns and
    /// a TEXT PK on the second. Nothing is added: no `observed_at`, no `version`, no `source`, no
    /// `arena_id`, and no CHECK on `factor` despite SCHEMA's comment enumerating seven tokens — SCHEMA
    /// declares none, and `docs/phase6/README.md` is explicit that changing a specified shape is a
    /// DECISION rather than an implementation detail. The missing availability column is real and is filed
    /// as **finding 443** with checkpoint 6.13 as its trigger, not patched in here.
    ///
    /// **Hand-checked for rule 14: NO AUTOINCREMENT.** Both PKs are TEXT or composite-TEXT, so EF
    /// generates none — verified against the emitted DDL rather than assumed, the same check M6 recorded
    /// for `signals`/`signal_ic`.
    ///
    /// **ADDITIVE ONLY — two CREATE TABLEs, no rebuild.** Neither table exists in any arena, so there is
    /// nothing to copy, no CHECK to widen and no row to migrate; this is the ALTER-shaped case, not the
    /// twelve-step one M12 needed. `Down` drops both, which is lossless in the only direction it can run:
    /// the series is re-fetchable from the source by construction, which is exactly what makes dropping
    /// it safe here and would NOT be true of a table holding measured results.
    /// </summary>
    public partial class Phase66FactorTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "factor_refresh_log",
                columns: table => new
                {
                    refreshed_at = table.Column<string>(type: "TEXT", nullable: false),
                    files_json = table.Column<string>(type: "TEXT", nullable: true),
                    checksum = table.Column<string>(type: "TEXT", nullable: true),
                    rows_added = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_factor_refresh_log", x => x.refreshed_at);
                });

            migrationBuilder.CreateTable(
                name: "factor_returns",
                columns: table => new
                {
                    date = table.Column<string>(type: "TEXT", nullable: false),
                    factor = table.Column<string>(type: "TEXT", nullable: false),
                    value = table.Column<double>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_factor_returns", x => new { x.date, x.factor });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "factor_refresh_log");

            migrationBuilder.DropTable(
                name: "factor_returns");
        }
    }
}
