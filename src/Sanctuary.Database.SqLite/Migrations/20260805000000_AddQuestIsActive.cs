using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

using Sanctuary.Database;

#nullable disable

namespace Sanctuary.Database.Sqlite.Migrations;

// Adds CharacterQuests.IsActive - which quest the character has TRACKED (the arrow/breadcrumb and
// "Take Me There" target). At most one row per character is true. Defaults to false, so existing
// characters simply start untracked until they pick a quest in the journal.
[DbContext(typeof(DatabaseContext))]
[Migration("20260805000000_AddQuestIsActive")]
public partial class AddQuestIsActive : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "IsActive",
            table: "CharacterQuests",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "IsActive",
            table: "CharacterQuests");
    }
}
