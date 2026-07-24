using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RD.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AdAccountLinksShareable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_IdentityLinks_System_Kind_ExternalId",
                table: "IdentityLinks");

            migrationBuilder.CreateIndex(
                name: "IX_IdentityLinks_System_Kind_ExternalId",
                table: "IdentityLinks",
                columns: new[] { "System", "Kind", "ExternalId" },
                unique: true,
                filter: "[Kind] <> 'AdAccount'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_IdentityLinks_System_Kind_ExternalId",
                table: "IdentityLinks");

            migrationBuilder.CreateIndex(
                name: "IX_IdentityLinks_System_Kind_ExternalId",
                table: "IdentityLinks",
                columns: new[] { "System", "Kind", "ExternalId" },
                unique: true);
        }
    }
}
