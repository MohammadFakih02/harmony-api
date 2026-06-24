using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harmony.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class ManageInvitesAndDropGuildInviteCode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_GuildInvites_Channels_channel_id",
                table: "GuildInvites");

            migrationBuilder.DropIndex(
                name: "IX_Guilds_invite_code",
                table: "Guilds");

            migrationBuilder.DropColumn(
                name: "invite_code",
                table: "Guilds");

            migrationBuilder.AlterColumn<long>(
                name: "channel_id",
                table: "GuildInvites",
                type: "bigint",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AddForeignKey(
                name: "FK_GuildInvites_Channels_channel_id",
                table: "GuildInvites",
                column: "channel_id",
                principalTable: "Channels",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_GuildInvites_Channels_channel_id",
                table: "GuildInvites");

            migrationBuilder.AddColumn<string>(
                name: "invite_code",
                table: "Guilds",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AlterColumn<long>(
                name: "channel_id",
                table: "GuildInvites",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Guilds_invite_code",
                table: "Guilds",
                column: "invite_code",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_GuildInvites_Channels_channel_id",
                table: "GuildInvites",
                column: "channel_id",
                principalTable: "Channels",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
