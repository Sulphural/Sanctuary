using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

using Sanctuary.Database;

#nullable disable

namespace Sanctuary.Database.MySql.Migrations;

// Adds CharacterQuests.CompletedUtc, the completion stamp a DAILY quest is judged by (null for every
// other quest). Attributes are declared here so Database.Migrate() discovers and applies it.
[DbContext(typeof(DatabaseContext))]
[Migration("20260812000000_AddQuestCompletedUtc")]
public partial class AddQuestCompletedUtc : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<System.DateTimeOffset>(
            name: "CompletedUtc",
            table: "CharacterQuests",
            type: "datetime(6)",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "CompletedUtc",
            table: "CharacterQuests");
    }
}
