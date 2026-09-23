using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zazi.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class NetworkTransactionIdIsUnique : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "UX_Transactions_Organization_Network_ProviderReference",
                table: "Transactions",
                columns: new[] { "OrganizationId", "Network", "ProviderReference" },
                unique: true,
                filter: "\"ProviderReference\" IS NOT NULL AND \"Source\" = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_Transactions_Organization_Network_ProviderReference",
                table: "Transactions");
        }
    }
}
