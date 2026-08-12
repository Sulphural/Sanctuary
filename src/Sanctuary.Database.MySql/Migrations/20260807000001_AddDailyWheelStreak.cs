using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

using Sanctuary.Database;

#nullable disable

namespace Sanctuary.Database.MySql.Migrations;

// Adds Characters.DailyWheelStreak (consecutive days the daily wheel has been spun; retail paid a bonus
// spin at 3 days and two at 7). Attributes are declared here so Database.Migrate() discovers and applies it.
[DbContext(typeof(DatabaseContext))]
[Migration("20260807000001_AddDailyWheelStreak")]
public partial class AddDailyWheelStreak : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "DailyWheelStreak",
            table: "Characters",
            type: "int",
            nullable: false,
            defaultValue: 0);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "DailyWheelStreak",
            table: "Characters");
    }
}
