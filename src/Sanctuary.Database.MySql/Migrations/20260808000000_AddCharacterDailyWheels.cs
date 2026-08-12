using System;

using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

using Sanctuary.Database;

#nullable disable

namespace Sanctuary.Database.MySql.Migrations;

// Per-wheel spin state, so each wheel has its own daily spin, streak and bonus spins instead of sharing
// the single set of columns on Characters.
[DbContext(typeof(DatabaseContext))]
[Migration("20260808000000_AddCharacterDailyWheels")]
public partial class AddCharacterDailyWheels : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "CharacterDailyWheels",
            columns: table => new
            {
                WheelId = table.Column<int>(type: "int", nullable: false),
                CharacterId = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                LastSpinUtc = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: true),
                BonusSpins = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                Streak = table.Column<int>(type: "int", nullable: false, defaultValue: 0)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CharacterDailyWheels", x => new { x.WheelId, x.CharacterId });
                table.ForeignKey(
                    name: "FK_CharacterDailyWheels_Characters_CharacterId",
                    column: x => x.CharacterId,
                    principalTable: "Characters",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_CharacterDailyWheels_CharacterId",
            table: "CharacterDailyWheels",
            column: "CharacterId");

        // Carry the old shared state over to the everyday wheel so streaks survive.
        migrationBuilder.Sql(
            """
            INSERT INTO `CharacterDailyWheels` (`WheelId`, `CharacterId`, `LastSpinUtc`, `BonusSpins`, `Streak`)
            SELECT 1, `Id`, `LastDailyWheelSpinUtc`, `DailyWheelBonusSpins`, `DailyWheelStreak`
            FROM `Characters`
            WHERE `LastDailyWheelSpinUtc` IS NOT NULL OR `DailyWheelBonusSpins` <> 0 OR `DailyWheelStreak` <> 0;
            """);

        // Replaced by the per-wheel table above.
        migrationBuilder.DropColumn(name: "LastDailyWheelSpinUtc", table: "Characters");
        migrationBuilder.DropColumn(name: "DailyWheelBonusSpins", table: "Characters");
        migrationBuilder.DropColumn(name: "DailyWheelStreak", table: "Characters");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "CharacterDailyWheels");

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "LastDailyWheelSpinUtc", table: "Characters", type: "datetime(6)", nullable: true);
        migrationBuilder.AddColumn<int>(
            name: "DailyWheelBonusSpins", table: "Characters", type: "int", nullable: false, defaultValue: 0);
        migrationBuilder.AddColumn<int>(
            name: "DailyWheelStreak", table: "Characters", type: "int", nullable: false, defaultValue: 0);
    }
}
