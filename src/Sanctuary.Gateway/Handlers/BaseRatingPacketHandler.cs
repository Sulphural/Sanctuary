using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Sanctuary.Core.Helpers;
using Sanctuary.Core.IO;
using Sanctuary.Database;
using Sanctuary.Database.Entities;
using Sanctuary.Game;
using Sanctuary.Gateway.Admin;
using Sanctuary.Packet;
using Sanctuary.Packet.Common.Attributes;

namespace Sanctuary.Gateway.Handlers;

[PacketHandler]
public static class BaseRatingPacketHandler
{
    private const string HousingSystem = "Housing";
    private const int MaxDirectoryEntries = 50;

    private static ILogger _logger = null!;
    private static IDbContextFactory<DatabaseContext> _dbContextFactory = null!;
    private static IResourceManager _resourceManager = null!;

    public static void ConfigureServices(IServiceProvider serviceProvider)
    {
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        _logger = loggerFactory.CreateLogger(nameof(BaseRatingPacketHandler));
        _dbContextFactory = serviceProvider.GetRequiredService<IDbContextFactory<DatabaseContext>>();
        _resourceManager = serviceProvider.GetRequiredService<IResourceManager>();
    }

    public static bool HandlePacket(GatewayConnection connection, PacketReader reader)
    {
        if (!reader.TryRead(out byte subOpCode))
            return false;

        var payload = reader.RemainingSpan;
        return subOpCode switch
        {
            3 => HandleDataRequest(connection, payload),
            6 => HandlePublish(connection),
            7 => HandleUnpublish(connection),
            8 => HandleVote(connection, payload),
            12 => HandleSearch(connection, payload),
            16 => HandleCandidateInfo(connection, payload),
            20 => HandleFeatured(connection, payload),
            _ => false
        };
    }

    private static bool HandleDataRequest(GatewayConnection connection, ReadOnlySpan<byte> payload)
    {
        var reader = new PacketReader(payload);
        if (!reader.TryRead(out string? system) || !reader.TryRead(out int mode))
        {
            LogMalformed(connection, 3, payload);
            return false;
        }

        using var dbContext = _dbContextFactory.CreateDbContext();
        var characterId = GuidHelper.GetPlayerId(connection.Player.Guid);
        var houses = dbContext.Houses
            .AsNoTracking()
            .Include(h => h.Character)
            .Where(h => h.IsPublished)
            .ToList();

        var filtered = SelectDirectoryHouses(dbContext, houses, characterId, mode);
        var selected = filtered.Take(MaxDirectoryEntries).ToList();
        var response = new RatingPacketDataReply
        {
            Correlation = connection.Player.Guid,
            System = NormalizeSystem(system),
            TotalCount = filtered.Count
        };

        for (int i = 0; i < selected.Count; i++)
            response.Entries[i] = ToRatingEntry(selected[i]);

        connection.SendTunneled(response);
        return true;
    }

    internal static List<DbHouse> SelectDirectoryHouses(
        DatabaseContext dbContext,
        IEnumerable<DbHouse> houses,
        ulong characterId,
        int mode)
    {
        return (mode switch
        {
            2 => FilterFriends(dbContext, houses, characterId)
                .OrderByDescending(h => h.Rating)
                .ThenByDescending(h => h.Votes),
            3 => houses
                .OrderByDescending(h => h.Created),
            _ => houses
                .OrderByDescending(h => h.Rating)
                .ThenByDescending(h => h.Votes)
                .ThenByDescending(h => h.LastVisited)
        }).ToList();
    }

    private static bool HandleSearch(GatewayConnection connection, ReadOnlySpan<byte> payload)
    {
        var reader = new PacketReader(payload);
        if (!reader.TryRead(out ulong correlation) ||
            !reader.TryRead(out string? system) ||
            !reader.TryRead(out string? query))
        {
            LogMalformed(connection, 12, payload);
            return false;
        }

        using var dbContext = _dbContextFactory.CreateDbContext();
        query ??= string.Empty;
        var normalizedQuery = query.Trim();
        var houses = dbContext.Houses
            .AsNoTracking()
            .Include(h => h.Character)
            .Where(h => h.IsPublished)
            .ToList()
            .Where(h => MatchesSearch(h, normalizedQuery))
            .OrderByDescending(h => h.Rating)
            .ThenByDescending(h => h.Votes)
            .Take(MaxDirectoryEntries)
            .ToList();

        var response = new RatingPacketSearchReply
        {
            Correlation = correlation,
            Query = query,
            Entries = houses.Select(ToRatingEntry).ToList()
        };

        _ = system;
        connection.SendTunneled(response);
        return true;
    }

    private static bool HandleFeatured(GatewayConnection connection, ReadOnlySpan<byte> payload)
    {
        var reader = new PacketReader(payload);
        if (!reader.TryRead(out string? system) || !reader.TryRead(out ulong correlation))
        {
            LogMalformed(connection, 20, payload);
            return false;
        }

        using var dbContext = _dbContextFactory.CreateDbContext();
        var house = SelectFeaturedHouse(dbContext.Houses
            .AsNoTracking()
            .Include(h => h.Character)
            .Where(h => h.IsPublished));

        connection.SendTunneled(new RatingPacketSendFeatured
        {
            Correlation = correlation,
            System = NormalizeSystem(system),
            Entry = house is null ? new RatingDataEntry() : ToRatingEntry(house)
        });
        return true;
    }

    private static bool HandleCandidateInfo(GatewayConnection connection, ReadOnlySpan<byte> payload)
    {
        var reader = new PacketReader(payload);
        if (!reader.TryRead(out string? system) ||
            !reader.TryRead(out string? candidateId) ||
            !reader.TryRead(out ulong ownerGuid) ||
            !reader.TryRead(out ulong requesterGuid))
        {
            LogMalformed(connection, 16, payload);
            return false;
        }

        using var dbContext = _dbContextFactory.CreateDbContext();
        var house = FindCandidateHouse(dbContext, connection, candidateId ?? string.Empty, ownerGuid);
        SendCandidateInfo(connection, dbContext, house, requesterGuid);
        _ = system;
        return true;
    }

    private static bool HandlePublish(GatewayConnection connection)
    {
        using var dbContext = _dbContextFactory.CreateDbContext();
        var house = FindCurrentOwnedHouse(dbContext, connection);
        if (house is null)
            return true;

        house.IsPublished = true;
        dbContext.SaveChanges();
        SendCandidateInfo(connection, dbContext, house, connection.Player.Guid);
        _logger.LogInformation("Published house {HouseId} for character {CharacterId}.", house.Id, house.CharacterId);
        return true;
    }

    private static bool HandleUnpublish(GatewayConnection connection)
    {
        using var dbContext = _dbContextFactory.CreateDbContext();
        var house = FindCurrentOwnedHouse(dbContext, connection);
        if (house is null)
            return true;

        house.IsPublished = false;
        dbContext.SaveChanges();
        SendCandidateInfo(connection, dbContext, house, connection.Player.Guid);
        _logger.LogInformation("Unpublished house {HouseId} for character {CharacterId}.", house.Id, house.CharacterId);
        return true;
    }

    private static bool HandleVote(GatewayConnection connection, ReadOnlySpan<byte> payload)
    {
        var reader = new PacketReader(payload);
        var values = new string[4];
        for (int i = 0; i < values.Length; i++)
        {
            if (!reader.TryRead(out string? value))
            {
                LogMalformed(connection, 8, payload);
                return false;
            }

            values[i] = value ?? string.Empty;
        }

        using var dbContext = _dbContextFactory.CreateDbContext();
        var characterId = GuidHelper.GetPlayerId(connection.Player.Guid);
        var house = FindVoteHouse(dbContext, connection, values);
        var rating = ParseVote(values);

        if (house is null || rating is < 1 or > 5 || !house.IsPublished || house.CharacterId == characterId)
        {
            connection.SendTunneled(new RatingPacketVoteReply());
            return true;
        }

        var existingVote = dbContext.HouseVotes.Find(house.Id, characterId);
        if (existingVote is null)
        {
            dbContext.HouseVotes.Add(new DbHouseVote
            {
                HouseId = house.Id,
                CharacterId = characterId,
                Value = rating
            });
            dbContext.SaveChanges();

            var votes = dbContext.HouseVotes
                .Where(v => v.HouseId == house.Id)
                .Select(v => v.Value)
                .ToList();
            house.Votes = votes.Count;
            house.Rating = votes.Count == 0 ? 0 : (float)votes.Average();
            dbContext.SaveChanges();
        }

        connection.SendTunneled(new RatingPacketVoteReply());
        SendCandidateInfo(connection, dbContext, house, connection.Player.Guid);
        return true;
    }

    private static IEnumerable<DbHouse> FilterFriends(
        DatabaseContext dbContext,
        IEnumerable<DbHouse> houses,
        ulong characterId)
    {
        var friendIds = dbContext.Friends
            .AsNoTracking()
            .Where(f => f.CharacterId == characterId)
            .Select(f => f.FriendCharacterId)
            .ToHashSet();
        return houses.Where(h => friendIds.Contains(h.CharacterId));
    }

    private static DbHouse? FindCandidateHouse(
        DatabaseContext dbContext,
        GatewayConnection connection,
        string candidateId,
        ulong ownerGuid)
    {
        var query = dbContext.Houses.Include(h => h.Character).AsQueryable();
        if (TryParseHouseId(candidateId, out var houseId))
            return query.FirstOrDefault(h => h.Id == houseId);

        if (TryGetCurrentHouseId(connection, out houseId))
        {
            var current = query.FirstOrDefault(h => h.Id == houseId);
            if (current is not null)
                return current;
        }

        if (ownerGuid == 0)
            return null;

        var ownerId = GuidHelper.GetPlayerId(ownerGuid);
        return SelectOldestOwnedHouse(query, ownerId);
    }

    internal static DbHouse? SelectFeaturedHouse(IQueryable<DbHouse> houses)
    {
        return houses
            .ToList()
            .OrderByDescending(h => h.Rating)
            .ThenByDescending(h => h.Votes)
            .ThenByDescending(h => h.LastVisited)
            .FirstOrDefault();
    }

    internal static DbHouse? SelectOldestOwnedHouse(IQueryable<DbHouse> houses, ulong ownerId)
    {
        return houses
            .Where(h => h.CharacterId == ownerId)
            .ToList()
            .OrderBy(h => h.Created)
            .FirstOrDefault();
    }

    private static DbHouse? FindCurrentOwnedHouse(DatabaseContext dbContext, GatewayConnection connection)
    {
        if (!TryGetCurrentHouseId(connection, out var houseId))
            return null;

        var characterId = GuidHelper.GetPlayerId(connection.Player.Guid);
        return dbContext.Houses
            .Include(h => h.Character)
            .FirstOrDefault(h => h.Id == houseId && h.CharacterId == characterId);
    }

    private static DbHouse? FindVoteHouse(
        DatabaseContext dbContext,
        GatewayConnection connection,
        IEnumerable<string> values)
    {
        if (TryGetCurrentHouseId(connection, out var currentHouseId))
        {
            var current = dbContext.Houses
                .Include(h => h.Character)
                .FirstOrDefault(h => h.Id == currentHouseId);
            if (current is not null)
                return current;
        }

        foreach (var value in values)
        {
            if (!TryParseHouseId(value, out var houseId))
                continue;

            var house = dbContext.Houses
                .Include(h => h.Character)
                .FirstOrDefault(h => h.Id == houseId);
            if (house is not null)
                return house;
        }

        return null;
    }

    private static void SendCandidateInfo(
        GatewayConnection connection,
        DatabaseContext dbContext,
        DbHouse? house,
        ulong correlation)
    {
        var response = new RatingPacketCandidateInfoReply { Correlation = correlation };
        if (house is not null)
        {
            var characterId = GuidHelper.GetPlayerId(connection.Player.Guid);
            response.Candidates.Add(new CandidateRatingInfo
            {
                CandidateId = HouseOwnershipService.GetDirectoryCandidateId(house),
                OwnerName = GetCharacterName(house.Character),
                Name = HouseOwnershipService.GetDirectoryHouseName(house, _resourceManager),
                Rating = house.Rating,
                Votes = house.Votes,
                HasRating = house.IsPublished,
                CanVote = house.IsPublished &&
                    house.CharacterId != characterId &&
                    !dbContext.HouseVotes.Any(v => v.HouseId == house.Id && v.CharacterId == characterId)
            });
        }

        connection.SendTunneled(response);
    }

    private static RatingDataEntry ToRatingEntry(DbHouse house)
    {
        return new RatingDataEntry
        {
            CandidateId = HouseOwnershipService.GetDirectoryCandidateId(house),
            OwnerName = GetCharacterName(house.Character),
            Name = HouseOwnershipService.GetDirectoryHouseName(house, _resourceManager),
            OwnerGuid = GuidHelper.GetPlayerGuid(house.CharacterId),
            Snapshot = HouseOwnershipService.GetDirectorySnapshot(house, _resourceManager),
            Description = house.Description,
            Keywords = house.KeywordList,
            Rating = house.Rating,
            Votes = house.Votes
        };
    }

    private static bool MatchesSearch(DbHouse house, string query)
    {
        if (query.Length == 0)
            return true;

        return GetCharacterName(house.Character).Contains(query, StringComparison.OrdinalIgnoreCase) ||
            HouseOwnershipService.GetDirectoryHouseName(house, _resourceManager).Contains(query, StringComparison.OrdinalIgnoreCase) ||
            house.Description.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            house.KeywordList.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetCharacterName(DbCharacter character)
    {
        if (!string.IsNullOrWhiteSpace(character.FullName))
            return character.FullName;

        return string.IsNullOrWhiteSpace(character.LastName)
            ? character.FirstName
            : $"{character.FirstName} {character.LastName}";
    }

    private static int ParseVote(IEnumerable<string> values)
    {
        foreach (var value in values.Reverse())
        {
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rating) &&
                rating is >= 1 and <= 5)
            {
                return rating;
            }
        }

        return 0;
    }

    private static bool TryParseHouseId(string value, out int houseId)
    {
        houseId = 0;
        if (!ulong.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            return false;

        try
        {
            var id = GuidHelper.GetHouseId(parsed);
            if (id > 0 && id <= int.MaxValue)
            {
                houseId = (int)id;
                return true;
            }
        }
        catch (ArgumentException)
        {
        }

        if (parsed <= int.MaxValue)
        {
            houseId = (int)parsed;
            return houseId > 0;
        }

        return false;
    }

    private static bool TryGetCurrentHouseId(GatewayConnection connection, out int houseId)
    {
        houseId = 0;
        if (connection.Player.CurrentHouseGuid == 0)
            return false;

        var id = GuidHelper.GetHouseId(connection.Player.CurrentHouseGuid);
        if (id == 0 || id > int.MaxValue)
            return false;

        houseId = (int)id;
        return true;
    }

    private static string NormalizeSystem(string? system)
    {
        return string.IsNullOrWhiteSpace(system) ? HousingSystem : system;
    }

    private static void LogMalformed(GatewayConnection connection, byte subOpCode, ReadOnlySpan<byte> payload)
    {
        _logger.LogWarning(
            "Malformed rating packet {SubOpCode} from player {PlayerGuid}. Data={Data}",
            subOpCode,
            connection.Player.Guid,
            Convert.ToHexString(payload));
    }
}
