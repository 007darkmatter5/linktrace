using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Linktrace.Data.Migrations
{
    /// <inheritdoc />
    public partial class SavedPortMaps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PortMapViews",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    DeviceIds = table.Column<string>(type: "TEXT", nullable: false),
                    FollowPassThrough = table.Column<bool>(type: "INTEGER", nullable: false),
                    ShowExternal = table.Column<bool>(type: "INTEGER", nullable: false),
                    Version = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PortMapViews", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PortMapViews_Name",
                table: "PortMapViews",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PortMapViews");
        }
    }
}
