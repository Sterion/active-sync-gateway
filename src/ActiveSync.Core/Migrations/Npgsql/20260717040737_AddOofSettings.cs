using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ActiveSync.Core.Migrations.Npgsql
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
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserName = table.Column<string>(type: "text", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    StartUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EndUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Message = table.Column<string>(type: "text", nullable: false),
                    BodyType = table.Column<string>(type: "text", nullable: false),
                    PreviousActiveScript = table.Column<string>(type: "text", nullable: true),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
