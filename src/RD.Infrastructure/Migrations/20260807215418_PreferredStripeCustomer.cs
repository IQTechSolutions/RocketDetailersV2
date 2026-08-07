using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RD.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PreferredStripeCustomer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PaidAt",
                table: "StripeInvoices",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "BillingStartedAt",
                table: "ConvertIntents",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreferredStripeCustomerId",
                table: "Clients",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "StripeCustomerPreferenceChanges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PreviousStripeCustomerId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    PreferredStripeCustomerId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ChangedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ChangedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    InvestigationItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StripeCustomerPreferenceChanges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StripeCustomerPreferenceChanges_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StripeCustomerPreferenceChanges_InvestigationItems_InvestigationItemId",
                        column: x => x.InvestigationItemId,
                        principalTable: "InvestigationItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StripeCustomerPreferenceChanges_ClientId_ChangedAt",
                table: "StripeCustomerPreferenceChanges",
                columns: new[] { "ClientId", "ChangedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_StripeCustomerPreferenceChanges_InvestigationItemId",
                table: "StripeCustomerPreferenceChanges",
                column: "InvestigationItemId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StripeCustomerPreferenceChanges");

            migrationBuilder.DropColumn(
                name: "PaidAt",
                table: "StripeInvoices");

            migrationBuilder.DropColumn(
                name: "BillingStartedAt",
                table: "ConvertIntents");

            migrationBuilder.DropColumn(
                name: "PreferredStripeCustomerId",
                table: "Clients");
        }
    }
}
