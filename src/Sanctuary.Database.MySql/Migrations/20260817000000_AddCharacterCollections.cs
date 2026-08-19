using System;

using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

using Sanctuary.Database;

#nullable disable

namespace Sanctuary.Database.MySql.Migrations;

// Which collections a character has already completed, so the completion reward (Adventurer XP, coins,
// items) pays out once instead of on every relog.
[DbContext(typeof(DatabaseContext))]
[Migration("20260817000000_AddCharacterCollections")]
public partial class AddCharacterCollections : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "CharacterCollections",
            columns: table => new
            {
                CollectionId = table.Column<int>(type: "int", nullable: false),
                CharacterId = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                CompletedUtc = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CharacterCollections", x => new { x.CollectionId, x.CharacterId });
                table.ForeignKey(
                    name: "FK_CharacterCollections_Characters_CharacterId",
                    column: x => x.CharacterId,
                    principalTable: "Characters",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_CharacterCollections_CharacterId",
            table: "CharacterCollections",
            column: "CharacterId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "CharacterCollections");
    }
}
