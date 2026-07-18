using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlphaLab.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase2Ledger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "accounts",
                columns: table => new
                {
                    // AlphaLab hand-edit (rule 14): `Sqlite:Autoincrement` removed so account_id is a plain
                    // `INTEGER PRIMARY KEY` per SCHEMA — see 20260712205739_InitialCreate.cs for the full
                    // rationale (EF 10 adds AUTOINCREMENT to value-generated int keys; the snapshot keeps
                    // ValueGeneratedOnAdd so there is no PendingModelChangesWarning). Re-apply if regenerated;
                    // SchemaFidelityTests.Schema_IntegerPrimaryKeys_HaveNoAutoincrement guards it.
                    account_id = table.Column<long>(type: "INTEGER", nullable: false),
                    strategy_id = table.Column<string>(type: "TEXT", nullable: false),
                    starting_cash = table.Column<decimal>(type: "TEXT", nullable: false),
                    run_kind = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "live")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_accounts", x => x.account_id);
                });

            migrationBuilder.CreateTable(
                name: "capacity_rejections",
                columns: table => new
                {
                    account_id = table.Column<long>(type: "INTEGER", nullable: false),
                    security_id = table.Column<long>(type: "INTEGER", nullable: false),
                    as_of = table.Column<string>(type: "TEXT", nullable: false),
                    intended_shares = table.Column<double>(type: "REAL", nullable: false),
                    allowed_shares = table.Column<double>(type: "REAL", nullable: false),
                    adv21 = table.Column<double>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_capacity_rejections", x => new { x.account_id, x.security_id, x.as_of });
                });

            migrationBuilder.CreateTable(
                name: "cash_events",
                columns: table => new
                {
                    // AlphaLab hand-edit (rule 14): `Sqlite:Autoincrement` removed so event_id is a plain
                    // `INTEGER PRIMARY KEY` per SCHEMA — see 20260712205739_InitialCreate.cs for the full
                    // rationale. Re-apply if regenerated; SchemaFidelityTests guards it.
                    event_id = table.Column<long>(type: "INTEGER", nullable: false),
                    account_id = table.Column<long>(type: "INTEGER", nullable: false),
                    security_id = table.Column<long>(type: "INTEGER", nullable: true),
                    as_of = table.Column<string>(type: "TEXT", nullable: false),
                    type = table.Column<string>(type: "TEXT", nullable: false),
                    amount = table.Column<decimal>(type: "TEXT", nullable: false),
                    action_id = table.Column<long>(type: "INTEGER", nullable: true),
                    run_kind = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "live")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cash_events", x => x.event_id);
                });

            migrationBuilder.CreateTable(
                name: "decisions",
                columns: table => new
                {
                    // AlphaLab hand-edit (rule 14): `Sqlite:Autoincrement` removed so decision_id is a plain
                    // `INTEGER PRIMARY KEY` per SCHEMA — see 20260712205739_InitialCreate.cs for the full
                    // rationale. Re-apply if regenerated; SchemaFidelityTests guards it.
                    decision_id = table.Column<long>(type: "INTEGER", nullable: false),
                    account_id = table.Column<long>(type: "INTEGER", nullable: false),
                    as_of = table.Column<string>(type: "TEXT", nullable: false),
                    stage_json = table.Column<string>(type: "TEXT", nullable: false),
                    run_kind = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "live")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_decisions", x => x.decision_id);
                });

            migrationBuilder.CreateTable(
                name: "equity_curve",
                columns: table => new
                {
                    account_id = table.Column<long>(type: "INTEGER", nullable: false),
                    as_of = table.Column<string>(type: "TEXT", nullable: false),
                    run_kind = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "live"),
                    equity = table.Column<decimal>(type: "TEXT", nullable: false),
                    cash = table.Column<decimal>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_equity_curve", x => new { x.account_id, x.as_of, x.run_kind });
                });

            migrationBuilder.CreateTable(
                name: "positions",
                columns: table => new
                {
                    account_id = table.Column<long>(type: "INTEGER", nullable: false),
                    security_id = table.Column<long>(type: "INTEGER", nullable: false),
                    shares = table.Column<double>(type: "REAL", nullable: false),
                    cost_basis = table.Column<decimal>(type: "TEXT", nullable: false),
                    opened_on = table.Column<string>(type: "TEXT", nullable: false),
                    frozen = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    frozen_reason = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_positions", x => new { x.account_id, x.security_id });
                });

            migrationBuilder.CreateTable(
                name: "strategies",
                columns: table => new
                {
                    strategy_id = table.Column<string>(type: "TEXT", nullable: false),
                    family = table.Column<string>(type: "TEXT", nullable: false),
                    config_json = table.Column<string>(type: "TEXT", nullable: false),
                    exit_policy_json = table.Column<string>(type: "TEXT", nullable: false),
                    holding_horizon_days = table.Column<int>(type: "INTEGER", nullable: true),
                    created_on = table.Column<string>(type: "TEXT", nullable: false),
                    parent_strategy_id = table.Column<string>(type: "TEXT", nullable: true),
                    status = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "candidate")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_strategies", x => x.strategy_id);
                });

            migrationBuilder.CreateTable(
                name: "trades",
                columns: table => new
                {
                    // AlphaLab hand-edit (rule 14): `Sqlite:Autoincrement` removed so trade_id is a plain
                    // `INTEGER PRIMARY KEY` per SCHEMA — see 20260712205739_InitialCreate.cs for the full
                    // rationale. Re-apply if regenerated; SchemaFidelityTests guards it.
                    trade_id = table.Column<long>(type: "INTEGER", nullable: false),
                    account_id = table.Column<long>(type: "INTEGER", nullable: false),
                    security_id = table.Column<long>(type: "INTEGER", nullable: false),
                    side = table.Column<string>(type: "TEXT", nullable: false),
                    decided_on = table.Column<string>(type: "TEXT", nullable: false),
                    filled_on = table.Column<string>(type: "TEXT", nullable: false),
                    shares = table.Column<double>(type: "REAL", nullable: false),
                    raw_fill_price = table.Column<decimal>(type: "TEXT", nullable: false),
                    commission = table.Column<decimal>(type: "TEXT", nullable: false),
                    spread_cost = table.Column<decimal>(type: "TEXT", nullable: false),
                    impact_cost = table.Column<decimal>(type: "TEXT", nullable: false),
                    cost_model_version = table.Column<string>(type: "TEXT", nullable: false),
                    reason = table.Column<string>(type: "TEXT", nullable: false),
                    action_id = table.Column<long>(type: "INTEGER", nullable: true),
                    run_kind = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "live")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_trades", x => x.trade_id);
                    table.CheckConstraint("ck_trades_side", "side IN ('buy','sell')");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "accounts");

            migrationBuilder.DropTable(
                name: "capacity_rejections");

            migrationBuilder.DropTable(
                name: "cash_events");

            migrationBuilder.DropTable(
                name: "decisions");

            migrationBuilder.DropTable(
                name: "equity_curve");

            migrationBuilder.DropTable(
                name: "positions");

            migrationBuilder.DropTable(
                name: "strategies");

            migrationBuilder.DropTable(
                name: "trades");
        }
    }
}
