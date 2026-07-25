using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XPostMonitor.Data.Migrations
{
    /// <inheritdoc />
    public partial class FixAutoTradingPricePrecision : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<decimal>(
                name: "EntryPrice",
                table: "AutoTrades",
                type: "decimal(28,20)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "decimal(38,30)");

            migrationBuilder.AlterColumn<decimal>(
                name: "TargetPrice",
                table: "AutoTradeOrders",
                type: "decimal(28,20)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "decimal(38,30)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<decimal>(
                name: "EntryPrice",
                table: "AutoTrades",
                type: "decimal(38,30)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "decimal(28,20)");

            migrationBuilder.AlterColumn<decimal>(
                name: "TargetPrice",
                table: "AutoTradeOrders",
                type: "decimal(38,30)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "decimal(28,20)");
        }
    }
}
