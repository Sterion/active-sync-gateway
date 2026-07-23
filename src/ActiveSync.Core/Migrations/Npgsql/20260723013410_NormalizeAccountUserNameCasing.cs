using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ActiveSync.Core.Migrations.Npgsql
{
    /// <inheritdoc />
    public partial class NormalizeAccountUserNameCasing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // B1: AccountEntry.UserName is now STORED case-folded so the raw unique index enforces
            // case-folded uniqueness (see AccountStore.NormalizeLogin). Existing databases may hold a
            // pre-fix case-variant pair (e.g. `Phone1` + `phone1`) that both fit under the raw index.
            // Collapse each such pair to a single survivor — the most-recently-updated row (tie-break on
            // the highest Id) — BEFORE case-folding, otherwise the UPDATE below would collide on the index.
            migrationBuilder.Sql(
                """
                DELETE FROM "AccountEntries"
                WHERE "Id" NOT IN (
                    SELECT a."Id" FROM "AccountEntries" a
                    WHERE NOT EXISTS (
                        SELECT 1 FROM "AccountEntries" b
                        WHERE lower(b."UserName") = lower(a."UserName")
                          AND (b."UpdatedUtc" > a."UpdatedUtc"
                               OR (b."UpdatedUtc" = a."UpdatedUtc" AND b."Id" > a."Id"))
                    )
                );
                """);

            // Case-fold the survivors so index and lookup agree.
            migrationBuilder.Sql(
                """
                UPDATE "AccountEntries" SET "UserName" = lower("UserName")
                WHERE "UserName" <> lower("UserName");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // One-way data normalization: the original mixed casing and the dropped duplicate rows
            // cannot be reconstructed, so the down migration is intentionally a no-op.
        }
    }
}
