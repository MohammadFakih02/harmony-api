using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harmony.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class DropDiscriminator : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Discriminator",
                table: "Users");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Discriminator",
                table: "Users",
                type: "character varying(4)",
                maxLength: 4,
                nullable: true);
        }
    }
}
