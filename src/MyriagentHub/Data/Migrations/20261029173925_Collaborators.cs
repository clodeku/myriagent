using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyriagentHub.Data.Migrations
{
    /// <inheritdoc />
    public partial class Collaborators : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "user_id",
                table: "browser_sessions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "collaborator_shares",
                columns: table => new
                {
                    conversation_id = table.Column<string>(type: "TEXT", nullable: false),
                    user_id = table.Column<string>(type: "TEXT", nullable: false),
                    granted_at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_collaborator_shares", x => new { x.conversation_id, x.user_id });
                });

            migrationBuilder.CreateTable(
                name: "collaborators",
                columns: table => new
                {
                    user_id = table.Column<string>(type: "TEXT", nullable: false),
                    display_name = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    disabled_at = table.Column<long>(type: "INTEGER", nullable: true),
                    last_login_at = table.Column<long>(type: "INTEGER", nullable: true),
                    prefs_json = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_collaborators", x => x.user_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_browser_sessions_user_id",
                table: "browser_sessions",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_collaborator_shares_user_id",
                table: "collaborator_shares",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "collaborator_shares");

            migrationBuilder.DropTable(
                name: "collaborators");

            migrationBuilder.DropIndex(
                name: "ix_browser_sessions_user_id",
                table: "browser_sessions");

            migrationBuilder.DropColumn(
                name: "user_id",
                table: "browser_sessions");
        }
    }
}
