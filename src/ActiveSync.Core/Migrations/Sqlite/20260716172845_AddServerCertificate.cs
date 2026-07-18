using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ActiveSync.Core.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddServerCertificate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ServerCertificates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    PfxProtected = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServerCertificates", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ServerCertificates");
        }
    }
}
