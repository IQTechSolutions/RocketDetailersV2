using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RD.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class IdentityLinkAllowMultipleStripeAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_IdentityLinks_ClientId_System_Kind",
                table: "IdentityLinks");

            migrationBuilder.CreateIndex(
                name: "IX_IdentityLinks_ClientId_System_Kind",
                table: "IdentityLinks",
                columns: new[] { "ClientId", "System", "Kind" },
                unique: true,
                filter: "[InvalidatedAt] IS NULL AND [Kind] = 'Contact'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_IdentityLinks_ClientId_System_Kind",
                table: "IdentityLinks");

            migrationBuilder.CreateIndex(
                name: "IX_IdentityLinks_ClientId_System_Kind",
                table: "IdentityLinks",
                columns: new[] { "ClientId", "System", "Kind" },
                unique: true,
                filter: "[InvalidatedAt] IS NULL AND [Kind] IN ('Customer','Subscription','Contact')");
        }
    }
}
