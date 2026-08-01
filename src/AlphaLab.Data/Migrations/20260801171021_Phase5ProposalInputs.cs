using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlphaLab.Data.Migrations
{
    /// <summary>
    /// M10 (checkpoint 5.7): the two D110 proposal-quality INPUTS on <c>journal_entries</c> —
    /// <c>prior_prob</c> (the calibration-skill channel) and <c>detectability_floor_ann</c> (the margin
    /// channel, redefined by D113 as the floor at ASSESSMENT rather than at admission).
    ///
    /// Two columns and no more: the scorer, its read-model, the route and the panel are NOT built here.
    /// A scorer built now would be a consumer for data that does not exist, and the route would serve an
    /// `insufficient` series for years. Capturing the inputs now is what keeps D110's chained criterion
    /// from having a missing first link.
    ///
    /// <c>Up</c> is genuinely additive — two <c>ALTER TABLE ADD COLUMN</c>s, no rebuild, so the rule-14
    /// hand-edit does not arise there. <c>Down</c> is a different matter: see below.
    /// </summary>
    public partial class Phase5ProposalInputs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "detectability_floor_ann",
                table: "journal_entries",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "prior_prob",
                table: "journal_entries",
                type: "REAL",
                nullable: true);
        }

        /// <summary>
        /// Hand-edited (rule 14), for the D94 reason and M9's: EF turns <c>DropColumn</c> into a
        /// whole-table REBUILD whose regenerated DDL re-adds <c>AUTOINCREMENT</c> to <c>entry_id</c> —
        /// the annotation InitialCreate's hand-edit stripped.
        ///
        /// It is worth stating that this is on the DOWN path and still matters. A rollback that quietly
        /// reshapes the table leaves the store in a state no forward migration produced, and the next Up
        /// would then apply to a schema nobody wrote. Both columns are unindexed and unconstrained, so
        /// SQLite drops them in place and the hand-edited DDL survives.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""ALTER TABLE "journal_entries" DROP COLUMN "detectability_floor_ann";""");
            migrationBuilder.Sql("""ALTER TABLE "journal_entries" DROP COLUMN "prior_prob";""");
        }
    }
}
