using System;

using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

using Sanctuary.Database;

#nullable disable

namespace Sanctuary.Database.MySql.Migrations
{
    // MySQL counterpart of the SQLite AddHousingEditorAndDirectory migration. Replaces the original
    // housing schema with the editor/directory model: houses keyed per CHARACTER (CharacterId +
    // Definition, unique), fixtures carrying the editor's transform/customization state, and houses
    // gaining publication/rating fields plus a HouseVotes table.
    //
    // The key types change (ulong -> int on Houses.Id / HouseFixtures.HouseId), so the housing tables
    // are dropped and recreated rather than migrated in place. Existing houses and fixtures are
    // discarded - the old NameId/IconId/CustomName trio has no equivalent in the new model.
    // Attributes declared here (rather than a .Designer.cs) so Database.Migrate() discovers this migration.
    [DbContext(typeof(DatabaseContext))]
    [Migration("20260815000000_AddHousingEditorAndDirectory")]
    public partial class AddHousingEditorAndDirectory : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "HousePermissions");
            migrationBuilder.DropTable(name: "HouseFixtures");
            migrationBuilder.DropTable(name: "Houses");

            migrationBuilder.CreateTable(
                name: "Houses",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    CharacterId = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    Definition = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true),
                    IsLocked = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    IsMembersOnly = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    IsFloraAllowed = table.Column<bool>(type: "tinyint(1)", nullable: false, defaultValue: true),
                    PetAutospawn = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    MaxFixtureCount = table.Column<int>(type: "int", nullable: false, defaultValue: 2000),
                    MaxLandmarkCount = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    FurnitureScore = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    IsPublished = table.Column<bool>(type: "tinyint(1)", nullable: false, defaultValue: false),
                    Votes = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    Rating = table.Column<float>(type: "float", nullable: false, defaultValue: 0f),
                    Description = table.Column<string>(type: "longtext", nullable: false),
                    KeywordList = table.Column<string>(type: "longtext", nullable: false),
                    CustomizationData = table.Column<string>(type: "longtext", nullable: true),
                    Created = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: false),
                    LastVisited = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Houses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Houses_Characters_CharacterId",
                        column: x => x.CharacterId,
                        principalTable: "Characters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_Houses_CharacterId_Definition",
                table: "Houses",
                columns: ["CharacterId", "Definition"],
                unique: true);

            migrationBuilder.CreateTable(
                name: "HouseFixtures",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    HouseId = table.Column<int>(type: "int", nullable: false),
                    ItemDefinitionId = table.Column<int>(type: "int", nullable: false),
                    PositionX = table.Column<float>(type: "float", nullable: false),
                    PositionY = table.Column<float>(type: "float", nullable: false),
                    PositionZ = table.Column<float>(type: "float", nullable: false),
                    PositionW = table.Column<float>(type: "float", nullable: false),
                    RotationX = table.Column<float>(type: "float", nullable: false),
                    RotationY = table.Column<float>(type: "float", nullable: false),
                    RotationZ = table.Column<float>(type: "float", nullable: false),
                    RotationW = table.Column<float>(type: "float", nullable: false),
                    Scale = table.Column<float>(type: "float", nullable: false, defaultValue: 1f),
                    CustomizationData = table.Column<string>(type: "longtext", nullable: true),
                    Created = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HouseFixtures", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HouseFixtures_Houses_HouseId",
                        column: x => x.HouseId,
                        principalTable: "Houses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_HouseFixtures_HouseId",
                table: "HouseFixtures",
                column: "HouseId");

            migrationBuilder.CreateTable(
                name: "HouseVotes",
                columns: table => new
                {
                    HouseId = table.Column<int>(type: "int", nullable: false),
                    CharacterId = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    Rating = table.Column<int>(type: "int", nullable: false),
                    Created = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HouseVotes", x => new { x.HouseId, x.CharacterId });
                    table.ForeignKey(
                        name: "FK_HouseVotes_Houses_HouseId",
                        column: x => x.HouseId,
                        principalTable: "Houses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_HouseVotes_Characters_CharacterId",
                        column: x => x.CharacterId,
                        principalTable: "Characters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_HouseVotes_CharacterId",
                table: "HouseVotes",
                column: "CharacterId");

            // Carried over from our own housing system; PR 111 has no equivalent.
            migrationBuilder.CreateTable(
                name: "HousePermissions",
                columns: table => new
                {
                    HouseId = table.Column<int>(type: "int", nullable: false),
                    CharacterId = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    PermissionLevel = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    Created = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HousePermissions", x => new { x.HouseId, x.CharacterId });
                    table.ForeignKey(
                        name: "FK_HousePermissions_Houses_HouseId",
                        column: x => x.HouseId,
                        principalTable: "Houses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_HousePermissions_Characters_CharacterId",
                        column: x => x.CharacterId,
                        principalTable: "Characters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_HousePermissions_CharacterId",
                table: "HousePermissions",
                column: "CharacterId");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "HousePermissions");
            migrationBuilder.DropTable(name: "HouseVotes");
            migrationBuilder.DropTable(name: "HouseFixtures");
            migrationBuilder.DropTable(name: "Houses");
        }
    }
}
