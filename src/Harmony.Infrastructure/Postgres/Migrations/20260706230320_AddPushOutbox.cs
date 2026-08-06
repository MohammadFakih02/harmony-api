using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harmony.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddPushOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PushOutbox",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    recipient_id = table.Column<long>(type: "bigint", nullable: false),
                    actor_id = table.Column<long>(type: "bigint", nullable: true),
                    guild_id = table.Column<long>(type: "bigint", nullable: true),
                    channel_id = table.Column<long>(type: "bigint", nullable: true),
                    message_id = table.Column<long>(type: "bigint", nullable: true),
                    exclude_user_ids = table.Column<string>(type: "text", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PushOutbox", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PushOutbox_next_attempt_at",
                table: "PushOutbox",
                column: "next_attempt_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PushOutbox");
        }
    }
}
