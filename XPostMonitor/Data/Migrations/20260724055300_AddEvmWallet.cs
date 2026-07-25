using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XPostMonitor.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEvmWallet : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EncryptedEvmPrivateKey",
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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EncryptedEvmPrivateKey",
                table: "UserTradingSettings");

            migrationBuilder.DropColumn(
                name: "EvmWalletAddress",
                table: "UserTradingSettings");
        }
    }
}
