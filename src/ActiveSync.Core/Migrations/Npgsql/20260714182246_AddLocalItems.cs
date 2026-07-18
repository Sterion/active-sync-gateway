#nullable disable

using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

namespace ActiveSync.Core.Migrations.Npgsql;

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
				Id = table.Column<int>("integer", nullable: false)
					.Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
				UserName = table.Column<string>("text", nullable: false),
				Collection = table.Column<string>("text", nullable: false),
				Uid = table.Column<string>("text", nullable: false),
				Content = table.Column<string>("text", nullable: false),
				Version = table.Column<int>("integer", nullable: false),
				ItemDateUtc = table.Column<DateTime>("timestamp with time zone", nullable: true),
				LastModifiedUtc = table.Column<DateTime>("timestamp with time zone", nullable: false)
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
