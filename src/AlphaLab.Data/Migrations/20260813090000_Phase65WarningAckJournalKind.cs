using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlphaLab.Data.Migrations
{
    /// <summary>
    /// M12 (Phase 6 checkpoint 6.5, PR 2 item b): <c>journal_entries.kind</c> gains <c>warning_ack</c>, the
    /// D157 record that an operator looked at a monitor Warning before the gate promoted through it.
    ///
    /// Finding 121's rule — an enum CHECK is extended only by migration, never by editing the model and
    /// hoping — and the D94/M9 precedent for HOW.
    ///
    /// **Hand-written raw rebuild (rule 14), for the reason M9 records rather than a copied habit.** SQLite
    /// cannot ALTER a CHECK constraint, so the change is a whole-table rebuild; a scaffolded rebuild
    /// re-adds <c>AUTOINCREMENT</c> to <c>entry_id</c>, the exact annotation the InitialCreate hand-edit
    /// stripped, and <c>SchemaFidelityTests.Schema_IntegerPrimaryKeys_HaveNoAutoincrement</c> catches it.
    /// The DDL below is therefore written by hand so the hand-edit survives.
    ///
    /// **Verified before it was written, not assumed:** the live sp500 arena holds **0** journal_entries
    /// rows, and <c>sqlite_master</c> shows nothing else referencing the table — no foreign key, no index,
    /// no trigger. So this is the twelve-step in its short form and the copy below is the whole of it.
    /// The copy is still column-for-column and <c>FX-JournalAckRebuild-RowsSurvive</c> still exists,
    /// because the migration ships to EVERY arena rather than to the one that happens to be empty today.
    /// </summary>
    public partial class Phase65WarningAckJournalKind : Migration
    {
        private const string WithAck =
            "kind IN ('hypothesis','observation','decision_note','skeptic_review','outcome','warning_ack')";

        private const string WithoutAck =
            "kind IN ('hypothesis','observation','decision_note','skeptic_review','outcome')";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) => Rebuild(migrationBuilder, WithAck);

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) => Rebuild(migrationBuilder, WithoutAck);

        /// <summary>
        /// Rebuild <c>journal_entries</c> with <paramref name="kindCheck"/>, preserving the plain
        /// <c>INTEGER PRIMARY KEY</c> (no AUTOINCREMENT) and every other column verbatim.
        ///
        /// Up and Down share this because they differ only in the CHECK text — two hand-written copies of
        /// the same DDL is exactly how a Down drifts from its Up and a rollback quietly reshapes a table.
        /// </summary>
        private static void Rebuild(MigrationBuilder migrationBuilder, string kindCheck)
        {
            migrationBuilder.Sql($"""
                CREATE TABLE "journal_entries_rebuild" (
                    "entry_id" INTEGER NOT NULL CONSTRAINT "PK_journal_entries" PRIMARY KEY,
                    "created_on" TEXT NOT NULL,
                    "kind" TEXT NOT NULL,
                    "title" TEXT NOT NULL,
                    "body_md" TEXT NOT NULL,
                    "strategy_id" TEXT NULL,
                    "linked_entry_id" INTEGER NULL,
                    "metric" TEXT NULL,
                    "evidence_window_days" INTEGER NULL,
                    "outcome" TEXT NULL,
                    "locked" INTEGER NOT NULL DEFAULT 0,
                    "expected_effect_ann" REAL NULL,
                    "detectability_floor_ann" REAL NULL,
                    "prior_prob" REAL NULL,
                    CONSTRAINT "ck_journal_entries_kind" CHECK ({kindCheck}),
                    CONSTRAINT "ck_journal_entries_outcome" CHECK (outcome IN ('confirmed','refuted','inconclusive'))
                );
                """);

            migrationBuilder.Sql("""
                INSERT INTO "journal_entries_rebuild" ("entry_id", "created_on", "kind", "title", "body_md", "strategy_id", "linked_entry_id", "metric", "evidence_window_days", "outcome", "locked", "expected_effect_ann", "detectability_floor_ann", "prior_prob")
                SELECT "entry_id", "created_on", "kind", "title", "body_md", "strategy_id", "linked_entry_id", "metric", "evidence_window_days", "outcome", "locked", "expected_effect_ann", "detectability_floor_ann", "prior_prob" FROM "journal_entries";
                """);

            migrationBuilder.Sql("""DROP TABLE "journal_entries";""");
            migrationBuilder.Sql("""ALTER TABLE "journal_entries_rebuild" RENAME TO "journal_entries";""");
        }
    }
}
