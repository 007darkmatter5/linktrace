using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Linktrace.Data.Migrations
{
    /// <inheritdoc />
    public partial class UserThemePreference : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ThemePreference",
                table: "AspNetUsers",
                type: "TEXT",
                maxLength: 10,
                nullable: false,
                defaultValue: "System"); // existing accounts follow the OS until they choose
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ThemePreference",
                table: "AspNetUsers");
        }
    }
}
