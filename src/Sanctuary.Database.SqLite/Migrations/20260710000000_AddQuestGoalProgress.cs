using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

using Sanctuary.Database;

#nullable disable

namespace Sanctuary.Database.Sqlite.Migrations;

// Adds CharacterQuests.GoalProgress (how many of a quest's goals are done). The entity has always had this
// field but no migration created it, so the column was missing on any freshly created database.
[DbContext(typeof(DatabaseContext))]
[Migration("20260710000000_AddQuestGoalProgress")]
public partial class AddQuestGoalProgress : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "GoalProgress",
            table: "CharacterQuests",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "GoalProgress",
            table: "CharacterQuests");
    }
}
