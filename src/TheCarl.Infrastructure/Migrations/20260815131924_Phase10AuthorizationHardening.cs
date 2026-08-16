using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TheCarl.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Phase10AuthorizationHardening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Roles_Users_OrganizationId",
                table: "Roles");

            migrationBuilder.DropIndex(
                name: "IX_RefreshTokens_Token",
                table: "RefreshTokens");

            migrationBuilder.DropIndex(
                name: "IX_CapturedSmsMessages_MessageHash",
                table: "CapturedSmsMessages");

            migrationBuilder.DropIndex(
                name: "IX_CapturedSmsMessages_ProviderReference",
                table: "CapturedSmsMessages");

            // Refresh tokens were stored in clear text and are now stored as SHA-256 hashes.
            // The original secrets cannot be recovered to backfill, and leaving the rows in
            // place would give every existing row TokenHash = '' and break the new unique
            // index. Purging them logs every client out once, which is the correct and
            // intended consequence of rotating credential storage.
            migrationBuilder.Sql("DELETE FROM \"RefreshTokens\";");

            migrationBuilder.DropColumn(
                name: "Token",
                table: "RefreshTokens");

            migrationBuilder.AddColumn<string>(
                name: "IdempotencyKey",
                table: "Transactions",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OriginalMessageFingerprint",
                table: "Transactions",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ParserVersion",
                table: "Transactions",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SourceDeviceId",
                table: "Transactions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReplacedByTokenHash",
                table: "RefreshTokens",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RevokedReason",
                table: "RefreshTokens",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TokenHash",
                table: "RefreshTokens",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Network",
                table: "Alerts",
                type: "character varying(80)",
                maxLength: 80,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<decimal>(
                name: "Threshold",
                table: "Alerts",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "AlertThresholds",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: true),
                    Network = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    WarningThreshold = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    CriticalThreshold = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlertThresholds", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DeadLetterTransactions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: true),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: true),
                    TransactionId = table.Column<Guid>(type: "uuid", nullable: true),
                    EntityType = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Payload = table.Column<string>(type: "text", nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    RetryCount = table.Column<int>(type: "integer", nullable: false),
                    DeadLetteredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeadLetterTransactions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DeviceLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceDeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    LinkedDeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    LinkType = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    LinkedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UnlinkedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceLinks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DeviceRegistrations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceIdentifier = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Platform = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    RegistrationToken = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    RegisteredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceRegistrations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SyncAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: true),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: true),
                    TransactionId = table.Column<Guid>(type: "uuid", nullable: true),
                    EntityType = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    AttemptNumber = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ErrorMessage = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    AttemptedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncAttempts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserRoles",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoleId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserRoles", x => new { x.UserId, x.RoleId });
                    table.ForeignKey(
                        name: "FK_UserRoles_Roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserRoles_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_Organization_IdempotencyKey",
                table: "Transactions",
                columns: new[] { "OrganizationId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_Organization_MessageFingerprint",
                table: "Transactions",
                columns: new[] { "OrganizationId", "OriginalMessageFingerprint" });

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_SourceDeviceId",
                table: "Transactions",
                column: "SourceDeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_RefreshTokens_ExpiresAtUtc",
                table: "RefreshTokens",
                column: "ExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_RefreshTokens_TokenHash",
                table: "RefreshTokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CapturedSmsMessages_OrganizationId_MessageHash",
                table: "CapturedSmsMessages",
                columns: new[] { "OrganizationId", "MessageHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CapturedSmsMessages_OrganizationId_ProviderReference",
                table: "CapturedSmsMessages",
                columns: new[] { "OrganizationId", "ProviderReference" });

            migrationBuilder.CreateIndex(
                name: "IX_AlertThresholds_OrganizationId_BranchId_Network",
                table: "AlertThresholds",
                columns: new[] { "OrganizationId", "BranchId", "Network" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeadLetterTransactions_OrganizationId",
                table: "DeadLetterTransactions",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_DeadLetterTransactions_TransactionId",
                table: "DeadLetterTransactions",
                column: "TransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceLinks_OrganizationId_SourceDeviceId_LinkedDeviceId",
                table: "DeviceLinks",
                columns: new[] { "OrganizationId", "SourceDeviceId", "LinkedDeviceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeviceRegistrations_OrganizationId_DeviceIdentifier",
                table: "DeviceRegistrations",
                columns: new[] { "OrganizationId", "DeviceIdentifier" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SyncAttempts_OrganizationId_TransactionId_AttemptNumber",
                table: "SyncAttempts",
                columns: new[] { "OrganizationId", "TransactionId", "AttemptNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SyncAttempts_Status",
                table: "SyncAttempts",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_UserRoles_RoleId",
                table: "UserRoles",
                column: "RoleId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AlertThresholds");

            migrationBuilder.DropTable(
                name: "DeadLetterTransactions");

            migrationBuilder.DropTable(
                name: "DeviceLinks");

            migrationBuilder.DropTable(
                name: "DeviceRegistrations");

            migrationBuilder.DropTable(
                name: "SyncAttempts");

            migrationBuilder.DropTable(
                name: "UserRoles");

            migrationBuilder.DropIndex(
                name: "IX_Transactions_Organization_IdempotencyKey",
                table: "Transactions");

            migrationBuilder.DropIndex(
                name: "IX_Transactions_Organization_MessageFingerprint",
                table: "Transactions");

            migrationBuilder.DropIndex(
                name: "IX_Transactions_SourceDeviceId",
                table: "Transactions");

            migrationBuilder.DropIndex(
                name: "IX_RefreshTokens_ExpiresAtUtc",
                table: "RefreshTokens");

            migrationBuilder.DropIndex(
                name: "IX_RefreshTokens_TokenHash",
                table: "RefreshTokens");

            migrationBuilder.DropIndex(
                name: "IX_CapturedSmsMessages_OrganizationId_MessageHash",
                table: "CapturedSmsMessages");

            migrationBuilder.DropIndex(
                name: "IX_CapturedSmsMessages_OrganizationId_ProviderReference",
                table: "CapturedSmsMessages");

            migrationBuilder.DropColumn(
                name: "IdempotencyKey",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "OriginalMessageFingerprint",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "ParserVersion",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "SourceDeviceId",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "ReplacedByTokenHash",
                table: "RefreshTokens");

            migrationBuilder.DropColumn(
                name: "RevokedReason",
                table: "RefreshTokens");

            migrationBuilder.DropColumn(
                name: "TokenHash",
                table: "RefreshTokens");

            migrationBuilder.DropColumn(
                name: "Network",
                table: "Alerts");

            migrationBuilder.DropColumn(
                name: "Threshold",
                table: "Alerts");

            migrationBuilder.AddColumn<string>(
                name: "Token",
                table: "RefreshTokens",
                type: "character varying(500)",
                maxLength: 500,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_RefreshTokens_Token",
                table: "RefreshTokens",
                column: "Token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CapturedSmsMessages_MessageHash",
                table: "CapturedSmsMessages",
                column: "MessageHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CapturedSmsMessages_ProviderReference",
                table: "CapturedSmsMessages",
                column: "ProviderReference");

            migrationBuilder.AddForeignKey(
                name: "FK_Roles_Users_OrganizationId",
                table: "Roles",
                column: "OrganizationId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
