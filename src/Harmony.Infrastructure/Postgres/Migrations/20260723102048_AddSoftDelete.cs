using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harmony.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddSoftDelete : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "deleted_at",
                table: "Guilds",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "deleted_at",
                table: "Channels",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Guilds_deleted_at",
                table: "Guilds",
                column: "deleted_at");

            migrationBuilder.CreateIndex(
                name: "IX_Channels_deleted_at",
                table: "Channels",
                column: "deleted_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Guilds_deleted_at",
                table: "Guilds");

            migrationBuilder.DropIndex(
                name: "IX_Channels_deleted_at",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "deleted_at",
                table: "Guilds");

            migrationBuilder.DropColumn(
                name: "deleted_at",
                table: "Channels");
        }
    }
}
