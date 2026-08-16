using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TheCarl.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Phase3ReversalInvariantAndConflicts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SyncConflicts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: true),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: true),
                    SubmittedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ClientTransactionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TransactionId = table.Column<Guid>(type: "uuid", nullable: true),
                    RelatedTransactionId = table.Column<Guid>(type: "uuid", nullable: true),
                    ConflictType = table.Column<int>(type: "integer", nullable: false),
                    ReasonCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    BatchId = table.Column<Guid>(type: "uuid", nullable: true),
                    CorrelationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    SubmittedAmount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    SubmittedCurrency = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    SubmittedType = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ResolvedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ResolvedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Resolution = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ResolutionNotes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncConflicts", x => x.Id);
                    table.CheckConstraint("CK_SyncConflicts_ResolutionComplete", "(\"Status\" IN (0, 1) AND \"ResolvedAtUtc\" IS NULL AND \"ResolvedByUserId\" IS NULL) OR (\"Status\" IN (2, 3) AND \"ResolvedAtUtc\" IS NOT NULL AND \"ResolvedByUserId\" IS NOT NULL)");
                });

            migrationBuilder.CreateIndex(
                name: "UX_Transactions_Organization_ReversesTransactionId",
                table: "Transactions",
                columns: new[] { "OrganizationId", "ReversesTransactionId" },
                unique: true,
                filter: "\"ReversesTransactionId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SyncConflicts_BatchId",
                table: "SyncConflicts",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncConflicts_OrganizationId_ClientTransactionId",
                table: "SyncConflicts",
                columns: new[] { "OrganizationId", "ClientTransactionId" });

            migrationBuilder.CreateIndex(
                name: "IX_SyncConflicts_OrganizationId_Status_CreatedAt",
                table: "SyncConflicts",
                columns: new[] { "OrganizationId", "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SyncConflicts_RelatedTransactionId",
                table: "SyncConflicts",
                column: "RelatedTransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncConflicts_TransactionId",
                table: "SyncConflicts",
                column: "TransactionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SyncConflicts");

            migrationBuilder.DropIndex(
                name: "UX_Transactions_Organization_ReversesTransactionId",
                table: "Transactions");
        }
    }
}
