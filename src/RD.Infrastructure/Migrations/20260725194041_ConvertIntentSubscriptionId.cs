using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RD.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ConvertIntentSubscriptionId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "StripeSubscriptionId",
                table: "ConvertIntents",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StripeSubscriptionId",
                table: "ConvertIntents");
        }
    }
}
