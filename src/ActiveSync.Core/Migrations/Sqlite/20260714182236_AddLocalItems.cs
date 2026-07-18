#nullable disable

using Microsoft.EntityFrameworkCore.Migrations;

namespace ActiveSync.Core.Migrations.Sqlite;

/// <inheritdoc />
public partial class AddLocalItems : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.CreateTable(
			"LocalItems",
			table => new
			{
				Id = table.Column<int>("INTEGER", nullable: false)
					.Annotation("Sqlite:Autoincrement", true),
				UserName = table.Column<string>("TEXT", nullable: false),
				Collection = table.Column<string>("TEXT", nullable: false),
				Uid = table.Column<string>("TEXT", nullable: false),
				Content = table.Column<string>("TEXT", nullable: false),
				Version = table.Column<int>("INTEGER", nullable: false),
				ItemDateUtc = table.Column<DateTime>("TEXT", nullable: true),
				LastModifiedUtc = table.Column<DateTime>("TEXT", nullable: false)
			},
			constraints: table => { table.PrimaryKey("PK_LocalItems", x => x.Id); });

		migrationBuilder.CreateIndex(
			"IX_LocalItems_UserName_Collection",
			"LocalItems",
			new[] { "UserName", "Collection" });
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.DropTable(
			"LocalItems");
	}
}
