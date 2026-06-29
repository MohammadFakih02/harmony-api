using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harmony.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddGuildWelcomeAndNotificationSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "system_messages_enabled",
                table: "Guilds",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<long>(
                name: "welcome_channel_id",
                table: "Guilds",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "welcome_message",
                table: "Guilds",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "NotificationSettings",
                columns: table => new
                {
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    scope_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    scope_id = table.Column<long>(type: "bigint", nullable: false),
                    level = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationSettings", x => new { x.user_id, x.scope_type, x.scope_id });
                    table.ForeignKey(
                        name: "FK_NotificationSettings_Users_user_id",
                        column: x => x.user_id,
                        principalTable: "Users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NotificationSettings");

            migrationBuilder.DropColumn(
                name: "system_messages_enabled",
                table: "Guilds");

            migrationBuilder.DropColumn(
                name: "welcome_channel_id",
                table: "Guilds");

            migrationBuilder.DropColumn(
                name: "welcome_message",
                table: "Guilds");
        }
    }
}
