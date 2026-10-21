using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Linktrace.Data.Migrations
{
    /// <inheritdoc />
    public partial class FaceplateLayout : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FaceplateNumbering",
                table: "Devices",
                type: "TEXT",
                maxLength: 30,
                nullable: false,
                defaultValue: "Auto"); // existing devices use automatic numbering

            migrationBuilder.AddColumn<int>(
                name: "FaceplateRows",
                table: "Devices",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FaceplateNumbering",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "FaceplateRows",
                table: "Devices");
        }
    }
}
