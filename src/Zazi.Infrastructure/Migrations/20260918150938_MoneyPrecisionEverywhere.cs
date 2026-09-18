using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zazi.Infrastructure.Migrations
{
    /// <summary>
    /// Brings every money column to numeric(18,4), which the schema already claimed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ten columns were unconstrained <c>numeric</c> — arbitrary precision — while the
    /// columns written from the very same values were <c>numeric(18,4)</c>. A session's
    /// opening cash and its own cash balance therefore disagreed about one figure whenever a
    /// client sent more than four decimal places, and every reconciliation number an operator
    /// acts on sat on the loose side of that divide.
    /// </para>
    /// <para>
    /// <b>This rounds existing values.</b> EF flags it as possible data loss and that is
    /// accurate: a stored 100.00005 becomes 100.0001. It is the intended correction rather
    /// than a side effect — those digits were never a currency amount and nothing could
    /// render them — but it is a write to financial history, so review the affected rows
    /// before applying it to a database that has been in service:
    /// </para>
    /// <code>
    /// SELECT id, "OpeningCash" FROM "Sessions" WHERE "OpeningCash" &lt;&gt; round("OpeningCash", 4);
    /// SELECT id, "Difference"  FROM "Reconciliations" WHERE "Difference" &lt;&gt; round("Difference", 4);
    /// </code>
    /// <para>
    /// An empty result means the rounding is a no-op and this is purely a schema tightening.
    /// Down widens the columns again and loses nothing, but it does not restore digits this
    /// migration rounded away — they are gone once Up has run.
    /// </para>
    /// </remarks>
    public partial class MoneyPrecisionEverywhere : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<decimal>(
                name: "OpeningFloat",
                table: "Sessions",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric");

            migrationBuilder.AlterColumn<decimal>(
                name: "OpeningCash",
                table: "Sessions",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric");

            migrationBuilder.AlterColumn<decimal>(
                name: "OpeningCash",
                table: "Reconciliations",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric");

            migrationBuilder.AlterColumn<decimal>(
                name: "ExpectedCash",
                table: "Reconciliations",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric");

            migrationBuilder.AlterColumn<decimal>(
                name: "Difference",
                table: "Reconciliations",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric");

            migrationBuilder.AlterColumn<decimal>(
                name: "CashOutflow",
                table: "Reconciliations",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric");

            migrationBuilder.AlterColumn<decimal>(
                name: "CashInflow",
                table: "Reconciliations",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric");

            migrationBuilder.AlterColumn<decimal>(
                name: "ActualCash",
                table: "Reconciliations",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric");

            migrationBuilder.AlterColumn<decimal>(
                name: "OpeningFloat",
                table: "FloatBalances",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric");

            migrationBuilder.AlterColumn<decimal>(
                name: "OpeningCash",
                table: "CashBalances",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<decimal>(
                name: "OpeningFloat",
                table: "Sessions",
                type: "numeric",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,4)",
                oldPrecision: 18,
                oldScale: 4);

            migrationBuilder.AlterColumn<decimal>(
                name: "OpeningCash",
                table: "Sessions",
                type: "numeric",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,4)",
                oldPrecision: 18,
                oldScale: 4);

            migrationBuilder.AlterColumn<decimal>(
                name: "OpeningCash",
                table: "Reconciliations",
                type: "numeric",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,4)",
                oldPrecision: 18,
                oldScale: 4);

            migrationBuilder.AlterColumn<decimal>(
                name: "ExpectedCash",
                table: "Reconciliations",
                type: "numeric",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,4)",
                oldPrecision: 18,
                oldScale: 4);

            migrationBuilder.AlterColumn<decimal>(
                name: "Difference",
                table: "Reconciliations",
                type: "numeric",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,4)",
                oldPrecision: 18,
                oldScale: 4);

            migrationBuilder.AlterColumn<decimal>(
                name: "CashOutflow",
                table: "Reconciliations",
                type: "numeric",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,4)",
                oldPrecision: 18,
                oldScale: 4);

            migrationBuilder.AlterColumn<decimal>(
                name: "CashInflow",
                table: "Reconciliations",
                type: "numeric",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,4)",
                oldPrecision: 18,
                oldScale: 4);

            migrationBuilder.AlterColumn<decimal>(
                name: "ActualCash",
                table: "Reconciliations",
                type: "numeric",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,4)",
                oldPrecision: 18,
                oldScale: 4);

            migrationBuilder.AlterColumn<decimal>(
                name: "OpeningFloat",
                table: "FloatBalances",
                type: "numeric",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,4)",
                oldPrecision: 18,
                oldScale: 4);

            migrationBuilder.AlterColumn<decimal>(
                name: "OpeningCash",
                table: "CashBalances",
                type: "numeric",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,4)",
                oldPrecision: 18,
                oldScale: 4);
        }
    }
}
