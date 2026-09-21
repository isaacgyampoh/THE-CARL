using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zazi.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PerAgentBalances : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_FloatBalances_OrganizationId_BranchId_Network",
                table: "FloatBalances");

            migrationBuilder.DropIndex(
                name: "IX_CashBalances_OrganizationId_BranchId",
                table: "CashBalances");

            migrationBuilder.AddColumn<Guid>(
                name: "AgentId",
                table: "FloatBalances",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AgentId",
                table: "CashBalances",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_FloatBalances_OrganizationId_BranchId_AgentId_Network",
                table: "FloatBalances",
                columns: new[] { "OrganizationId", "BranchId", "AgentId", "Network" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CashBalances_OrganizationId_BranchId_AgentId",
                table: "CashBalances",
                columns: new[] { "OrganizationId", "BranchId", "AgentId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_FloatBalances_OrganizationId_BranchId_AgentId_Network",
                table: "FloatBalances");

            migrationBuilder.DropIndex(
                name: "IX_CashBalances_OrganizationId_BranchId_AgentId",
                table: "CashBalances");

            migrationBuilder.DropColumn(
                name: "AgentId",
                table: "FloatBalances");

            migrationBuilder.DropColumn(
                name: "AgentId",
                table: "CashBalances");

            migrationBuilder.CreateIndex(
                name: "IX_FloatBalances_OrganizationId_BranchId_Network",
                table: "FloatBalances",
                columns: new[] { "OrganizationId", "BranchId", "Network" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CashBalances_OrganizationId_BranchId",
                table: "CashBalances",
                columns: new[] { "OrganizationId", "BranchId" },
                unique: true);
        }
    }
}
