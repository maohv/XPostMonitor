using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XPostMonitor.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAutoTradingTakeProfits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "EnableAutoTrading",
                table: "WatchlistEntries",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "AutoTrades",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ChatId = table.Column<long>(type: "bigint", nullable: false),
                    PostId = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Chain = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    TokenAddress = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    TokenName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    TokenSymbol = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    WalletAddress = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    QuoteTokenAddress = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    EntryPrice = table.Column<decimal>(type: "decimal(38,30)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ErrorMessage = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutoTrades", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AutoTrades_TelegramUsers_ChatId",
                        column: x => x.ChatId,
                        principalTable: "TelegramUsers",
                        principalColumn: "ChatId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TakeProfitSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ChatId = table.Column<long>(type: "bigint", nullable: false),
                    ProfitPercent = table.Column<decimal>(type: "decimal(10,2)", nullable: false),
                    SellPercent = table.Column<decimal>(type: "decimal(5,2)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TakeProfitSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TakeProfitSettings_TelegramUsers_ChatId",
                        column: x => x.ChatId,
                        principalTable: "TelegramUsers",
                        principalColumn: "ChatId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AutoTradeOrders",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AutoTradeId = table.Column<long>(type: "bigint", nullable: false),
                    GmgnOrderId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ProfitPercent = table.Column<decimal>(type: "decimal(10,2)", nullable: false),
                    SellPercent = table.Column<decimal>(type: "decimal(5,2)", nullable: false),
                    TargetPrice = table.Column<decimal>(type: "decimal(38,30)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    TransactionHash = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    RealizedProfitUsd = table.Column<decimal>(type: "decimal(18,8)", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ClosedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    NotifiedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutoTradeOrders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AutoTradeOrders_AutoTrades_AutoTradeId",
                        column: x => x.AutoTradeId,
                        principalTable: "AutoTrades",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AutoTradeOrders_AutoTradeId",
                table: "AutoTradeOrders",
                column: "AutoTradeId");

            migrationBuilder.CreateIndex(
                name: "IX_AutoTradeOrders_GmgnOrderId",
                table: "AutoTradeOrders",
                column: "GmgnOrderId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AutoTrades_ChatId",
                table: "AutoTrades",
                column: "ChatId");

            migrationBuilder.CreateIndex(
                name: "IX_AutoTrades_Status",
                table: "AutoTrades",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_TakeProfitSettings_ChatId_ProfitPercent",
                table: "TakeProfitSettings",
                columns: new[] { "ChatId", "ProfitPercent" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AutoTradeOrders");

            migrationBuilder.DropTable(
                name: "TakeProfitSettings");

            migrationBuilder.DropTable(
                name: "AutoTrades");

            migrationBuilder.DropColumn(
                name: "EnableAutoTrading",
                table: "WatchlistEntries");
        }
    }
}
