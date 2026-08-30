using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zazi.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SmsCaptureAndSyncQueue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CapturedSmsMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: true),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: true),
                    SourcePhoneNumber = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    RawMessage = table.Column<string>(type: "text", nullable: false),
                    MessageHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Provider = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Network = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    MessageType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric", nullable: false),
                    CustomerPhoneNumber = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    ProviderReference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    MessageTimestampUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ConfidenceScore = table.Column<decimal>(type: "numeric", nullable: false),
                    ProcessingStatus = table.Column<int>(type: "integer", nullable: false),
                    IsDuplicate = table.Column<bool>(type: "boolean", nullable: false),
                    NormalizedText = table.Column<string>(type: "text", nullable: true),
                    TransactionId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CapturedSmsMessages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SyncQueue",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: true),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: true),
                    EntityId = table.Column<Guid>(type: "uuid", nullable: true),
                    EntityType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    EventType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Payload = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    RetryCount = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ErrorMessage = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    IsManual = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncQueue", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CapturedSmsMessages_MessageHash",
                table: "CapturedSmsMessages",
                column: "MessageHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CapturedSmsMessages_OrganizationId",
                table: "CapturedSmsMessages",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_CapturedSmsMessages_ProviderReference",
                table: "CapturedSmsMessages",
                column: "ProviderReference");

            migrationBuilder.CreateIndex(
                name: "IX_SyncQueue_NextAttemptAtUtc",
                table: "SyncQueue",
                column: "NextAttemptAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_SyncQueue_OrganizationId",
                table: "SyncQueue",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncQueue_Status",
                table: "SyncQueue",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CapturedSmsMessages");

            migrationBuilder.DropTable(
                name: "SyncQueue");
        }
    }
}
