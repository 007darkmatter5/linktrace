using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Linktrace.Data.Migrations
{
    /// <inheritdoc />
    public partial class DeviceTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DeviceTemplates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    Manufacturer = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Model = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    HeightU = table.Column<int>(type: "INTEGER", nullable: false),
                    FaceplateRows = table.Column<int>(type: "INTEGER", nullable: false),
                    FaceplateNumbering = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    FaceplateTemplateId = table.Column<int>(type: "INTEGER", nullable: true),
                    Version = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Ports = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceTemplates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeviceTemplates_FaceplateTemplates_FaceplateTemplateId",
                        column: x => x.FaceplateTemplateId,
                        principalTable: "FaceplateTemplates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceTemplates_FaceplateTemplateId",
                table: "DeviceTemplates",
                column: "FaceplateTemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceTemplates_Name",
                table: "DeviceTemplates",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeviceTemplates");
        }
    }
}
