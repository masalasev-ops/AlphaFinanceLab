using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlphaLab.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase1DataFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "api_usage_log",
                columns: table => new
                {
                    as_of = table.Column<string>(type: "TEXT", nullable: false),
                    source = table.Column<string>(type: "TEXT", nullable: false),
                    calls = table.Column<int>(type: "INTEGER", nullable: false),
                    plan_limit = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_api_usage_log", x => new { x.as_of, x.source });
                });

            migrationBuilder.CreateTable(
                name: "bars",
                columns: table => new
                {
                    security_id = table.Column<long>(type: "INTEGER", nullable: false),
                    date = table.Column<string>(type: "TEXT", nullable: false),
                    version = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 1),
                    observed_at = table.Column<string>(type: "TEXT", nullable: false),
                    open = table.Column<double>(type: "REAL", nullable: true),
                    high = table.Column<double>(type: "REAL", nullable: true),
                    low = table.Column<double>(type: "REAL", nullable: true),
                    close = table.Column<double>(type: "REAL", nullable: true),
                    volume = table.Column<long>(type: "INTEGER", nullable: true),
                    adj_open = table.Column<double>(type: "REAL", nullable: true),
                    adj_high = table.Column<double>(type: "REAL", nullable: true),
                    adj_low = table.Column<double>(type: "REAL", nullable: true),
                    adj_close = table.Column<double>(type: "REAL", nullable: true),
                    source = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "eodhd")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bars", x => new { x.security_id, x.date, x.version });
                });

            migrationBuilder.CreateTable(
                name: "corporate_actions",
                columns: table => new
                {
                    // AlphaLab hand-edit (rule 14): `Sqlite:Autoincrement` removed so action_id is a plain
                    // `INTEGER PRIMARY KEY` per SCHEMA — see 20260712205739_InitialCreate.cs for the full
                    // rationale (EF 10 adds AUTOINCREMENT to value-generated int keys; the snapshot keeps
                    // ValueGeneratedOnAdd so there is no PendingModelChangesWarning). Re-apply if regenerated;
                    // SchemaFidelityTests.Schema_IntegerPrimaryKeys_HaveNoAutoincrement guards it.
                    action_id = table.Column<long>(type: "INTEGER", nullable: false),
                    security_id = table.Column<long>(type: "INTEGER", nullable: false),
                    type = table.Column<string>(type: "TEXT", nullable: false),
                    ex_date = table.Column<string>(type: "TEXT", nullable: true),
                    effective_date = table.Column<string>(type: "TEXT", nullable: false),
                    cash_per_share = table.Column<decimal>(type: "TEXT", nullable: true),
                    ratio = table.Column<double>(type: "REAL", nullable: true),
                    counterparty_security_id = table.Column<long>(type: "INTEGER", nullable: true),
                    new_symbol = table.Column<string>(type: "TEXT", nullable: true),
                    observed_at = table.Column<string>(type: "TEXT", nullable: false),
                    source = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "eodhd"),
                    processed_on = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_corporate_actions", x => x.action_id);
                    table.CheckConstraint("ck_corporate_actions_type", "type IN ('dividend','split','ticker_change','merger_cash','merger_stock','merger_mixed','spinoff','delist')");
                });

            migrationBuilder.CreateTable(
                name: "index_membership",
                columns: table => new
                {
                    security_id = table.Column<long>(type: "INTEGER", nullable: false),
                    added_on = table.Column<string>(type: "TEXT", nullable: false),
                    removed_on = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_index_membership", x => new { x.security_id, x.added_on });
                });

            migrationBuilder.CreateTable(
                name: "index_membership_log",
                columns: table => new
                {
                    // AlphaLab hand-edit (rule 14): `Sqlite:Autoincrement` removed so log_id is a plain
                    // `INTEGER PRIMARY KEY` per SCHEMA — see the action_id note above. Re-apply if regenerated.
                    log_id = table.Column<long>(type: "INTEGER", nullable: false),
                    as_of = table.Column<string>(type: "TEXT", nullable: false),
                    source_count = table.Column<int>(type: "INTEGER", nullable: true),
                    crosscheck_count = table.Column<int>(type: "INTEGER", nullable: true),
                    agreed = table.Column<int>(type: "INTEGER", nullable: false),
                    adds_json = table.Column<string>(type: "TEXT", nullable: true),
                    drops_json = table.Column<string>(type: "TEXT", nullable: true),
                    note = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_index_membership_log", x => x.log_id);
                });

            migrationBuilder.CreateTable(
                name: "sector_changes",
                columns: table => new
                {
                    security_id = table.Column<long>(type: "INTEGER", nullable: false),
                    changed_on = table.Column<string>(type: "TEXT", nullable: false),
                    old_sector = table.Column<string>(type: "TEXT", nullable: true),
                    new_sector = table.Column<string>(type: "TEXT", nullable: true),
                    old_industry = table.Column<string>(type: "TEXT", nullable: true),
                    new_industry = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sector_changes", x => new { x.security_id, x.changed_on });
                });

            migrationBuilder.CreateTable(
                name: "securities",
                columns: table => new
                {
                    // AlphaLab hand-edit (rule 14): `Sqlite:Autoincrement` removed so security_id is a plain
                    // `INTEGER PRIMARY KEY` per SCHEMA — see the action_id note above. Re-apply if regenerated.
                    security_id = table.Column<long>(type: "INTEGER", nullable: false),
                    current_symbol = table.Column<string>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: true),
                    exchange = table.Column<string>(type: "TEXT", nullable: true),
                    sector = table.Column<string>(type: "TEXT", nullable: true),
                    industry = table.Column<string>(type: "TEXT", nullable: true),
                    first_seen = table.Column<string>(type: "TEXT", nullable: false),
                    delisted_on = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_securities", x => x.security_id);
                });

            migrationBuilder.CreateTable(
                name: "ticker_history",
                columns: table => new
                {
                    security_id = table.Column<long>(type: "INTEGER", nullable: false),
                    valid_from = table.Column<string>(type: "TEXT", nullable: false),
                    symbol = table.Column<string>(type: "TEXT", nullable: false),
                    valid_to = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ticker_history", x => new { x.security_id, x.valid_from });
                });

            migrationBuilder.CreateTable(
                name: "trading_calendar",
                columns: table => new
                {
                    date = table.Column<string>(type: "TEXT", nullable: false),
                    session = table.Column<string>(type: "TEXT", nullable: false),
                    close_time_local = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_trading_calendar", x => x.date);
                    table.CheckConstraint("ck_trading_calendar_session", "session IN ('full','half')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_bars_observed",
                table: "bars",
                column: "observed_at");

            migrationBuilder.CreateIndex(
                name: "ux_securities_active_symbol",
                table: "securities",
                columns: new[] { "current_symbol", "exchange" },
                unique: true,
                filter: "delisted_on IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_ticker_hist_symbol",
                table: "ticker_history",
                columns: new[] { "symbol", "valid_from" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "api_usage_log");

            migrationBuilder.DropTable(
                name: "bars");

            migrationBuilder.DropTable(
                name: "corporate_actions");

            migrationBuilder.DropTable(
                name: "index_membership");

            migrationBuilder.DropTable(
                name: "index_membership_log");

            migrationBuilder.DropTable(
                name: "sector_changes");

            migrationBuilder.DropTable(
                name: "securities");

            migrationBuilder.DropTable(
                name: "ticker_history");

            migrationBuilder.DropTable(
                name: "trading_calendar");
        }
    }
}
