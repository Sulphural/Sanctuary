using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

using Sanctuary.Database;

#nullable disable

namespace Sanctuary.Database.Sqlite.Migrations;

// Adds CharacterQuests.CompletedUtc - when a DAILY quest was completed, so it can be judged by calendar
// day and offered again the next one. Null for every non-daily quest, and for daily completions that
// predate this column (treated as long past, so they simply come round again).
[DbContext(typeof(DatabaseContext))]
[Migration("20260812000000_AddQuestCompletedUtc")]
public partial class AddQuestCompletedUtc : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<System.DateTimeOffset>(
            name: "CompletedUtc",
            table: "CharacterQuests",
            type: "TEXT",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "CompletedUtc",
            table: "CharacterQuests");
    }
}
