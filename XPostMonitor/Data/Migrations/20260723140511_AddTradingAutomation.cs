using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XPostMonitor.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTradingAutomation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BuyAmount",
                table: "UserTradingSettings");

            migrationBuilder.DropColumn(
                name: "SlippagePercent",
                table: "UserTradingSettings");

            migrationBuilder.DropColumn(
                name: "WalletAddress",
                table: "UserTradingSettings");

            migrationBuilder.RenameColumn(
                name: "EncryptedPrivateKey",
                table: "UserTradingSettings",
                newName: "EncryptedGmgnPrivateKey");

            migrationBuilder.AddColumn<string>(
                name: "TokenChain",
                table: "WatchlistEntries",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TokenDex",
                table: "WatchlistEntries",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "UserChainTradingSettings",
                columns: table => new
                {
                    ChatId = table.Column<long>(type: "bigint", nullable: false),
                    Chain = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    BuyAmount = table.Column<decimal>(type: "decimal(18,8)", nullable: false),
                    SlippagePercent = table.Column<decimal>(type: "decimal(5,2)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserChainTradingSettings", x => new { x.ChatId, x.Chain });
                    table.ForeignKey(
                        name: "FK_UserChainTradingSettings_TelegramUsers_ChatId",
                        column: x => x.ChatId,
                        principalTable: "TelegramUsers",
                        principalColumn: "ChatId",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserChainTradingSettings");

            migrationBuilder.DropColumn(
                name: "TokenChain",
                table: "WatchlistEntries");

            migrationBuilder.DropColumn(
                name: "TokenDex",
                table: "WatchlistEntries");

            migrationBuilder.RenameColumn(
                name: "EncryptedGmgnPrivateKey",
                table: "UserTradingSettings",
                newName: "EncryptedPrivateKey");

            migrationBuilder.AddColumn<decimal>(
                name: "BuyAmount",
                table: "UserTradingSettings",
                type: "decimal(18,8)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "SlippagePercent",
                table: "UserTradingSettings",
                type: "decimal(5,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "WalletAddress",
                table: "UserTradingSettings",
                type: "nvarchar(42)",
                maxLength: 42,
                nullable: false,
                defaultValue: "");
        }
    }
}
