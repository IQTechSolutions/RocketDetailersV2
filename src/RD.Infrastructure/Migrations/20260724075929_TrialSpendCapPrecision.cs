using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RD.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class TrialSpendCapPrecision : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<decimal>(
                name: "SpendCapSnapshot",
                table: "TrialPeriods",
                type: "decimal(19,4)",
                precision: 19,
                scale: 4,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "decimal(18,2)",
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<decimal>(
                name: "SpendCapSnapshot",
                table: "TrialPeriods",
                type: "decimal(18,2)",
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "decimal(19,4)",
                oldPrecision: 19,
                oldScale: 4,
                oldNullable: true);
        }
    }
}
