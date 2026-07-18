using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlphaLab.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase2RegimeLabels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "regime_episodes",
                columns: table => new
                {
                    // AlphaLab hand-edit (rule 14): `Sqlite:Autoincrement` removed so episode_id is a plain
                    // `INTEGER PRIMARY KEY` per SCHEMA — see 20260712205739_InitialCreate.cs for the full
                    // rationale (EF 10 adds AUTOINCREMENT to value-generated int keys; the snapshot keeps
                    // ValueGeneratedOnAdd so there is no PendingModelChangesWarning). Re-apply if regenerated;
                    // SchemaFidelityTests.Schema_IntegerPrimaryKeys_HaveNoAutoincrement guards it.
                    episode_id = table.Column<long>(type: "INTEGER", nullable: false),
                    label = table.Column<string>(type: "TEXT", nullable: false),
                    start_date = table.Column<string>(type: "TEXT", nullable: false),
                    end_date = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_regime_episodes", x => x.episode_id);
                });

            migrationBuilder.CreateTable(
                name: "regime_labels",
                columns: table => new
                {
                    as_of = table.Column<string>(type: "TEXT", nullable: false),
                    trend = table.Column<string>(type: "TEXT", nullable: false),
                    vol = table.Column<string>(type: "TEXT", nullable: false),
                    label = table.Column<string>(type: "TEXT", nullable: false),
                    inputs_hash = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_regime_labels", x => x.as_of);
                    table.CheckConstraint("ck_regime_labels_trend", "trend IN ('bull','bear')");
                    table.CheckConstraint("ck_regime_labels_vol", "vol IN ('normal_vol','high_vol')");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "regime_episodes");

            migrationBuilder.DropTable(
                name: "regime_labels");
        }
    }
}
