using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlphaLab.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase5LlmTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "analysis_cache",
                columns: table => new
                {
                    prompt_hash = table.Column<string>(type: "TEXT", nullable: false),
                    model = table.Column<string>(type: "TEXT", nullable: false),
                    as_of = table.Column<string>(type: "TEXT", nullable: false),
                    task = table.Column<string>(type: "TEXT", nullable: false),
                    output_json = table.Column<string>(type: "TEXT", nullable: false),
                    input_tokens = table.Column<int>(type: "INTEGER", nullable: true),
                    output_tokens = table.Column<int>(type: "INTEGER", nullable: true),
                    cost_usd = table.Column<double>(type: "REAL", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_analysis_cache", x => new { x.prompt_hash, x.model, x.as_of });
                    table.CheckConstraint("ck_analysis_cache_task", "task IN ('news_extraction', 'regime_brief', 'research_brief', 'skeptic', 'hypotheses')");
                });

            migrationBuilder.CreateTable(
                name: "llm_budget_log",
                columns: table => new
                {
                    as_of = table.Column<string>(type: "TEXT", nullable: false),
                    calls = table.Column<int>(type: "INTEGER", nullable: false),
                    tokens = table.Column<int>(type: "INTEGER", nullable: false),
                    cost_usd = table.Column<double>(type: "REAL", nullable: false),
                    degraded = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    note = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_llm_budget_log", x => x.as_of);
                });

            migrationBuilder.CreateTable(
                name: "news_items",
                columns: table => new
                {
                    // RULE 14 HAND-EDIT: the generated `.Annotation("Sqlite:Autoincrement", true)` is
                    // DELETED here. news_id is a plain rowid alias, exactly as SCHEMA declares it — never
                    // AUTOINCREMENT. EF Core's convention adds the annotation to value-generated integer
                    // keys and its snapshot cannot express "rowid without AUTOINCREMENT", so the .Designer
                    // and ModelSnapshot files are left untouched (the model keeps ValueGeneratedOnAdd, so
                    // model == snapshot and no PendingModelChangesWarning appears; the rowid still
                    // auto-assigns). Re-apply this edit if the migration is ever regenerated.
                    // Guarded by SchemaFidelityTests.Schema_IntegerPrimaryKeys_HaveNoAutoincrement.
                    news_id = table.Column<long>(type: "INTEGER", nullable: false),
                    as_of = table.Column<string>(type: "TEXT", nullable: false),
                    title_hash = table.Column<string>(type: "TEXT", nullable: false),
                    title = table.Column<string>(type: "TEXT", nullable: true),
                    source = table.Column<string>(type: "TEXT", nullable: true),
                    symbols_json = table.Column<string>(type: "TEXT", nullable: true),
                    truncated_chars = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_news_items", x => x.news_id);
                });

            migrationBuilder.CreateIndex(
                name: "ux_news_items_as_of_title",
                table: "news_items",
                columns: new[] { "as_of", "title_hash" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "analysis_cache");

            migrationBuilder.DropTable(
                name: "llm_budget_log");

            migrationBuilder.DropTable(
                name: "news_items");
        }
    }
}
