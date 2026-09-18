using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zazi.Infrastructure.Migrations
{
    /// <summary>
    /// Indexes transactions by organization and date together.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both hot read paths — the dashboard's "today" aggregate and the paged transaction
    /// list — filter by organization and a date range, and the list then sorts by the same
    /// column. With only single-column indexes PostgreSQL scans a day across every tenant
    /// and discards the rest, then sorts what survives.
    /// </para>
    /// <para>
    /// Built CONCURRENTLY, which is why this is hand-written rather than the scaffolded
    /// CreateIndex. A plain CREATE INDEX holds a lock that blocks INSERT for the duration,
    /// and the writers on this table are agents' handsets syncing captured transactions —
    /// stalling them is the one thing a migration here must not do. CONCURRENTLY cannot run
    /// inside a transaction, hence suppressTransaction.
    /// </para>
    /// <para>
    /// A CONCURRENTLY build that fails leaves an index marked INVALID behind; it is not used
    /// by queries, and re-running this migration after dropping it is safe. Check with:
    /// <c>SELECT indexrelid::regclass FROM pg_index WHERE NOT indisvalid;</c>
    /// </para>
    /// </remarks>
    public partial class TransactionOrganizationDateIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_Transactions_OrganizationId_TransactionAtUtc"
                ON "Transactions" ("OrganizationId", "TransactionAtUtc");
                """,
                suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP INDEX CONCURRENTLY IF EXISTS "IX_Transactions_OrganizationId_TransactionAtUtc";
                """,
                suppressTransaction: true);
        }
    }
}
