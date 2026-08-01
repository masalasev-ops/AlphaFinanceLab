using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlphaLab.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase5AiSeatTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ai_context_packs",
                columns: table => new
                {
                    pack_id = table.Column<long>(type: "INTEGER", nullable: false)
,
                    seat = table.Column<string>(type: "TEXT", nullable: false),
                    strategy_id = table.Column<string>(type: "TEXT", nullable: true),
                    as_of = table.Column<string>(type: "TEXT", nullable: false),
                    watermark = table.Column<string>(type: "TEXT", nullable: false),
                    recipe_version = table.Column<string>(type: "TEXT", nullable: false),
                    pack_json = table.Column<string>(type: "TEXT", nullable: false),
                    pack_hash = table.Column<string>(type: "TEXT", nullable: false),
                    token_estimate = table.Column<int>(type: "INTEGER", nullable: false),
                    created_at = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_context_packs", x => x.pack_id);
                    table.CheckConstraint("ck_ai_context_packs_seat", "seat IN ('researcher', 'contestant', 'advisor')");
                });

            migrationBuilder.CreateTable(
                name: "ai_decisions",
                columns: table => new
                {
                    decision_id = table.Column<long>(type: "INTEGER", nullable: false)
,
                    strategy_id = table.Column<string>(type: "TEXT", nullable: false),
                    as_of = table.Column<string>(type: "TEXT", nullable: false),
                    pack_hash = table.Column<string>(type: "TEXT", nullable: false),
                    prompt_version = table.Column<string>(type: "TEXT", nullable: false),
                    model_version = table.Column<string>(type: "TEXT", nullable: false),
                    output_json = table.Column<string>(type: "TEXT", nullable: false),
                    applied_json = table.Column<string>(type: "TEXT", nullable: true),
                    sampling_json = table.Column<string>(type: "TEXT", nullable: true),
                    tokens_in = table.Column<int>(type: "INTEGER", nullable: false),
                    tokens_out = table.Column<int>(type: "INTEGER", nullable: false),
                    cost_usd = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_decisions", x => x.decision_id);
                });

            migrationBuilder.CreateIndex(
                name: "ux_ai_context_packs",
                table: "ai_context_packs",
                columns: new[] { "seat", "strategy_id", "as_of", "recipe_version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_ai_decisions",
                table: "ai_decisions",
                columns: new[] { "strategy_id", "as_of", "prompt_version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_context_packs");

            migrationBuilder.DropTable(
                name: "ai_decisions");
        }
    }
}
