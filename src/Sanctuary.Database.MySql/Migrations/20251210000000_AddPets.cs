using System;

using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

using Sanctuary.Database;

#nullable disable

namespace Sanctuary.Database.MySql.Migrations
{
    // Attributes declared here (rather than a .Designer.cs) so Database.Migrate() discovers this
    // migration; without them EF skips it and the Pets table is never created.
    [DbContext(typeof(DatabaseContext))]
    [Migration("20251210000000_AddPets")]
    public partial class AddPets : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Pets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    CharacterGuid = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    Name = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Tint = table.Column<int>(type: "int", nullable: false),
                    Definition = table.Column<int>(type: "int", nullable: false),
                    Created = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: false, defaultValueSql: "NOW()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Pets", x => new { x.Id, x.CharacterGuid });
                    table.ForeignKey(
                        name: "FK_Pets_Characters_CharacterGuid",
                        column: x => x.CharacterGuid,
                        principalTable: "Characters",
                        principalColumn: "Guid",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_Pets_CharacterGuid",
                table: "Pets",
                column: "CharacterGuid");

            migrationBuilder.CreateIndex(
                name: "IX_Pets_Tint_Definition_CharacterGuid",
                table: "Pets",
                columns: new[] { "Tint", "Definition", "CharacterGuid" },
                unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Pets");
        }
    }
}
