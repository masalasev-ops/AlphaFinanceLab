using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlphaLab.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase64MembershipProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "observed_at",
                table: "index_membership_log",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source",
                table: "index_membership_log",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "observed_at",
                table: "index_membership_log");

            migrationBuilder.DropColumn(
                name: "source",
                table: "index_membership_log");
        }
    }
}
