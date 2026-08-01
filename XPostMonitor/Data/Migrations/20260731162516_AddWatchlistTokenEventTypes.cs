using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XPostMonitor.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWatchlistTokenEventTypes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CreateTokenOnPost",
                table: "WatchlistEntries",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "CreateTokenOnQuote",
                table: "WatchlistEntries",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "CreateTokenOnReply",
                table: "WatchlistEntries",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "CreateTokenOnRepost",
                table: "WatchlistEntries",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CreateTokenOnPost",
                table: "WatchlistEntries");

            migrationBuilder.DropColumn(
                name: "CreateTokenOnQuote",
                table: "WatchlistEntries");

            migrationBuilder.DropColumn(
                name: "CreateTokenOnReply",
                table: "WatchlistEntries");

            migrationBuilder.DropColumn(
                name: "CreateTokenOnRepost",
                table: "WatchlistEntries");
        }
    }
}
