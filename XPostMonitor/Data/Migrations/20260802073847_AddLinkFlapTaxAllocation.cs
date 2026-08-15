using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XPostMonitor.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLinkFlapTaxAllocation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FlapHolderPercent",
                table: "LinkTokenSettings",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FlapHolderPercent",
                table: "LinkTokenSettings");
        }
    }
}
