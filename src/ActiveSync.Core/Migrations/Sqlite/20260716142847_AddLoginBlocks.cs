using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ActiveSync.Core.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddLoginBlocks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LoginBlocks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserName = table.Column<string>(type: "TEXT", nullable: false),
                    DeviceId = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoginBlocks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LoginBlocks_UserName_DeviceId",
                table: "LoginBlocks",
                columns: new[] { "UserName", "DeviceId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LoginBlocks");
        }
    }
}
