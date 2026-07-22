using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ActiveSync.Core.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class CompressCollectionSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PreviousSnapshotJson",
                table: "CollectionStates");

            migrationBuilder.DropColumn(
                name: "SnapshotJson",
                table: "CollectionStates");

            migrationBuilder.AddColumn<byte[]>(
                name: "PreviousSnapshotCompressed",
                table: "CollectionStates",
                type: "BLOB",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "SnapshotCompressed",
                table: "CollectionStates",
                type: "BLOB",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PreviousSnapshotCompressed",
                table: "CollectionStates");

            migrationBuilder.DropColumn(
                name: "SnapshotCompressed",
                table: "CollectionStates");

            migrationBuilder.AddColumn<string>(
                name: "PreviousSnapshotJson",
                table: "CollectionStates",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SnapshotJson",
                table: "CollectionStates",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }
    }
}
