using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zazi.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class TransactionsAppendOnly : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A recorded transaction is a fact, and in a dispute — "I came at 11:50 and withdrew
            // fifty cedis" — it has to be the same fact it was at 11:50. The application already
            // never edits or deletes one: a mistake is corrected by a new Reversal or Adjustment
            // row that points at the original. This makes the database refuse it too, so the
            // guarantee does not rest on every future line of code remembering the rule, and a
            // record can be shown to a customer as untouched.
            //
            // Only the facts are frozen. The lifecycle columns and UpdatedAt may still move, as
            // they do today.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION zazi_transactions_are_append_only() RETURNS trigger AS $$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'A recorded transaction cannot be deleted. Record a reversal instead.'
                            USING ERRCODE = 'restrict_violation';
                    END IF;

                    IF NEW."OrganizationId"      IS DISTINCT FROM OLD."OrganizationId"
                    OR NEW."BranchId"            IS DISTINCT FROM OLD."BranchId"
                    OR NEW."AgentId"             IS DISTINCT FROM OLD."AgentId"
                    OR NEW."Network"             IS DISTINCT FROM OLD."Network"
                    OR NEW."Type"                IS DISTINCT FROM OLD."Type"
                    OR NEW."Amount"              IS DISTINCT FROM OLD."Amount"
                    OR NEW."Currency"            IS DISTINCT FROM OLD."Currency"
                    OR NEW."CashDelta"           IS DISTINCT FROM OLD."CashDelta"
                    OR NEW."FloatDelta"          IS DISTINCT FROM OLD."FloatDelta"
                    OR NEW."CustomerPhoneNumber" IS DISTINCT FROM OLD."CustomerPhoneNumber"
                    OR NEW."ProviderReference"   IS DISTINCT FROM OLD."ProviderReference"
                    OR NEW."TransactionAtUtc"    IS DISTINCT FROM OLD."TransactionAtUtc"
                    OR NEW."ClientTransactionId" IS DISTINCT FROM OLD."ClientTransactionId"
                    THEN
                        RAISE EXCEPTION 'A recorded transaction cannot be changed. Record a reversal or an adjustment instead.'
                            USING ERRCODE = 'restrict_violation';
                    END IF;

                    RETURN NEW;
                END
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER transactions_append_only
                    BEFORE UPDATE OR DELETE ON "Transactions"
                    FOR EACH ROW EXECUTE FUNCTION zazi_transactions_are_append_only();
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_OrganizationId_CustomerPhoneNumber",
                table: "Transactions",
                columns: new[] { "OrganizationId", "CustomerPhoneNumber" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS transactions_append_only ON "Transactions";
                DROP FUNCTION IF EXISTS zazi_transactions_are_append_only();
                """);

            migrationBuilder.DropIndex(
                name: "IX_Transactions_OrganizationId_CustomerPhoneNumber",
                table: "Transactions");
        }
    }
}
