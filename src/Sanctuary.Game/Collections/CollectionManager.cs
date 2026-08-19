using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Sanctuary.Database;
using Sanctuary.Database.Entities;
using Sanctuary.Game.Entities;
using Sanctuary.Game.Resources.Definitions;
using Sanctuary.Game.Resources.Definitions.Rewards;
using Sanctuary.Packet;

namespace Sanctuary.Game.Collections;

// Pays out a collection the moment its last entry is picked up. This is the Adventurer job's XP loop:
// the freestyle job has no weapon kit and earns nothing from combat, so without a completion payout the
// job simply never levels - the collections panel filled up and that was the end of it.
//
// The reward goes to the collection's OWN job (Adventurer unless the data says otherwise), not the
// active one, so exploring pays the Adventurer while you're dressed as a Ninja. Quest XP is unchanged
// and still credits whatever job is being played.
public sealed class CollectionManager : ICollectionManager
{
    // "Collection complete" celebration effect - the same gold treasure sparkle burst the gather nodes
    // and quest collectibles play, so a finished collection reads as a bigger version of a pickup.
    private const int CompleteEffectId = 5386;

    private readonly IResourceManager _resourceManager;
    private readonly IRewardManager _rewardManager;
    private readonly IDbContextFactory<DatabaseContext> _dbContextFactory;
    private readonly ILogger<CollectionManager> _logger;

    public CollectionManager(
        IResourceManager resourceManager,
        IRewardManager rewardManager,
        IDbContextFactory<DatabaseContext> dbContextFactory,
        ILogger<CollectionManager> logger)
    {
        _resourceManager = resourceManager;
        _rewardManager = rewardManager;
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    public void LoadCompleted(Player player)
    {
        var characterId = player.CharacterId;

        try
        {
            using var db = _dbContextFactory.CreateDbContext();

            var completed = db.CharacterCollections
                .Where(row => row.CharacterId == characterId)
                .Select(row => row.CollectionId)
                .ToList();

            player.CompletedCollections.Clear();
            foreach (var collectionId in completed)
                player.CompletedCollections.Add(collectionId);
        }
        catch (Exception ex)
        {
            // Better to risk re-paying one collection than to fail the login over it.
            _logger.LogError(ex, "Failed to load completed collections for character {characterId}.", characterId);
        }
    }

    public bool OnItemCollected(Player player, int itemDefinitionId)
    {
        // Candidate collections first: cheap id compare over the definitions, and the great majority of
        // granted items (quest rewards, store purchases) belong to none of them and stop here.
        var candidates = _resourceManager.Collections.Values
            .Where(definition => definition.Contains(itemDefinitionId))
            .ToList();

        return candidates.Count != 0 && PayCompleted(player, candidates);
    }

    public bool CheckAll(Player player)
    {
        return PayCompleted(player, _resourceManager.Collections.Values);
    }

    private bool PayCompleted(Player player, IEnumerable<CollectionDefinition> definitions)
    {
        var unpaid = definitions
            .Where(definition => !player.CompletedCollections.Contains(definition.Id))
            .ToList();

        if (unpaid.Count == 0)
            return false;

        var owned = player.Items.Select(item => item.Definition).ToHashSet();

        var completedAny = false;

        foreach (var definition in unpaid)
        {
            if (!definition.IsComplete(owned))
                continue;

            if (Complete(player, definition))
                completedAny = true;
        }

        return completedAny;
    }

    private bool Complete(Player player, CollectionDefinition definition)
    {
        // ★ THE DB ROW IS THE LOCK, NOT THE IN-MEMORY SET. Two characters sharing nothing is fine, but the
        // same character can have the completion evaluated twice in a tick (two nodes clicked together),
        // and the in-memory set alone would let both through. Insert first: if the row is already there,
        // this completion has been paid and nothing else happens.
        if (!TryRecordCompletion(player, definition.Id))
            return false;

        player.CompletedCollections.Add(definition.Id);

        var leveled = false;

        if (definition.RewardXp > 0)
        {
            // A character missing the rewarded job would silently lose the XP, and the collection is
            // already marked paid by this point - worth a warning rather than nothing.
            if (!player.Profiles.Any(profile => profile.Id == definition.RewardProfileId))
            {
                _logger.LogWarning(
                    "Collection {collectionId} rewards profile {profileId}, which character {characterId} does not have - XP not paid.",
                    definition.Id, definition.RewardProfileId, player.CharacterId);
            }
            else
            {
                leveled = player.AwardXpToProfile(definition.RewardXp, definition.RewardProfileId);
                PersistProfileXp(player, definition.RewardProfileId);
            }
        }

        if (definition.RewardCoins > 0)
            _rewardManager.TryGrantCurrency(player, CurrencyType.Coins, definition.RewardCoins);

        foreach (var itemDefinitionId in definition.RewardItems)
            _rewardManager.TryGrantItem(player, itemDefinitionId, tint: 0);

        // Coins + XP fly-in banner, the same celebration a quest turn-in uses for its reward.
        player.SendTunneled(new RewardBundlePacket
        {
            RewardBundle =
            {
                PlayerGuid = player.Guid,
                Coins = definition.RewardCoins,
                Experience = definition.RewardXp
            }
        });

        player.SendTunneledToVisible(new PlayerUpdatePacketPlayCompositeEffect
        {
            Guid = player.Guid,
            CompositeEffectId = CompleteEffectId,
            Position = player.Position
        }, sendToSelf: true);

        _logger.LogInformation(
            "Collection {collectionId} completed by {characterId}: {xp} xp to profile {profileId}{leveled}, {coins} coins, {items} items.",
            definition.Id, player.CharacterId, definition.RewardXp, definition.RewardProfileId,
            leveled ? " (levelled)" : string.Empty, definition.RewardCoins, definition.RewardItems.Count);

        return true;
    }

    // Writes the completion row. Returns false when the character already had one, which is what makes
    // the payout idempotent across a double-click, a race, or a relog.
    private bool TryRecordCompletion(Player player, int collectionId)
    {
        var characterId = player.CharacterId;

        try
        {
            using var db = _dbContextFactory.CreateDbContext();

            if (db.CharacterCollections.Any(row => row.CharacterId == characterId && row.CollectionId == collectionId))
                return false;

            db.CharacterCollections.Add(new DbCharacterCollection
            {
                CollectionId = collectionId,
                CharacterId = characterId,
                CompletedUtc = DateTimeOffset.UtcNow
            });

            return db.SaveChanges() > 0;
        }
        catch (Exception ex)
        {
            // A unique-key violation from a genuine race lands here too: the other caller won, so this
            // one must not pay out.
            _logger.LogError(ex, "Failed to record collection {collectionId} for character {characterId}.",
                collectionId, characterId);
            return false;
        }
    }

    // A one-shot reward should be as durable as the coins/items granted next to it, so the job's level
    // is written now rather than waiting for the save-on-disconnect path (the same reason quest XP is
    // persisted immediately).
    private void PersistProfileXp(Player player, int profileId)
    {
        var profile = player.Profiles.FirstOrDefault(candidate => candidate.Id == profileId);
        if (profile is null)
            return;

        try
        {
            using var db = _dbContextFactory.CreateDbContext();

            var dbProfile = db.Profiles
                .FirstOrDefault(row => row.CharacterId == player.CharacterId && row.Id == profileId);

            if (dbProfile is null)
                return;

            dbProfile.Level = profile.Rank;
            dbProfile.LevelXP = profile.LevelXpRaw;
            db.SaveChanges();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist profile {profileId} XP for character {characterId}.",
                profileId, player.CharacterId);
        }
    }
}
