using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XPostMonitor.Data.Migrations
{
    /// <inheritdoc />
    public partial class UseExactLinkTokenWorkerSlots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "WorkerSlots",
                table: "LinkTokenSettings",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "1");

            // Giữ nguyên ý nghĩa dữ liệu cũ trước khi xóa cột WorkerCount.
            migrationBuilder.Sql("""
                EXEC(N'UPDATE [LinkTokenSettings]
                SET [WorkerSlots] = CASE [WorkerCount]
                    WHEN 3 THEN N''1,2,3''
                    WHEN 2 THEN N''1,2''
                    ELSE N''1''
                END;');
                """);

            migrationBuilder.DropColumn(
                name: "WorkerCount",
                table: "LinkTokenSettings");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "WorkerCount",
                table: "LinkTokenSettings",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.Sql("""
                EXEC(N'UPDATE [LinkTokenSettings]
                SET [WorkerCount] = CASE
                    WHEN [WorkerSlots] LIKE N''%,%,%'' THEN 3
                    WHEN [WorkerSlots] LIKE N''%,%'' THEN 2
                    ELSE 1
                END;');
                """);

            migrationBuilder.DropColumn(
                name: "WorkerSlots",
                table: "LinkTokenSettings");
        }
    }
}
