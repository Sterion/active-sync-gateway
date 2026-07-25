using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ActiveSync.Core.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddSentCommandToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SentCommandTokens",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DeviceKey = table.Column<int>(type: "INTEGER", nullable: false),
                    CollectionId = table.Column<string>(type: "TEXT", nullable: false),
                    SyncKeyAtClaim = table.Column<int>(type: "INTEGER", nullable: false),
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SentCommandTokens", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SentCommandTokens_DeviceKey_CollectionId_SyncKeyAtClaim_Key",
                table: "SentCommandTokens",
                columns: new[] { "DeviceKey", "CollectionId", "SyncKeyAtClaim", "Key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SentCommandTokens");
        }
    }
}
