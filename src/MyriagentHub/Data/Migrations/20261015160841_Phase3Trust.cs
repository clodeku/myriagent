using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyriagentHub.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase3Trust : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "token_issued_at",
                table: "devices",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "owner_key_log",
                columns: table => new
                {
                    rev = table.Column<long>(type: "INTEGER", nullable: false),
                    action = table.Column<string>(type: "TEXT", nullable: false),
                    credential_id = table.Column<byte[]>(type: "BLOB", nullable: false),
                    cose_public_key = table.Column<byte[]>(type: "BLOB", nullable: true),
                    alg = table.Column<int>(type: "INTEGER", nullable: true),
                    name = table.Column<string>(type: "TEXT", nullable: true),
                    statement = table.Column<string>(type: "TEXT", nullable: false),
                    signer_credential_id = table.Column<byte[]>(type: "BLOB", nullable: true),
                    assertion = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_owner_key_log", x => x.rev);
                });

            migrationBuilder.CreateTable(
                name: "pairing_requests",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    device_code_hash = table.Column<byte[]>(type: "BLOB", nullable: false),
                    user_code = table.Column<string>(type: "TEXT", nullable: false),
                    device_name = table.Column<string>(type: "TEXT", nullable: false),
                    machine_name = table.Column<string>(type: "TEXT", nullable: true),
                    windows_user = table.Column<string>(type: "TEXT", nullable: true),
                    device_pubkey = table.Column<byte[]>(type: "BLOB", nullable: false),
                    key_storage = table.Column<string>(type: "TEXT", nullable: false),
                    remote_ip = table.Column<string>(type: "TEXT", nullable: true),
                    user_agent = table.Column<string>(type: "TEXT", nullable: true),
                    app_version = table.Column<string>(type: "TEXT", nullable: true),
                    protocol_version = table.Column<int>(type: "INTEGER", nullable: true),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    expires_at = table.Column<long>(type: "INTEGER", nullable: false),
                    interval_seconds = table.Column<int>(type: "INTEGER", nullable: false),
                    last_poll_at = table.Column<long>(type: "INTEGER", nullable: true),
                    device_id = table.Column<string>(type: "TEXT", nullable: true),
                    approved_at = table.Column<long>(type: "INTEGER", nullable: true),
                    approved_session = table.Column<string>(type: "TEXT", nullable: true),
                    denied_at = table.Column<long>(type: "INTEGER", nullable: true),
                    consumed_at = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pairing_requests", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_pairing_requests_device_code_hash",
                table: "pairing_requests",
                column: "device_code_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_pairing_requests_user_code",
                table: "pairing_requests",
                column: "user_code");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "owner_key_log");

            migrationBuilder.DropTable(
                name: "pairing_requests");

            migrationBuilder.DropColumn(
                name: "token_issued_at",
                table: "devices");
        }
    }
}
