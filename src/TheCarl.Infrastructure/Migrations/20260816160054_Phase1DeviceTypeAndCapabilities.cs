using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TheCarl.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Phase1DeviceTypeAndCapabilities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DeviceType",
                table: "Devices",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Backfill from the legacy free-text Platform column BEFORE the CHECK constraint
            // is added, so the constraint validates real data rather than a blanket default.
            //
            // Leaving every existing row at 0 (Other) would mislabel every enrolled Android
            // handset and silently strip its SmsCapture capability, because capabilities are
            // granted from DeviceType.
            //
            // This mirrors DeviceTypeMapping.FromPlatformString exactly, including the order:
            // tablet is tested before phone ("androidtablet" also contains "android"), and
            // anything unrecognised deliberately stays Other rather than being guessed at.
            //   0 Other · 1 AndroidPhone · 2 AndroidTablet · 3 iPhone · 4 iPad
            //   5 WebBrowser · 6 GsmGateway
            migrationBuilder.Sql("""
                UPDATE "Devices"
                SET "DeviceType" = CASE
                    WHEN normalized LIKE '%ipad%'                              THEN 4
                    -- 'ios' is anchored, not a substring: KaiOS normalises to 'kaios',
                    -- which contains 'ios' and would otherwise be backfilled as an iPhone.
                    WHEN normalized LIKE '%iphone%' OR normalized LIKE 'ios%' THEN 3
                    WHEN normalized LIKE '%android%'
                         AND (normalized LIKE '%tablet%' OR normalized LIKE '%tab%') THEN 2
                    WHEN normalized LIKE '%android%'                           THEN 1
                    WHEN normalized LIKE '%gsmgateway%' OR normalized LIKE '%gateway%' THEN 6
                    WHEN normalized LIKE '%web%' OR normalized LIKE '%browser%' THEN 5
                    ELSE 0
                END
                FROM (
                    SELECT "Id" AS device_id,
                           regexp_replace(lower(coalesce("Platform", '')), '[^a-z0-9]', '', 'g') AS normalized
                    FROM "Devices"
                ) AS mapped
                WHERE "Devices"."Id" = mapped.device_id;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Devices_OrganizationId_DeviceType",
                table: "Devices",
                columns: new[] { "OrganizationId", "DeviceType" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Devices_DeviceTypeInRange",
                table: "Devices",
                sql: "\"DeviceType\" >= 0 AND \"DeviceType\" <= 6");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Devices_OrganizationId_DeviceType",
                table: "Devices");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Devices_DeviceTypeInRange",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "DeviceType",
                table: "Devices");
        }
    }
}
