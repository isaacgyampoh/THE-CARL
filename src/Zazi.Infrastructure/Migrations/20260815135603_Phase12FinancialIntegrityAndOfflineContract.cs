using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zazi.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Phase12FinancialIntegrityAndOfflineContract : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CapturedSmsMessages");

            migrationBuilder.DropIndex(
                name: "IX_Transactions_Organization_IdempotencyKey",
                table: "Transactions");

            migrationBuilder.DropIndex(
                name: "IX_Transactions_Organization_MessageFingerprint",
                table: "Transactions");

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
                name: "ReconciliationStatus",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "SyncStatus",
                table: "Transactions");

            migrationBuilder.RenameColumn(
                name: "TransactionAt",
                table: "Transactions",
                newName: "TransactionAtUtc");

            migrationBuilder.RenameColumn(
                name: "Status",
                table: "Transactions",
                newName: "State");

            // The column is reused, but the enum behind it changed. Old TransactionStatus
            // ordinals do not line up with TransactionLifecycleState — for example old
            // Captured(1) would read as Parsed(1), demoting an accepted ledger row back to
            // evidence. Values are remapped explicitly, highest first so no row is rewritten
            // twice by a later clause.
            //   Pending(0)->PendingReview(2)  Captured(1)->Accepted(3)  Validated(2)->Accepted(3)
            //   NeedsReview(3)->PendingReview(2)  Reconciled(4)->Synced(5)  Rejected(5)->Rejected(4)
            //   Duplicate(6)->Rejected(4)  Failed(7)->Rejected(4)
            migrationBuilder.Sql("""
                UPDATE "Transactions" SET "State" = CASE "State"
                    WHEN 7 THEN 4
                    WHEN 6 THEN 4
                    WHEN 5 THEN 4
                    WHEN 4 THEN 5
                    WHEN 3 THEN 2
                    WHEN 2 THEN 3
                    WHEN 1 THEN 3
                    WHEN 0 THEN 2
                    ELSE 2
                END;
                """);

            // EF inferred a rename from SourceDeviceId to SessionId because both are uuid.
            // They are semantically unrelated: keeping the column would silently populate
            // SessionId with device identifiers. Dropped and recreated instead.
            migrationBuilder.DropColumn(
                name: "SourceDeviceId",
                table: "Transactions");

            migrationBuilder.AddColumn<Guid>(
                name: "SessionId",
                table: "Transactions",
                type: "uuid",
                nullable: true);

            migrationBuilder.RenameIndex(
                name: "IX_Transactions_TransactionAt",
                table: "Transactions",
                newName: "IX_Transactions_TransactionAtUtc");

            // Dropping SourceDeviceId above also dropped IX_Transactions_SourceDeviceId, so
            // there is no index left to rename. SessionId gets a fresh index instead.
            migrationBuilder.CreateIndex(
                name: "IX_Transactions_SessionId",
                table: "Transactions",
                column: "SessionId");

            migrationBuilder.RenameIndex(
                name: "IX_Transactions_OrganizationId_BranchId_TransactionAt",
                table: "Transactions",
                newName: "IX_Transactions_OrganizationId_BranchId_TransactionAtUtc");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AcceptedAtUtc",
                table: "Transactions",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<Guid>(
                name: "AdjustsTransactionId",
                table: "Transactions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "CashDelta",
                table: "Transactions",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "ClientTransactionId",
                table: "Transactions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CorrectionReason",
                table: "Transactions",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EvidenceFingerprint",
                table: "Transactions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "EvidenceId",
                table: "Transactions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "FloatDelta",
                table: "Transactions",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<Guid>(
                name: "ReversesTransactionId",
                table: "Transactions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SessionId",
                table: "RefreshTokens",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MfaPolicy",
                table: "Organizations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "AuthSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: true),
                    FamilyId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    SecurityStampAtIssue = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ClientFingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CreatedFromIpPrefix = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    LastSeenAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RevokedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedReason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    MfaSatisfied = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuthSessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MfaCredentials",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Method = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    EncryptedSecret = table.Column<byte[]>(type: "bytea", nullable: true),
                    KeyId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CodeHash = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    CodeSalt = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    UsedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    VerifiedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastUsedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastAcceptedTimeStep = table.Column<long>(type: "bigint", nullable: true),
                    FailedAttempts = table.Column<int>(type: "integer", nullable: false),
                    LockedUntilUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MfaCredentials", x => x.Id);
                    table.CheckConstraint("CK_MfaCredentials_SecretShape", "(\"Method\" = 0 AND \"EncryptedSecret\" IS NOT NULL AND \"CodeHash\" IS NULL) OR (\"Method\" = 1 AND \"CodeHash\" IS NOT NULL AND \"EncryptedSecret\" IS NULL)");
                });

            migrationBuilder.CreateTable(
                name: "TransactionEvidence",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: true),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: true),
                    SubmittedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceType = table.Column<int>(type: "integer", nullable: false),
                    Provider = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    SenderAddress = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    CustomerPhoneNumber = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    Amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    Currency = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    ObservedType = table.Column<int>(type: "integer", nullable: false),
                    ProviderReference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DeviceReceivedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ServerReceivedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FingerprintVersion = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    RawHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ParserName = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    ParserVersion = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ConfidenceScore = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: false),
                    RawMessage = table.Column<string>(type: "text", nullable: true),
                    RawMessagePurgedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    State = table.Column<int>(type: "integer", nullable: false),
                    FinancialTransactionId = table.Column<Guid>(type: "uuid", nullable: true),
                    OutcomeReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    IsDuplicate = table.Column<bool>(type: "boolean", nullable: false),
                    DuplicateOfEvidenceId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TransactionEvidence", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_AdjustsTransactionId",
                table: "Transactions",
                column: "AdjustsTransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_EvidenceId",
                table: "Transactions",
                column: "EvidenceId");

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_ReversesTransactionId",
                table: "Transactions",
                column: "ReversesTransactionId");

            migrationBuilder.CreateIndex(
                name: "UX_Transactions_Organization_ClientTransactionId",
                table: "Transactions",
                columns: new[] { "OrganizationId", "ClientTransactionId" },
                unique: true,
                filter: "\"ClientTransactionId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_Transactions_Organization_EvidenceFingerprint",
                table: "Transactions",
                columns: new[] { "OrganizationId", "EvidenceFingerprint" },
                unique: true,
                filter: "\"EvidenceFingerprint\" IS NOT NULL");

            // Pre-existing rows have no stored ledger deltas. Backfill them from the
            // authoritative direction (cash-in raises cash and lowers float; cash-out the
            // reverse) so a rebuilt projection matches what was originally posted. Types
            // without an automatic rule are left at zero and must be reviewed.
            migrationBuilder.Sql("""
                UPDATE "Transactions"
                SET "CashDelta" = CASE "Type" WHEN 0 THEN "Amount" WHEN 1 THEN -"Amount" ELSE 0 END,
                    "FloatDelta" = CASE "Type" WHEN 0 THEN -"Amount" WHEN 1 THEN "Amount" WHEN 4 THEN "Amount" ELSE 0 END
                WHERE "CashDelta" = 0 AND "FloatDelta" = 0;
                """);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Transactions_AmountNonNegative",
                table: "Transactions",
                sql: "\"Amount\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Transactions_ConfidenceRange",
                table: "Transactions",
                sql: "\"ConfidenceScore\" >= 0 AND \"ConfidenceScore\" <= 1");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Transactions_ReversalHasOriginal",
                table: "Transactions",
                sql: "\"Type\" <> 3 OR \"ReversesTransactionId\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Transactions_UnknownHasNoLedgerEffect",
                table: "Transactions",
                sql: "\"Type\" <> 6 OR (\"CashDelta\" = 0 AND \"FloatDelta\" = 0)");

            migrationBuilder.CreateIndex(
                name: "IX_AuthSessions_DeviceId",
                table: "AuthSessions",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_AuthSessions_ExpiresAtUtc",
                table: "AuthSessions",
                column: "ExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AuthSessions_FamilyId",
                table: "AuthSessions",
                column: "FamilyId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuthSessions_OrganizationId_Status",
                table: "AuthSessions",
                columns: new[] { "OrganizationId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_AuthSessions_UserId_Status",
                table: "AuthSessions",
                columns: new[] { "UserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_MfaCredentials_OrganizationId",
                table: "MfaCredentials",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_MfaCredentials_UserId_Method_Status",
                table: "MfaCredentials",
                columns: new[] { "UserId", "Method", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_TransactionEvidence_FinancialTransactionId",
                table: "TransactionEvidence",
                column: "FinancialTransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_TransactionEvidence_OrganizationId",
                table: "TransactionEvidence",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_TransactionEvidence_OrganizationId_BranchId_ServerReceivedA~",
                table: "TransactionEvidence",
                columns: new[] { "OrganizationId", "BranchId", "ServerReceivedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_TransactionEvidence_OrganizationId_Fingerprint",
                table: "TransactionEvidence",
                columns: new[] { "OrganizationId", "Fingerprint" });

            migrationBuilder.CreateIndex(
                name: "IX_TransactionEvidence_OrganizationId_RawHash",
                table: "TransactionEvidence",
                columns: new[] { "OrganizationId", "RawHash" });

            migrationBuilder.CreateIndex(
                name: "IX_TransactionEvidence_State",
                table: "TransactionEvidence",
                column: "State");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuthSessions");

            migrationBuilder.DropTable(
                name: "MfaCredentials");

            migrationBuilder.DropTable(
                name: "TransactionEvidence");

            migrationBuilder.DropIndex(
                name: "IX_Transactions_AdjustsTransactionId",
                table: "Transactions");

            migrationBuilder.DropIndex(
                name: "IX_Transactions_EvidenceId",
                table: "Transactions");

            migrationBuilder.DropIndex(
                name: "IX_Transactions_ReversesTransactionId",
                table: "Transactions");

            migrationBuilder.DropIndex(
                name: "UX_Transactions_Organization_ClientTransactionId",
                table: "Transactions");

            migrationBuilder.DropIndex(
                name: "UX_Transactions_Organization_EvidenceFingerprint",
                table: "Transactions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Transactions_AmountNonNegative",
                table: "Transactions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Transactions_ConfidenceRange",
                table: "Transactions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Transactions_ReversalHasOriginal",
                table: "Transactions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Transactions_UnknownHasNoLedgerEffect",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "AcceptedAtUtc",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "AdjustsTransactionId",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "CashDelta",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "ClientTransactionId",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "CorrectionReason",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "EvidenceFingerprint",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "EvidenceId",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "FloatDelta",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "ReversesTransactionId",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "SessionId",
                table: "RefreshTokens");

            migrationBuilder.DropColumn(
                name: "MfaPolicy",
                table: "Organizations");

            migrationBuilder.RenameColumn(
                name: "TransactionAtUtc",
                table: "Transactions",
                newName: "TransactionAt");

            migrationBuilder.RenameColumn(
                name: "State",
                table: "Transactions",
                newName: "Status");

            migrationBuilder.RenameColumn(
                name: "SessionId",
                table: "Transactions",
                newName: "SourceDeviceId");

            migrationBuilder.RenameIndex(
                name: "IX_Transactions_TransactionAtUtc",
                table: "Transactions",
                newName: "IX_Transactions_TransactionAt");

            migrationBuilder.DropIndex(
                name: "IX_Transactions_SessionId",
                table: "Transactions");

            migrationBuilder.RenameIndex(
                name: "IX_Transactions_OrganizationId_BranchId_TransactionAtUtc",
                table: "Transactions",
                newName: "IX_Transactions_OrganizationId_BranchId_TransactionAt");

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

            migrationBuilder.AddColumn<string>(
                name: "ReconciliationStatus",
                table: "Transactions",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SyncStatus",
                table: "Transactions",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CapturedSmsMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric", nullable: false),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: true),
                    ConfidenceScore = table.Column<decimal>(type: "numeric", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CustomerPhoneNumber = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: true),
                    IsDuplicate = table.Column<bool>(type: "boolean", nullable: false),
                    MessageHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    MessageTimestampUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    MessageType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Network = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    NormalizedText = table.Column<string>(type: "text", nullable: true),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProcessingStatus = table.Column<int>(type: "integer", nullable: false),
                    Provider = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    ProviderReference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    RawMessage = table.Column<string>(type: "text", nullable: false),
                    SourcePhoneNumber = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    TransactionId = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CapturedSmsMessages", x => x.Id);
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
                name: "IX_CapturedSmsMessages_OrganizationId",
                table: "CapturedSmsMessages",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_CapturedSmsMessages_OrganizationId_MessageHash",
                table: "CapturedSmsMessages",
                columns: new[] { "OrganizationId", "MessageHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CapturedSmsMessages_OrganizationId_ProviderReference",
                table: "CapturedSmsMessages",
                columns: new[] { "OrganizationId", "ProviderReference" });
        }
    }
}
