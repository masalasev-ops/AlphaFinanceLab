using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlphaLab.Data.Migrations
{
    /// <summary>
    /// M9 (checkpoint 5.6): <c>jobs.kind</c> gains <c>analysis_hypotheses</c> for the FR-23 researcher seat.
    ///
    /// Finding 121's rule — an enum CHECK is extended only by migration, never by editing the model and
    /// hoping — and the D94 precedent for HOW.
    ///
    /// **Hand-edited to a raw rebuild (rule 14).** SQLite cannot ALTER a CHECK constraint, so EF's
    /// <c>DropCheckConstraint</c>/<c>AddCheckConstraint</c> pair is generated as a whole-table rebuild —
    /// and the regenerated DDL re-adds <c>AUTOINCREMENT</c> to <c>job_id</c>, the exact annotation the
    /// InitialCreate hand-edit stripped. That is not hypothetical: the scaffolded version of this migration
    /// was written, and BOTH
    /// <c>SchemaFidelityTests.Schema_IntegerPrimaryKeys_HaveNoAutoincrement</c> and the M6 closure guard
    /// caught it. Same defect, same fix, same reason as
    /// <c>corporate_actions.processed_on</c> in 20260722181746_Phase4Replay: write the DDL by hand so the
    /// hand-edit survives.
    ///
    /// The rebuild is the standard SQLite twelve-step in its short form (no foreign keys reference
    /// <c>jobs</c>, and it carries no indexes or triggers, so the middle steps are empty). Rows are copied
    /// column-for-column — a queued job must survive a schema change it has nothing to do with.
    /// </summary>
    public partial class Phase5HypothesesJobKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) =>
            Rebuild(migrationBuilder, "kind IN ('replay','analysis_brief','analysis_skeptic','analysis_hypotheses')");

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            Rebuild(migrationBuilder, "kind IN ('replay','analysis_brief','analysis_skeptic')");

        /// <summary>
        /// Rebuild <c>jobs</c> with <paramref name="kindCheck"/>, preserving the plain
        /// <c>INTEGER PRIMARY KEY</c> (no AUTOINCREMENT) and every other column verbatim.
        ///
        /// Up and Down share this because they differ only in the CHECK text — two hand-written copies of
        /// the same DDL is exactly how a Down drifts from its Up and a rollback quietly reshapes a table.
        /// </summary>
        private static void Rebuild(MigrationBuilder migrationBuilder, string kindCheck)
        {
            migrationBuilder.Sql($"""
                CREATE TABLE "jobs_rebuild" (
                    "job_id" INTEGER NOT NULL CONSTRAINT "PK_jobs" PRIMARY KEY,
                    "kind" TEXT NOT NULL,
                    "status" TEXT NOT NULL DEFAULT 'queued',
                    "submitted_at" TEXT NOT NULL,
                    "started_at" TEXT NULL,
                    "finished_at" TEXT NULL,
                    "request_json" TEXT NOT NULL,
                    "result_ref" TEXT NULL,
                    "error_json" TEXT NULL,
                    CONSTRAINT "ck_jobs_kind" CHECK ({kindCheck}),
                    CONSTRAINT "ck_jobs_status" CHECK (status IN ('queued','running','done','failed'))
                );
                """);

            migrationBuilder.Sql("""
                INSERT INTO "jobs_rebuild" ("job_id", "kind", "status", "submitted_at", "started_at", "finished_at", "request_json", "result_ref", "error_json")
                SELECT "job_id", "kind", "status", "submitted_at", "started_at", "finished_at", "request_json", "result_ref", "error_json" FROM "jobs";
                """);

            migrationBuilder.Sql("""DROP TABLE "jobs";""");
            migrationBuilder.Sql("""ALTER TABLE "jobs_rebuild" RENAME TO "jobs";""");
        }
    }
}
