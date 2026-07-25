using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RD.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ConvertIntentCloseTagWrittenAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CloseTagWrittenAt",
                table: "ConvertIntents",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConvertIntents_State_CloseTagWrittenAt",
                table: "ConvertIntents",
                columns: new[] { "State", "CloseTagWrittenAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ConvertIntents_State_CloseTagWrittenAt",
                table: "ConvertIntents");

            migrationBuilder.DropColumn(
                name: "CloseTagWrittenAt",
                table: "ConvertIntents");
        }
    }
}
