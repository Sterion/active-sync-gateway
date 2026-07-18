using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ActiveSync.Core.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddDevicePolicyColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PolicyDocHash",
                table: "Devices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RecoveryPasswordProtected",
                table: "Devices",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PolicyDocHash",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "RecoveryPasswordProtected",
                table: "Devices");
        }
    }
}
