using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TheCarl.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Phase4DeviceEnrollment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DeviceEnrollmentCodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: false),
                    CodeHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CodePrefix = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    DeviceRole = table.Column<int>(type: "integer", nullable: false),
                    IntendedUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    IssuedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    RedeemedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RedeemedByDeviceId = table.Column<Guid>(type: "uuid", nullable: true),
                    RevokedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    FailedAttempts = table.Column<int>(type: "integer", nullable: false),
                    Label = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceEnrollmentCodes", x => x.Id);
                    table.CheckConstraint("CK_DeviceEnrollmentCodes_MustExpire", "\"ExpiresAtUtc\" > \"CreatedAt\"");
                    table.CheckConstraint("CK_DeviceEnrollmentCodes_TerminalStateHasTimestamp", "(\"Status\" = 0) OR (\"Status\" = 1 AND \"RedeemedAtUtc\" IS NOT NULL) OR (\"Status\" = 2 AND \"RevokedAtUtc\" IS NOT NULL)");
                });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceEnrollmentCodes_CodeHash",
                table: "DeviceEnrollmentCodes",
                column: "CodeHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeviceEnrollmentCodes_OrganizationId_BranchId",
                table: "DeviceEnrollmentCodes",
                columns: new[] { "OrganizationId", "BranchId" });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceEnrollmentCodes_OrganizationId_Status_ExpiresAtUtc",
                table: "DeviceEnrollmentCodes",
                columns: new[] { "OrganizationId", "Status", "ExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceEnrollmentCodes_RedeemedByDeviceId",
                table: "DeviceEnrollmentCodes",
                column: "RedeemedByDeviceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeviceEnrollmentCodes");
        }
    }
}
