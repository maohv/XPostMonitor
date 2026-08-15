using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XPostMonitor.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFlapTaxAllocation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FlapHolderPercent",
                table: "UserTradingSettings",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FlapHolderPercent",
                table: "UserTradingSettings");
        }
    }
}
