using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sanctuary.Database.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddPets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Tint",
                table: "Mounts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Definition",
                table: "Mounts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "Pets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    CharacterId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Tint = table.Column<int>(type: "INTEGER", nullable: false),
                    Definition = table.Column<int>(type: "INTEGER", nullable: false),
                    Created = table.Column<DateTimeOffset>(type: "TEXT", nullable: false, defaultValueSql: "datetime('now')")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Pets", x => new { x.Id, x.CharacterId });
                    table.ForeignKey(
                        name: "FK_Pets_Characters_CharacterId",
                        column: x => x.CharacterId,
                        principalTable: "Characters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Mounts_Tint_Definition_CharacterId",
                table: "Mounts",
                columns: new[] { "Tint", "Definition", "CharacterId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Pets_CharacterId",
                table: "Pets",
                column: "CharacterId");

            migrationBuilder.CreateIndex(
                name: "IX_Pets_Tint_Definition_CharacterId",
                table: "Pets",
                columns: new[] { "Tint", "Definition", "CharacterId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Pets");

            migrationBuilder.DropIndex(
                name: "IX_Mounts_Tint_Definition_CharacterId",
                table: "Mounts");

            migrationBuilder.DropColumn(
                name: "Tint",
                table: "Mounts");

            migrationBuilder.DropColumn(
                name: "Definition",
                table: "Mounts");
        }
    }
}
