using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlphaLab.Data.Migrations
{
    /// <inheritdoc />
    public partial class CorporateActionVersioning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "version",
                table: "corporate_actions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateIndex(
                name: "ix_corporate_actions_observed",
                table: "corporate_actions",
                column: "observed_at");

            migrationBuilder.CreateIndex(
                name: "ux_corporate_actions_identity",
                table: "corporate_actions",
                columns: new[] { "security_id", "type", "effective_date", "version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_corporate_actions_observed",
                table: "corporate_actions");

            migrationBuilder.DropIndex(
                name: "ux_corporate_actions_identity",
                table: "corporate_actions");

            migrationBuilder.DropColumn(
                name: "version",
                table: "corporate_actions");
        }
    }
}
