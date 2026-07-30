using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XPostMonitor.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddArcBridgeFreeWallets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ArcBridgeWallets",
                columns: table => new
                {
                    ChatId = table.Column<long>(type: "bigint", nullable: false),
                    WalletAddress = table.Column<string>(type: "nvarchar(42)", maxLength: 42, nullable: false),
                    EncryptedPrivateKey = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArcBridgeWallets", x => x.ChatId);
                    table.ForeignKey(
                        name: "FK_ArcBridgeWallets_TelegramUsers_ChatId",
                        column: x => x.ChatId,
                        principalTable: "TelegramUsers",
                        principalColumn: "ChatId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ArcBridgeTransfers",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ChatId = table.Column<long>(type: "bigint", nullable: false),
                    WalletAddress = table.Column<string>(type: "nvarchar(42)", maxLength: 42, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    GrossAmountAtomic = table.Column<string>(type: "nvarchar(78)", maxLength: 78, nullable: false),
                    ReceiveAmountAtomic = table.Column<string>(type: "nvarchar(78)", maxLength: 78, nullable: false),
                    MaxFeeAtomic = table.Column<string>(type: "nvarchar(78)", maxLength: 78, nullable: false),
                    BurnIntentJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DepositTransactionHash = table.Column<string>(type: "nvarchar(66)", maxLength: 66, nullable: true),
                    DepositBlockNumber = table.Column<string>(type: "nvarchar(78)", maxLength: 78, nullable: true),
                    MintTransactionHash = table.Column<string>(type: "nvarchar(66)", maxLength: 66, nullable: true),
                    ErrorMessage = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArcBridgeTransfers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ArcBridgeTransfers_ArcBridgeWallets_ChatId",
                        column: x => x.ChatId,
                        principalTable: "ArcBridgeWallets",
                        principalColumn: "ChatId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ArcBridgeTransfers_ChatId_Status",
                table: "ArcBridgeTransfers",
                columns: new[] { "ChatId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ArcBridgeTransfers");

            migrationBuilder.DropTable(
                name: "ArcBridgeWallets");
        }
    }
}
