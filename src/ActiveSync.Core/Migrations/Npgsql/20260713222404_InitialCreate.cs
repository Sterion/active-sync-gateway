#nullable disable

using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

namespace ActiveSync.Core.Migrations.Npgsql;

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
				Id = table.Column<int>("integer", nullable: false)
					.Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
				UserName = table.Column<string>("text", nullable: false),
				DeviceId = table.Column<string>("text", nullable: false),
				DeviceType = table.Column<string>("text", nullable: false),
				PolicyKey = table.Column<long>("bigint", nullable: false),
				FolderSyncKey = table.Column<int>("integer", nullable: false),
				DeviceInfoJson = table.Column<string>("text", nullable: true),
				PingParamsJson = table.Column<string>("text", nullable: true),
				LastSyncRequestJson = table.Column<string>("text", nullable: true),
				CreatedUtc = table.Column<DateTime>("timestamp with time zone", nullable: false),
				LastSeenUtc = table.Column<DateTime>("timestamp with time zone", nullable: false)
			},
			constraints: table => { table.PrimaryKey("PK_Devices", x => x.Id); });

		migrationBuilder.CreateTable(
			"UserFolders",
			table => new
			{
				Id = table.Column<int>("integer", nullable: false)
					.Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
				UserName = table.Column<string>("text", nullable: false),
				BackendKey = table.Column<string>("text", nullable: false),
				DisplayName = table.Column<string>("text", nullable: false),
				ParentBackendKey = table.Column<string>("text", nullable: true),
				Type = table.Column<int>("integer", nullable: false),
				EasClass = table.Column<string>("text", nullable: false),
				Deleted = table.Column<bool>("boolean", nullable: false)
			},
			constraints: table => { table.PrimaryKey("PK_UserFolders", x => x.Id); });

		migrationBuilder.CreateTable(
			"CollectionStates",
			table => new
			{
				Id = table.Column<int>("integer", nullable: false)
					.Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
				DeviceKey = table.Column<int>("integer", nullable: false),
				CollectionId = table.Column<string>("text", nullable: false),
				SyncKey = table.Column<int>("integer", nullable: false),
				SnapshotJson = table.Column<string>("text", nullable: false),
				PreviousSnapshotJson = table.Column<string>("text", nullable: true),
				FilterType = table.Column<int>("integer", nullable: false),
				OptionsJson = table.Column<string>("text", nullable: true),
				UpdatedUtc = table.Column<DateTime>("timestamp with time zone", nullable: false)
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
				Id = table.Column<int>("integer", nullable: false)
					.Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
				DeviceKey = table.Column<int>("integer", nullable: false),
				ServerId = table.Column<string>("text", nullable: false),
				DisplayName = table.Column<string>("text", nullable: false),
				ParentServerId = table.Column<string>("text", nullable: true),
				Type = table.Column<int>("integer", nullable: false)
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
				Id = table.Column<int>("integer", nullable: false)
					.Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
				UserFolderKey = table.Column<int>("integer", nullable: false),
				Href = table.Column<string>("text", nullable: false)
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
