using System;
using System.Linq;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using Sanctuary.Database;
using Sanctuary.Database.Entities;
using Sanctuary.Gateway.Handlers;

namespace Sanctuary.UdpLibrary.Tests;

[TestClass]
public class HousingDirectorySqliteTests
{
    [TestMethod]
    public void HousingMigrationCreatesTheFinalDirectorySchema()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<DatabaseContext>()
            .UseSqlite(connection, x => x.MigrationsAssembly("Sanctuary.Database.Sqlite"))
            .Options;
        using var dbContext = new DatabaseContext(options);
        dbContext.Database.Migrate();

        foreach (var table in new[] { "Houses", "HouseFixtures", "HouseVotes" })
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name";
            command.Parameters.AddWithValue("$name", table);

            Assert.AreEqual(1L, (long)command.ExecuteScalar()!, $"Missing {table} table.");
        }
    }

    [TestMethod]
    public void DateTimeOffsetOrderingRunsAfterSqliteQueryMaterialization()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<DatabaseContext>()
            .UseSqlite(connection, x => x.MigrationsAssembly("Sanctuary.Database.Sqlite"))
            .Options;
        using var dbContext = new DatabaseContext(options);
        dbContext.Database.EnsureCreated();

        var user = new DbUser
        {
            Id = 1,
            Username = "housing-test",
            Password = "test",
            Created = DateTimeOffset.UtcNow
        };
        var character = new DbCharacter
        {
            Id = 10,
            User = user,
            FirstName = "Housing",
            Head = string.Empty,
            HeadId = 0,
            Hair = string.Empty,
            HairId = 0,
            SkinTone = string.Empty,
            SkinToneId = 0
        };
        var oldest = CreateHouse(1, character, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var newest = CreateHouse(2, character, new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero));
        dbContext.AddRange(user, character, newest, oldest);
        dbContext.SaveChanges();

        var houses = dbContext.Houses.AsNoTracking().Include(h => h.Character);

        Assert.AreEqual(oldest.Id, BaseRatingPacketHandler.SelectOldestOwnedHouse(houses, character.Id)?.Id);
        Assert.AreEqual(newest.Id, BaseRatingPacketHandler.SelectFeaturedHouse(houses.Where(h => h.IsPublished))?.Id);
    }

    [TestMethod]
    public void FriendsDirectoryCountUsesOnlyFriendOwnedHouses()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<DatabaseContext>()
            .UseSqlite(connection, x => x.MigrationsAssembly("Sanctuary.Database.Sqlite"))
            .Options;
        using var dbContext = new DatabaseContext(options);
        dbContext.Database.EnsureCreated();

        var user = new DbUser
        {
            Id = 2,
            Username = "housing-friends-test",
            Password = "test",
            Created = DateTimeOffset.UtcNow
        };
        var viewer = CreateCharacter(20, user, "Viewer");
        var friend = CreateCharacter(21, user, "Friend");
        var stranger = CreateCharacter(22, user, "Stranger");
        var friendHouse = CreateHouse(3, friend, new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));
        var strangerHouse = CreateHouse(4, stranger, new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero));

        dbContext.AddRange(user, viewer, friend, stranger, friendHouse, strangerHouse);
        dbContext.SaveChanges();
        dbContext.Friends.Add(new DbFriend
        {
            CharacterId = viewer.Id,
            FriendCharacterId = friend.Id
        });
        dbContext.SaveChanges();

        var publishedHouses = dbContext.Houses
            .AsNoTracking()
            .Include(h => h.Character)
            .Where(h => h.IsPublished)
            .ToList();
        var selected = BaseRatingPacketHandler.SelectDirectoryHouses(
            dbContext,
            publishedHouses,
            viewer.Id,
            mode: 2);

        Assert.AreEqual(1, selected.Count);
        Assert.AreEqual(friendHouse.Id, selected[0].Id);
    }

    [TestMethod]
    public void HomeOwnershipIsScopedToEachCharacter()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<DatabaseContext>()
            .UseSqlite(connection, x => x.MigrationsAssembly("Sanctuary.Database.Sqlite"))
            .Options;
        using var dbContext = new DatabaseContext(options);
        dbContext.Database.EnsureCreated();

        var user = new DbUser
        {
            Id = 3,
            Username = "housing-ownership-test",
            Password = "test",
            Created = DateTimeOffset.UtcNow
        };
        var firstCharacter = CreateCharacter(30, user, "First");
        var secondCharacter = CreateCharacter(31, user, "Second");
        var firstHouse = CreateHouse(5, firstCharacter, DateTimeOffset.UtcNow);
        var secondHouse = CreateHouse(6, secondCharacter, DateTimeOffset.UtcNow);
        secondHouse.Definition = firstHouse.Definition;

        dbContext.AddRange(user, firstCharacter, secondCharacter, firstHouse, secondHouse);
        dbContext.SaveChanges();

        Assert.AreEqual(1, dbContext.Houses.Count(house => house.CharacterId == firstCharacter.Id));
        Assert.AreEqual(1, dbContext.Houses.Count(house => house.CharacterId == secondCharacter.Id));

        var duplicate = CreateHouse(7, firstCharacter, DateTimeOffset.UtcNow);
        duplicate.Definition = firstHouse.Definition;
        dbContext.Houses.Add(duplicate);

        Assert.Throws<DbUpdateException>(() => dbContext.SaveChanges());
    }

    private static DbCharacter CreateCharacter(ulong id, DbUser user, string firstName)
    {
        return new DbCharacter
        {
            Id = id,
            User = user,
            UserId = user.Id,
            FirstName = firstName,
            Head = string.Empty,
            HeadId = 0,
            Hair = string.Empty,
            HairId = 0,
            SkinTone = string.Empty,
            SkinToneId = 0
        };
    }

    private static DbHouse CreateHouse(int id, DbCharacter character, DateTimeOffset created)
    {
        return new DbHouse
        {
            Id = id,
            Definition = 23 + id,
            Name = $"House {id}",
            IsPublished = true,
            Rating = 5,
            Votes = 10,
            Created = created,
            LastVisited = created,
            Character = character,
            CharacterId = character.Id
        };
    }
}
