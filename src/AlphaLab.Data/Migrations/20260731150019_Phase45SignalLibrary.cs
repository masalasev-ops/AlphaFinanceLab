using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlphaLab.Data.Migrations
{
    /// <summary>
    /// M6 (Phase 4.5, D91/FR-43,44): the Signal Library's two tables — `signals` (the frozen instrument
    /// registry) and `signal_ic` (one row per grade). Hand-checked: no AUTOINCREMENT (rule 14; both PKs
    /// are TEXT/composite, so EF generates none — verified, not assumed). Neither table carries
    /// `run_kind`, by design (SCHEMA): a grade is a property of a signal and a date, not of a strategy
    /// run. No indexes beyond the PKs and no CHECK constraints, matching SCHEMA verbatim; the
    /// `signal_id` REFERENCES relationship is documentary only, so no EF foreign key is declared (house
    /// precedent — the model creates no shadow indexes).
    /// </summary>
    public partial class Phase45SignalLibrary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "signal_ic",
                columns: table => new
                {
                    signal_id = table.Column<string>(type: "TEXT", nullable: false),
                    as_of = table.Column<string>(type: "TEXT", nullable: false),
                    horizon_days = table.Column<int>(type: "INTEGER", nullable: false),
                    rank_ic = table.Column<double>(type: "REAL", nullable: false),
                    n = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_signal_ic", x => new { x.signal_id, x.as_of, x.horizon_days });
                });

            migrationBuilder.CreateTable(
                name: "signals",
                columns: table => new
                {
                    signal_id = table.Column<string>(type: "TEXT", nullable: false),
                    family = table.Column<string>(type: "TEXT", nullable: false),
                    config_json = table.Column<string>(type: "TEXT", nullable: false),
                    code_version = table.Column<string>(type: "TEXT", nullable: false),
                    registered_on = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_signals", x => x.signal_id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "signal_ic");

            migrationBuilder.DropTable(
                name: "signals");
        }
    }
}
