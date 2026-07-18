using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ActiveSync.Core.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddOofSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OofSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserName = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    StartUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    EndUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Message = table.Column<string>(type: "TEXT", nullable: false),
                    BodyType = table.Column<string>(type: "TEXT", nullable: false),
                    PreviousActiveScript = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OofSettings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OofSettings_UserName",
                table: "OofSettings",
                column: "UserName",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OofSettings");
        }
    }
}
