using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Linktrace.Data.Migrations
{
    /// <inheritdoc />
    public partial class FaceplateTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FaceplateTemplateId",
                table: "Devices",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "FaceplateTemplates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Model = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Columns = table.Column<int>(type: "INTEGER", nullable: false),
                    Rows = table.Column<int>(type: "INTEGER", nullable: false),
                    Version = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Labels = table.Column<string>(type: "TEXT", nullable: true),
                    Placements = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FaceplateTemplates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Devices_FaceplateTemplateId",
                table: "Devices",
                column: "FaceplateTemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_FaceplateTemplates_Model",
                table: "FaceplateTemplates",
                column: "Model");

            migrationBuilder.CreateIndex(
                name: "IX_FaceplateTemplates_Name",
                table: "FaceplateTemplates",
                column: "Name",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Devices_FaceplateTemplates_FaceplateTemplateId",
                table: "Devices",
                column: "FaceplateTemplateId",
                principalTable: "FaceplateTemplates",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Devices_FaceplateTemplates_FaceplateTemplateId",
                table: "Devices");

            migrationBuilder.DropTable(
                name: "FaceplateTemplates");

            migrationBuilder.DropIndex(
                name: "IX_Devices_FaceplateTemplateId",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "FaceplateTemplateId",
                table: "Devices");
        }
    }
}
