using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RD.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ClientMerge : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "MergedAt",
                table: "Clients",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "MergedIntoClientId",
                table: "Clients",
                type: "uniqueidentifier",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MergedAt",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "MergedIntoClientId",
                table: "Clients");
        }
    }
}
