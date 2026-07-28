using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XPostMonitor.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddParallelTradingWorkers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ParallelTokenCount",
                table: "WatchlistEntries",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<long>(
                name: "TradingWorkerId",
                table: "AutoTrades",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TradingWorkers",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ChatId = table.Column<long>(type: "bigint", nullable: false),
                    SlotNumber = table.Column<int>(type: "int", nullable: false),
                    EvmWalletAddress = table.Column<string>(type: "nvarchar(42)", maxLength: 42, nullable: false),
                    EncryptedEvmPrivateKey = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    EncryptedGmgnApiKey = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    EncryptedGmgnPrivateKey = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TradingWorkers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TradingWorkers_TelegramUsers_ChatId",
                        column: x => x.ChatId,
                        principalTable: "TelegramUsers",
                        principalColumn: "ChatId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.Sql("""
                INSERT INTO TradingWorkers
                    (ChatId, SlotNumber, EvmWalletAddress, EncryptedEvmPrivateKey,
                     EncryptedGmgnApiKey, EncryptedGmgnPrivateKey, IsEnabled, CreatedAtUtc, UpdatedAtUtc)
                SELECT u.ChatId, 1,
                       COALESCE(s.EvmWalletAddress, ''),
                       COALESCE(s.EncryptedEvmPrivateKey, ''),
                       COALESCE(s.EncryptedGmgnApiKey, ''),
                       COALESCE(s.EncryptedGmgnPrivateKey, ''),
                       1, SYSUTCDATETIME(), SYSUTCDATETIME()
                FROM TelegramUsers u
                LEFT JOIN UserTradingSettings s ON s.ChatId = u.ChatId;

                UPDATE a
                SET TradingWorkerId = w.Id
                FROM AutoTrades a
                INNER JOIN TradingWorkers w ON w.ChatId = a.ChatId AND w.SlotNumber = 1;
                """);

            migrationBuilder.AlterColumn<long>(
                name: "TradingWorkerId",
                table: "AutoTrades",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);

            migrationBuilder.DropColumn(
                name: "EncryptedEvmPrivateKey",
                table: "UserTradingSettings");

            migrationBuilder.DropColumn(
                name: "EncryptedGmgnApiKey",
                table: "UserTradingSettings");

            migrationBuilder.DropColumn(
                name: "EncryptedGmgnPrivateKey",
                table: "UserTradingSettings");

            migrationBuilder.DropColumn(
                name: "EvmWalletAddress",
                table: "UserTradingSettings");

            migrationBuilder.CreateIndex(
                name: "IX_AutoTrades_TradingWorkerId",
                table: "AutoTrades",
                column: "TradingWorkerId");

            migrationBuilder.CreateIndex(
                name: "IX_TradingWorkers_ChatId_SlotNumber",
                table: "TradingWorkers",
                columns: new[] { "ChatId", "SlotNumber" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_AutoTrades_TradingWorkers_TradingWorkerId",
                table: "AutoTrades",
                column: "TradingWorkerId",
                principalTable: "TradingWorkers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AutoTrades_TradingWorkers_TradingWorkerId",
                table: "AutoTrades");

            migrationBuilder.AddColumn<string>(
                name: "EncryptedEvmPrivateKey",
                table: "UserTradingSettings",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "EncryptedGmgnApiKey",
                table: "UserTradingSettings",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "EncryptedGmgnPrivateKey",
                table: "UserTradingSettings",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "EvmWalletAddress",
                table: "UserTradingSettings",
                type: "nvarchar(42)",
                maxLength: 42,
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql("""
                UPDATE s
                SET EvmWalletAddress = w.EvmWalletAddress,
                    EncryptedEvmPrivateKey = w.EncryptedEvmPrivateKey,
                    EncryptedGmgnApiKey = w.EncryptedGmgnApiKey,
                    EncryptedGmgnPrivateKey = w.EncryptedGmgnPrivateKey
                FROM UserTradingSettings s
                INNER JOIN TradingWorkers w ON w.ChatId = s.ChatId AND w.SlotNumber = 1;
                """);

            migrationBuilder.DropIndex(
                name: "IX_AutoTrades_TradingWorkerId",
                table: "AutoTrades");

            migrationBuilder.DropColumn(
                name: "ParallelTokenCount",
                table: "WatchlistEntries");

            migrationBuilder.DropColumn(
                name: "TradingWorkerId",
                table: "AutoTrades");

            migrationBuilder.DropTable(
                name: "TradingWorkers");
        }
    }
}
