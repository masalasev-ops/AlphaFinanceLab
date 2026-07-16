using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlphaLab.Data.Migrations
{
    /// <inheritdoc />
    public partial class DataQualityFlags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "data_quality_flags",
                columns: table => new
                {
                    // AlphaLab hand-edit (rule 14): `Sqlite:Autoincrement` removed so flag_id is a plain
                    // `INTEGER PRIMARY KEY` per SCHEMA — see 20260712205739_InitialCreate.cs for the full
                    // rationale (EF 10 adds AUTOINCREMENT to value-generated int keys; the snapshot keeps
                    // ValueGeneratedOnAdd so there is no PendingModelChangesWarning). Re-apply if regenerated;
                    // SchemaFidelityTests.Schema_IntegerPrimaryKeys_HaveNoAutoincrement guards it.
                    flag_id = table.Column<long>(type: "INTEGER", nullable: false),
                    run_id = table.Column<long>(type: "INTEGER", nullable: false),
                    security_id = table.Column<long>(type: "INTEGER", nullable: true),
                    symbol = table.Column<string>(type: "TEXT", nullable: false),
                    date = table.Column<string>(type: "TEXT", nullable: true),
                    issue = table.Column<string>(type: "TEXT", nullable: false),
                    severity = table.Column<string>(type: "TEXT", nullable: false),
                    detail = table.Column<string>(type: "TEXT", nullable: false),
                    observed_at = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_data_quality_flags", x => x.flag_id);
                    table.CheckConstraint("ck_data_quality_flags_issue", "issue IN ('missing_bar','nan_field','non_positive_price','outlier_return','unexplained_adjustment','cross_check_mismatch')");
                    table.CheckConstraint("ck_data_quality_flags_severity", "severity IN ('warn','reject')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_data_quality_flags_run",
                table: "data_quality_flags",
                column: "run_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "data_quality_flags");
        }
    }
}
