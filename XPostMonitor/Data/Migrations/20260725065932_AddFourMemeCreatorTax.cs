using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XPostMonitor.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFourMemeCreatorTax : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CreatorTaxPercent",
                table: "WatchlistEntries",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CreatorTaxPercent",
                table: "WatchlistEntries");
        }
    }
}
