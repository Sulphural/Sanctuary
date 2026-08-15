using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
namespace Sanctuary.Gateway.Admin;

public readonly record struct PendingHousingFixturePlacement(
    ulong PlayerGuid,
    int HouseId,
    ulong FixtureGuid,
    int ItemDefinitionId,
    int ItemRecordId,
    int TintId,
    ulong NpcGuid,
    bool HoverActive,
    DateTimeOffset StartedAt);

public static class HousingPlacementSession
{
    private static readonly ConcurrentDictionary<ulong, PendingHousingFixturePlacement> PendingPlacements = new();
    private static long _nextFixtureGuid = 200_000_000_000;

    public static PendingHousingFixturePlacement Start(
        ulong playerGuid,
        int houseId,
        int itemDefinitionId,
        int itemRecordId,
        int tintId)
    {
        foreach (var pending in PendingPlacements.Where(entry =>
            entry.Value.PlayerGuid == playerGuid && entry.Value.HouseId == houseId))
        {
            PendingPlacements.TryRemove(pending.Key, out _);
        }

        var fixtureGuid = unchecked((ulong)Interlocked.Increment(ref _nextFixtureGuid));
        var placement = new PendingHousingFixturePlacement(
            playerGuid,
            houseId,
            fixtureGuid,
            itemDefinitionId,
            itemRecordId,
            tintId,
            0,
            false,
            DateTimeOffset.UtcNow);

        PendingPlacements[fixtureGuid] = placement;

        return placement;
    }

    public static bool TrySetNpcGuid(
        ulong playerGuid,
        int houseId,
        ulong fixtureGuid,
        ulong npcGuid,
        out PendingHousingFixturePlacement placement)
    {
        placement = default;

        while (PendingPlacements.TryGetValue(fixtureGuid, out var current))
        {
            if (current.PlayerGuid != playerGuid || current.HouseId != houseId)
                return false;

            var updated = current with { NpcGuid = npcGuid };
            if (!PendingPlacements.TryUpdate(fixtureGuid, updated, current))
                continue;

            placement = updated;
            return true;
        }

        return false;
    }

    public static bool TryActivateHover(
        ulong playerGuid,
        int houseId,
        ulong fixtureGuid,
        out PendingHousingFixturePlacement placement)
    {
        placement = default;

        while (PendingPlacements.TryGetValue(fixtureGuid, out var current))
        {
            if (current.PlayerGuid != playerGuid || current.HouseId != houseId)
                return false;

            if (current.HoverActive)
            {
                placement = current;
                return false;
            }

            var updated = current with { HoverActive = true };
            if (!PendingPlacements.TryUpdate(fixtureGuid, updated, current))
                continue;

            placement = updated;
            return true;
        }

        return false;
    }

    public static IReadOnlyList<PendingHousingFixturePlacement> TakeAll(ulong playerGuid, int houseId)
    {
        var removed = new List<PendingHousingFixturePlacement>();

        foreach (var entry in PendingPlacements.Where(entry =>
            entry.Value.PlayerGuid == playerGuid && entry.Value.HouseId == houseId))
        {
            if (PendingPlacements.TryRemove(entry.Key, out var placement))
                removed.Add(placement);
        }

        return removed;
    }

    public static IReadOnlyList<PendingHousingFixturePlacement> TakeAll(ulong playerGuid)
    {
        var removed = new List<PendingHousingFixturePlacement>();

        foreach (var entry in PendingPlacements.Where(entry => entry.Value.PlayerGuid == playerGuid))
        {
            if (PendingPlacements.TryRemove(entry.Key, out var placement))
                removed.Add(placement);
        }

        return removed;
    }

    public static bool TryGet(
        ulong playerGuid,
        int houseId,
        int itemDefinitionId,
        out PendingHousingFixturePlacement placement)
    {
        placement = PendingPlacements.Values
            .Where(candidate =>
                candidate.PlayerGuid == playerGuid &&
                candidate.HouseId == houseId &&
                candidate.ItemDefinitionId == itemDefinitionId)
            .OrderByDescending(candidate => candidate.StartedAt)
            .FirstOrDefault();

        return placement != default;
    }

    public static bool TryTake(
        ulong playerGuid,
        int houseId,
        ulong fixtureGuid,
        out PendingHousingFixturePlacement placement)
    {
        if (!PendingPlacements.TryRemove(fixtureGuid, out placement))
            return false;

        if (placement.PlayerGuid == playerGuid && placement.HouseId == houseId)
            return true;

        placement = default;
        return false;
    }
}
