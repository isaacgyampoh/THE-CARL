using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zazi.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ParsingReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ParsingReports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReportedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: true),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: true),
                    ClientTransactionId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    RawMessage = table.Column<string>(type: "text", nullable: false),
                    SenderIdentity = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    ObservedNetwork = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ObservedType = table.Column<int>(type: "integer", nullable: false),
                    ObservedAmountMinor = table.Column<long>(type: "bigint", nullable: false),
                    Verdict = table.Column<int>(type: "integer", nullable: false),
                    Note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ParserVersion = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    AppVersion = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    ReviewedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReviewedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ParsingReports", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ParsingReports_OrganizationId",
                table: "ParsingReports",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_ParsingReports_OrganizationId_ReportedByUserId_ClientTransa~",
                table: "ParsingReports",
                columns: new[] { "OrganizationId", "ReportedByUserId", "ClientTransactionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ParsingReports_ReviewedAtUtc_CreatedAt",
                table: "ParsingReports",
                columns: new[] { "ReviewedAtUtc", "CreatedAt" },
                filter: "\"ReviewedAtUtc\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ParsingReports");
        }
    }
}
