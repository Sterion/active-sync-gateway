#nullable disable

using Microsoft.EntityFrameworkCore.Migrations;

namespace ActiveSync.Core.Migrations.Sqlite;

/// <inheritdoc />
public partial class InitialCreate : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.CreateTable(
			"Devices",
			table => new
			{
				Id = table.Column<int>("INTEGER", nullable: false)
					.Annotation("Sqlite:Autoincrement", true),
				UserName = table.Column<string>("TEXT", nullable: false),
				DeviceId = table.Column<string>("TEXT", nullable: false),
				DeviceType = table.Column<string>("TEXT", nullable: false),
				PolicyKey = table.Column<uint>("INTEGER", nullable: false),
				FolderSyncKey = table.Column<int>("INTEGER", nullable: false),
				DeviceInfoJson = table.Column<string>("TEXT", nullable: true),
				PingParamsJson = table.Column<string>("TEXT", nullable: true),
				LastSyncRequestJson = table.Column<string>("TEXT", nullable: true),
				CreatedUtc = table.Column<DateTime>("TEXT", nullable: false),
				LastSeenUtc = table.Column<DateTime>("TEXT", nullable: false)
			},
			constraints: table => { table.PrimaryKey("PK_Devices", x => x.Id); });

		migrationBuilder.CreateTable(
			"UserFolders",
			table => new
			{
				Id = table.Column<int>("INTEGER", nullable: false)
					.Annotation("Sqlite:Autoincrement", true),
				UserName = table.Column<string>("TEXT", nullable: false),
				BackendKey = table.Column<string>("TEXT", nullable: false),
				DisplayName = table.Column<string>("TEXT", nullable: false),
				ParentBackendKey = table.Column<string>("TEXT", nullable: true),
				Type = table.Column<int>("INTEGER", nullable: false),
				EasClass = table.Column<string>("TEXT", nullable: false),
				Deleted = table.Column<bool>("INTEGER", nullable: false)
			},
			constraints: table => { table.PrimaryKey("PK_UserFolders", x => x.Id); });

		migrationBuilder.CreateTable(
			"CollectionStates",
			table => new
			{
				Id = table.Column<int>("INTEGER", nullable: false)
					.Annotation("Sqlite:Autoincrement", true),
				DeviceKey = table.Column<int>("INTEGER", nullable: false),
				CollectionId = table.Column<string>("TEXT", nullable: false),
				SyncKey = table.Column<int>("INTEGER", nullable: false),
				SnapshotJson = table.Column<string>("TEXT", nullable: false),
				PreviousSnapshotJson = table.Column<string>("TEXT", nullable: true),
				FilterType = table.Column<int>("INTEGER", nullable: false),
				OptionsJson = table.Column<string>("TEXT", nullable: true),
				UpdatedUtc = table.Column<DateTime>("TEXT", nullable: false)
			},
			constraints: table =>
			{
				table.PrimaryKey("PK_CollectionStates", x => x.Id);
				table.ForeignKey(
					"FK_CollectionStates_Devices_DeviceKey",
					x => x.DeviceKey,
					"Devices",
					"Id",
					onDelete: ReferentialAction.Cascade);
			});

		migrationBuilder.CreateTable(
			"DeviceFolders",
			table => new
			{
				Id = table.Column<int>("INTEGER", nullable: false)
					.Annotation("Sqlite:Autoincrement", true),
				DeviceKey = table.Column<int>("INTEGER", nullable: false),
				ServerId = table.Column<string>("TEXT", nullable: false),
				DisplayName = table.Column<string>("TEXT", nullable: false),
				ParentServerId = table.Column<string>("TEXT", nullable: true),
				Type = table.Column<int>("INTEGER", nullable: false)
			},
			constraints: table =>
			{
				table.PrimaryKey("PK_DeviceFolders", x => x.Id);
				table.ForeignKey(
					"FK_DeviceFolders_Devices_DeviceKey",
					x => x.DeviceKey,
					"Devices",
					"Id",
					onDelete: ReferentialAction.Cascade);
			});

		migrationBuilder.CreateTable(
			"DavItems",
			table => new
			{
				Id = table.Column<int>("INTEGER", nullable: false)
					.Annotation("Sqlite:Autoincrement", true),
				UserFolderKey = table.Column<int>("INTEGER", nullable: false),
				Href = table.Column<string>("TEXT", nullable: false)
			},
			constraints: table =>
			{
				table.PrimaryKey("PK_DavItems", x => x.Id);
				table.ForeignKey(
					"FK_DavItems_UserFolders_UserFolderKey",
					x => x.UserFolderKey,
					"UserFolders",
					"Id",
					onDelete: ReferentialAction.Cascade);
			});

		migrationBuilder.CreateIndex(
			"IX_CollectionStates_DeviceKey_CollectionId",
			"CollectionStates",
			new[] { "DeviceKey", "CollectionId" },
			unique: true);

		migrationBuilder.CreateIndex(
			"IX_DavItems_UserFolderKey_Href",
			"DavItems",
			new[] { "UserFolderKey", "Href" },
			unique: true);

		migrationBuilder.CreateIndex(
			"IX_DeviceFolders_DeviceKey_ServerId",
			"DeviceFolders",
			new[] { "DeviceKey", "ServerId" },
			unique: true);

		migrationBuilder.CreateIndex(
			"IX_Devices_UserName_DeviceId",
			"Devices",
			new[] { "UserName", "DeviceId" },
			unique: true);

		migrationBuilder.CreateIndex(
			"IX_UserFolders_UserName_BackendKey",
			"UserFolders",
			new[] { "UserName", "BackendKey" },
			unique: true);
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.DropTable(
			"CollectionStates");

		migrationBuilder.DropTable(
			"DavItems");

		migrationBuilder.DropTable(
			"DeviceFolders");

		migrationBuilder.DropTable(
			"UserFolders");

		migrationBuilder.DropTable(
			"Devices");
	}
}
