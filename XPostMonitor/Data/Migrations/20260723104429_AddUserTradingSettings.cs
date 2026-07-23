using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XPostMonitor.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddUserTradingSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UserTradingSettings",
                columns: table => new
                {
                    ChatId = table.Column<long>(type: "bigint", nullable: false),
                    WalletAddress = table.Column<string>(type: "nvarchar(42)", maxLength: 42, nullable: false),
                    EncryptedPrivateKey = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    EncryptedGmgnApiKey = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    BuyAmount = table.Column<decimal>(type: "decimal(18,8)", nullable: false),
                    SlippagePercent = table.Column<decimal>(type: "decimal(5,2)", nullable: false),
                    EnableTokenCreation = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserTradingSettings", x => x.ChatId);
                    table.ForeignKey(
                        name: "FK_UserTradingSettings_TelegramUsers_ChatId",
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
                name: "UserTradingSettings");
        }
    }
}
