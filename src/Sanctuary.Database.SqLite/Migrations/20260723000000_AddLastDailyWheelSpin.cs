using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

using Sanctuary.Database;

#nullable disable

namespace Sanctuary.Database.Sqlite.Migrations;

// Adds Characters.LastDailyWheelSpinUtc ("Spin For The Win!" once-per-day gate). Attributes are
// declared here (rather than a .Designer.cs) so Database.Migrate() discovers and applies it.
[DbContext(typeof(DatabaseContext))]
[Migration("20260723000000_AddLastDailyWheelSpin")]
public partial class AddLastDailyWheelSpin : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<System.DateTimeOffset>(
            name: "LastDailyWheelSpinUtc",
            table: "Characters",
            type: "TEXT",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "LastDailyWheelSpinUtc",
            table: "Characters");
    }
}
