using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XPostMonitor.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLinkTokenSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LinkTokenSettings",
                columns: table => new
                {
                    ChatId = table.Column<long>(type: "bigint", nullable: false),
                    EnableAutoCreate = table.Column<bool>(type: "bit", nullable: false),
                    Chain = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Launchpad = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Anchor = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    CreatorTaxPercent = table.Column<int>(type: "int", nullable: false),
                    EnableAutoTrading = table.Column<bool>(type: "bit", nullable: false),
                    WorkerCount = table.Column<int>(type: "int", nullable: false),
                    BuyAmount = table.Column<decimal>(type: "decimal(18,8)", nullable: false),
                    SlippagePercent = table.Column<decimal>(type: "decimal(5,2)", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LinkTokenSettings", x => x.ChatId);
                    table.ForeignKey(
                        name: "FK_LinkTokenSettings_TelegramUsers_ChatId",
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
                name: "LinkTokenSettings");
        }
    }
}
