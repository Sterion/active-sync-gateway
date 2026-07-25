using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ActiveSync.Core.Migrations.Npgsql
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
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DeviceKey = table.Column<int>(type: "integer", nullable: false),
                    CollectionId = table.Column<string>(type: "text", nullable: false),
                    SyncKeyAtClaim = table.Column<int>(type: "integer", nullable: false),
                    Key = table.Column<string>(type: "text", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
