using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyriagentHub.Data.Migrations
{
    /// <inheritdoc />
    public partial class AppReleases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "app_releases",
                columns: table => new
                {
                    release = table.Column<string>(type: "TEXT", nullable: false),
                    version = table.Column<string>(type: "TEXT", nullable: false),
                    build = table.Column<string>(type: "TEXT", nullable: false),
                    commit = table.Column<string>(type: "TEXT", nullable: false),
                    manifest_sha256 = table.Column<string>(type: "TEXT", nullable: false),
                    zip_sha256 = table.Column<string>(type: "TEXT", nullable: false),
                    zip_bytes = table.Column<long>(type: "INTEGER", nullable: false),
                    protocol_version = table.Column<int>(type: "INTEGER", nullable: false),
                    min_runtime = table.Column<string>(type: "TEXT", nullable: false),
                    kid = table.Column<string>(type: "TEXT", nullable: false),
                    state = table.Column<string>(type: "TEXT", nullable: false),
                    ingested_at = table.Column<long>(type: "INTEGER", nullable: false),
                    withdrawn_at = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_app_releases", x => x.release);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "app_releases");
        }
    }
}
