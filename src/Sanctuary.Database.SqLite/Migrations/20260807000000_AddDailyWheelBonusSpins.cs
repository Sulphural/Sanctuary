using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

using Sanctuary.Database;

#nullable disable

namespace Sanctuary.Database.Sqlite.Migrations;

// Adds Characters.DailyWheelBonusSpins (extra "Spin For The Win!" spins granted by /wheel give, spent
// after the free daily one). Attributes are declared here (rather than a .Designer.cs) so
// Database.Migrate() discovers and applies it.
[DbContext(typeof(DatabaseContext))]
[Migration("20260807000000_AddDailyWheelBonusSpins")]
public partial class AddDailyWheelBonusSpins : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "DailyWheelBonusSpins",
            table: "Characters",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "DailyWheelBonusSpins",
            table: "Characters");
    }
}
