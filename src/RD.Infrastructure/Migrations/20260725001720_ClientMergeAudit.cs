using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RD.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ClientMergeAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClientMergeAudits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SurvivorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DuplicateId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MergedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    MergedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SnapshotJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ReversedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReversedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientMergeAudits", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClientMergeAudits_DuplicateId_ReversedAt",
                table: "ClientMergeAudits",
                columns: new[] { "DuplicateId", "ReversedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClientMergeAudits");
        }
    }
}
