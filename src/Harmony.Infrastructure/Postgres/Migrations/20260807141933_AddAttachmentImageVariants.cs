using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harmony.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddAttachmentImageVariants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "blur_hash",
                table: "FileAttachments",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "variant_widths",
                table: "FileAttachments",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "blur_hash",
                table: "FileAttachments");

            migrationBuilder.DropColumn(
                name: "variant_widths",
                table: "FileAttachments");
        }
    }
}
