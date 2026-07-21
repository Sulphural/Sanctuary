using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

using Sanctuary.Database;

#nullable disable

namespace Sanctuary.Database.MySql.Migrations
{
    // Attributes are declared here (rather than a .Designer.cs) so Database.Migrate() discovers and
    // applies this migration; without them EF skips it and the CharacterQuests table is never created.
    [DbContext(typeof(DatabaseContext))]
    [Migration("20260703000000_AddCharacterQuests")]
    public partial class AddCharacterQuests : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CharacterQuests",
                columns: table => new
                {
                    QuestId = table.Column<int>(type: "int", nullable: false),
                    CharacterGuid = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    Completed = table.Column<bool>(type: "tinyint(1)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CharacterQuests", x => new { x.QuestId, x.CharacterGuid });
                    table.ForeignKey(
                        name: "FK_CharacterQuests_Characters_CharacterGuid",
                        column: x => x.CharacterGuid,
                        principalTable: "Characters",
                        principalColumn: "Guid",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_CharacterQuests_CharacterGuid",
                table: "CharacterQuests",
                column: "CharacterGuid");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CharacterQuests");
        }
    }
}
