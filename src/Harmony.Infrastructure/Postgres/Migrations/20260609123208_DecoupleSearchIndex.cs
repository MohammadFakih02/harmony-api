using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harmony.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class DecoupleSearchIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Manually force PostgreSQL to drop the relational constraint
            migrationBuilder.DropForeignKey(
                name: "FK_MessagesSearch_Channels_channel_id",
                table: "MessagesSearch"
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Manually recreate the relationship if we ever rollback
            migrationBuilder.AddForeignKey(
                name: "FK_MessagesSearch_Channels_channel_id",
                table: "MessagesSearch",
                column: "channel_id",
                principalTable: "Channels",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade
            );
        }
    }
}
