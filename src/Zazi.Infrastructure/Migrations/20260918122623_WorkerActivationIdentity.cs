using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zazi.Infrastructure.Migrations
{
    /// <summary>
    /// Makes room for a worker who authenticates by activation code instead of a password.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two changes, both additive. <c>Email</c> becomes nullable so a worker with no account
    /// of their own can exist without a synthetic address; PostgreSQL treats nulls as
    /// distinct, so the unique index on (OrganizationId, Email) still protects real
    /// addresses. <c>CredentialType</c> defaults to 0 (Password), so every existing row keeps
    /// exactly its current meaning and no account changes behaviour.
    /// </para>
    /// <para>
    /// <b>Down is only safe before any worker exists.</b> Restoring NOT NULL on Email cannot
    /// succeed once credential-less workers are present: the column default does not backfill
    /// existing nulls, and filling them with the empty string would collide on the unique
    /// index the moment an organization has two such workers. This is inherent rather than an
    /// oversight — a worker created without an address has no address to restore. Rolling
    /// back after workers exist means deciding what those workers become, which is a data
    /// decision and not a schema one.
    /// </para>
    /// </remarks>
    public partial class WorkerActivationIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Email",
                table: "Users",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(255)",
                oldMaxLength: 255);

            migrationBuilder.AddColumn<int>(
                name: "CredentialType",
                table: "Users",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CredentialType",
                table: "Users");

            migrationBuilder.AlterColumn<string>(
                name: "Email",
                table: "Users",
                type: "character varying(255)",
                maxLength: 255,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(255)",
                oldMaxLength: 255,
                oldNullable: true);
        }
    }
}
