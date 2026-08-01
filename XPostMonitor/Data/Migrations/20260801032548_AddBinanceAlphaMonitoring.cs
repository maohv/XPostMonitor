using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XPostMonitor.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBinanceAlphaMonitoring : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BinanceAlphaTokens",
                columns: table => new
                {
                    TokenId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ChainId = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ChainName = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ContractAddress = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Symbol = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    AlphaId = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    IconUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ListingTimeUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FirstSeenAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    NotifiedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BinanceAlphaTokens", x => x.TokenId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BinanceAlphaTokens_ChainId_ContractAddress",
                table: "BinanceAlphaTokens",
                columns: new[] { "ChainId", "ContractAddress" });

            migrationBuilder.CreateIndex(
                name: "IX_BinanceAlphaTokens_NotifiedAtUtc",
                table: "BinanceAlphaTokens",
                column: "NotifiedAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BinanceAlphaTokens");
        }
    }
}
