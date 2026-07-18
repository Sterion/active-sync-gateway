using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ActiveSync.Core.Migrations.Npgsql
{
    /// <inheritdoc />
    public partial class AddAccountWipe : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LastProtocolVersion",
                table: "Devices",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PendingAccountWipe",
                table: "Devices",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastProtocolVersion",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "PendingAccountWipe",
                table: "Devices");
        }
    }
}
