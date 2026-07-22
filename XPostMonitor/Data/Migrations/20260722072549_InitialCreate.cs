using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XPostMonitor.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TelegramUsers",
                columns: table => new
                {
                    ChatId = table.Column<long>(type: "bigint", nullable: false),
                    TelegramUserId = table.Column<long>(type: "bigint", nullable: false),
                    Username = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    DisplayName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastSeenAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TelegramUsers", x => x.ChatId);
                });

            migrationBuilder.CreateTable(
                name: "XAccounts",
                columns: table => new
                {
                    XUserId = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Username = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_XAccounts", x => x.XUserId);
                });

            migrationBuilder.CreateTable(
                name: "WatchlistEntries",
                columns: table => new
                {
                    ChatId = table.Column<long>(type: "bigint", nullable: false),
                    XUserId = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WatchlistEntries", x => new { x.ChatId, x.XUserId });
                    table.ForeignKey(
                        name: "FK_WatchlistEntries_TelegramUsers_ChatId",
                        column: x => x.ChatId,
                        principalTable: "TelegramUsers",
                        principalColumn: "ChatId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WatchlistEntries_XAccounts_XUserId",
                        column: x => x.XUserId,
                        principalTable: "XAccounts",
                        principalColumn: "XUserId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "XSubscriptions",
                columns: table => new
                {
                    XUserId = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    RemoteSubscriptionId = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_XSubscriptions", x => new { x.XUserId, x.EventType });
                    table.ForeignKey(
                        name: "FK_XSubscriptions_XAccounts_XUserId",
                        column: x => x.XUserId,
                        principalTable: "XAccounts",
                        principalColumn: "XUserId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WatchlistEntries_XUserId",
                table: "WatchlistEntries",
                column: "XUserId");

            migrationBuilder.CreateIndex(
                name: "IX_XAccounts_Username",
                table: "XAccounts",
                column: "Username",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WatchlistEntries");

            migrationBuilder.DropTable(
                name: "XSubscriptions");

            migrationBuilder.DropTable(
                name: "TelegramUsers");

            migrationBuilder.DropTable(
                name: "XAccounts");
        }
    }
}
