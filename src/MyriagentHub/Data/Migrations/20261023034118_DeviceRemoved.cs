using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyriagentHub.Data.Migrations
{
    /// <inheritdoc />
    public partial class DeviceRemoved : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "removed_at",
                table: "devices",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "removed_at",
                table: "devices");
        }
    }
}
