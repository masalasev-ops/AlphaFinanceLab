using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlphaLab.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase35PositionSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "position_snapshots",
                columns: table => new
                {
                    account_id = table.Column<long>(type: "INTEGER", nullable: false),
                    as_of = table.Column<string>(type: "TEXT", nullable: false),
                    security_id = table.Column<long>(type: "INTEGER", nullable: false),
                    run_kind = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "live"),
                    shares = table.Column<double>(type: "REAL", nullable: false),
                    cost_basis = table.Column<decimal>(type: "TEXT", nullable: false),
                    opened_on = table.Column<string>(type: "TEXT", nullable: false),
                    frozen = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    frozen_reason = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_position_snapshots", x => new { x.account_id, x.as_of, x.security_id, x.run_kind });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "position_snapshots");
        }
    }
}
