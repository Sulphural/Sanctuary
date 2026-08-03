using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Sanctuary.Core.Helpers;
using Sanctuary.Core.IO;
using Sanctuary.Database;
using Sanctuary.Game;
using Sanctuary.Game.Combat;
using Sanctuary.Game.Entities;
using Sanctuary.Gateway.Handlers.Abilities;
using Sanctuary.Game.Resources.Definitions;
using Sanctuary.Game.Zones;
using Sanctuary.Packet;
using Sanctuary.Packet.Common;
using Sanctuary.Packet.Common.Attributes;
using Sanctuary.Packet.Common.Chat;

namespace Sanctuary.Gateway.Handlers;

[PacketHandler]
public static partial class AbilityPacketClientRequestStartAbilityHandler
{
    // internal: shared with the extracted per-category ability classes under Handlers/Abilities.
    internal static ILogger _logger = null!;
    internal static IResourceManager _resourceManager = null!;
    private static IDbContextFactory<DatabaseContext> _dbContextFactory = null!;

    private static readonly ConcurrentDictionary<ulong, ConcurrentDictionary<int, DateTimeOffset>> _itemCooldowns = new();

    // Back to the normal standing idle after a boombox dance (also reused by other emote-style abilities).
    internal const int BoomboxIdleAnimId = 1;

    // Per-category ability classes, tried in order until one matches; a generic catch-all fallback
    // follows for anything that isn't a recognized category. Order matches the original inline dispatch.
    private static readonly IConsumableAbility[] _consumableAbilities =
    [
        new BoomboxAbility(),
        new CakeAbility(),
        new CombatOrbAbility(),
        new CombatPotionAbility(),
        new SillyStringAbility(),
        new TransformFoodAbility(),
        new FoodEffectAbility(),
    ];

    // Energy (from the 2014-04-01 capture): max 100, regen +4/s time-based (full refill 25s, in & out of combat,
    // no kill chunks). Special (slot 1) costs the whole bar (100); basic (slot 0) costs nothing. Reported on the
    // same op38/sub13 ClientUpdatePacketMana the real server used.
    internal const int MaxEnergy = 100;
    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, int> _energy = new();
    internal static void SendEnergy(Player player, int energy) =>
        player.SendTunneled(new ClientUpdatePacketMana { CurrentMana = energy, MaxMana = MaxEnergy });

    // Unique effect-tag ids for the lingering cast-FX plays (start high to stay clear of
    // the zones' heal-shower tag range). internal: shared with Handlers/Abilities classes.
    internal static int _castFxTagCounter = 5000;

    public static void ConfigureServices(IServiceProvider serviceProvider)
    {
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        _logger = loggerFactory.CreateLogger(nameof(AbilityPacketClientRequestStartAbilityHandler));

        _resourceManager = serviceProvider.GetRequiredService<IResourceManager>();
        _dbContextFactory = serviceProvider.GetRequiredService<IDbContextFactory<DatabaseContext>>();

        // Cross-layer bridge for the Energy power-up (PowerupSystem, Game layer) - the real energy pool is
        // private to this handler (Gateway layer), so it can't reach in directly.
        PowerupSystem.RequestEnergyRefill = player =>
        {
            _energy[player.Guid] = MaxEnergy;
            SendEnergy(player, MaxEnergy);
        };

        PowerupSystem.RestoreEnergy = (player, amount) =>
        {
            var energy = GetEnergy(player);
            if (energy >= MaxEnergy)
                return;

            var next = Math.Min(MaxEnergy, energy + amount);
            _energy[player.Guid] = next;
            SendEnergy(player, next);
        };
    }

    public static bool HandlePacket(GatewayConnection connection, ReadOnlySpan<byte> data)
    {
        if (!AbilityPacketClientRequestStartAbility.TryDeserialize(data, out var packet))
        {
            _logger.LogError("Failed to deserialize {packet}. ( Raw: {raw} )",
                nameof(AbilityPacketClientRequestStartAbility), Convert.ToHexString(data));
            return false;
        }

        _logger.LogInformation("AbilityPacket: Id={Id} Slot={Slot}", packet.Data.Id, packet.Data.Slot);

        // DEATH: no acting while knocked out (can't swing/shoot/use items until you respawn).
        if (connection.Player.IsDead)
            return true;

        // Full action lock (Stun/Sleep/Fear/Freeze - see StatusEffects.BlocksAbilities): can't swing,
        // cast, or use an item while under one of these. No current ability applies these to a player
        // yet (this is the enforcement half of the CC system; the apply half is StatusEffects.Apply,
        // for zones/abilities to call), but the gate needs to exist before anything can safely use it.
        if (StatusEffects.BlocksAbilities(connection.Player.Guid))
            return SendFailure(connection);

        // Item bar (id 2) = consumables (boombox / cake / transform food); any other bar = combat ability.
        if (packet.Data.Id == 2)
            return HandleItemAbility(connection, packet);

        return HandleCombatAbility(connection, packet, data);
    }

    private static bool HandleItemAbility(GatewayConnection connection, AbilityPacketClientRequestStartAbility packet)
    {
        connection.Player.ActionBars.TryGetValue(2, out var actionBar);

        if (actionBar is null || !actionBar.Slots.TryGetValue(packet.Data.Slot, out var slot) || slot.IsEmpty)
            return SendFailure(connection);

        if (!connection.Player.ActionBarItemGuids.TryGetValue(2, out var slotItemGuids) ||
            !slotItemGuids.TryGetValue(packet.Data.Slot, out var itemGuid))
            return SendFailure(connection);

        var clientItem = connection.Player.Items.FirstOrDefault(x => x.Id == itemGuid);

        if (clientItem is null)
            return SendFailure(connection);

        if (!_resourceManager.ClientItemDefinitions.TryGetValue(clientItem.Definition, out var itemDefinition) ||
            itemDefinition.ActivatableAbilityId == 0)
            return SendFailure(connection);

        // Per-category ability classes (Handlers/Abilities) - splits this dispatcher into one small
        // class per category (PR #27 review). All recognized categories are migrated now; only the
        // generic catch-all fallback (an item with no recognized category at all) stays inline below,
        // since it isn't really its own "category" so much as a default case.
        foreach (var ability in _consumableAbilities)
        {
            if (ability.Matches(itemDefinition))
                return ability.Handle(connection, packet, packet.Data.Slot, clientItem, itemDefinition);
        }

        TriggerAbilityEffect(connection, itemDefinition);

        if (itemDefinition.SingleUse)
            return ConsumeItem(connection, clientItem, itemDefinition, packet.Data.Slot);

        return true;
    }

    internal static bool IsOnCooldown(ulong playerGuid, int itemDefinitionId)
    {
        return _itemCooldowns.TryGetValue(playerGuid, out var cooldowns) &&
               cooldowns.TryGetValue(itemDefinitionId, out var expiry) &&
               DateTimeOffset.UtcNow < expiry;
    }

    internal static void StartCooldown(ulong playerGuid, int itemDefinitionId, int cooldownMs)
    {
        var cooldowns = _itemCooldowns.GetOrAdd(playerGuid, _ => new ConcurrentDictionary<int, DateTimeOffset>());

        cooldowns[itemDefinitionId] = DateTimeOffset.UtcNow.AddMilliseconds(cooldownMs);
    }

    // Color-variant items (e.g. the 5 Silly String Can colors) share one Icon.Id and differ only by
    // TintId - action bar slots need that tint too or every variant renders as the same untinted icon.
    // Same per-instance-override-over-definition-default pattern as Player.ApplyEquipment's attachment tint.
    internal static int IconTintId(ClientItem clientItem, ClientItemDefinition itemDefinition) =>
        clientItem.Tint == 0 ? itemDefinition.Icon.TintId : clientItem.Tint;

    internal static bool ConsumeItem(GatewayConnection connection, ClientItem clientItem, ClientItemDefinition clientItemDefinition, int actionBarSlot)
    {
        using var dbContext = _dbContextFactory.CreateDbContext();

        var characterId = GuidHelper.GetPlayerId(connection.Player.Guid);
        var dbItem = dbContext.Items.SingleOrDefault(i => i.CharacterId == characterId && i.Id == clientItem.Id);

        if (dbItem is null)
            return SendFailure(connection);

        dbItem.Count--;

        var shouldDeleteItem = dbItem.Count <= 0;

        if (shouldDeleteItem)
            dbContext.Items.Remove(dbItem);

        if (dbContext.SaveChanges() <= 0)
            return SendFailure(connection);

        if (shouldDeleteItem)
        {
            connection.Player.Items.Remove(clientItem);
            connection.SendTunneled(new ClientUpdatePacketItemDelete { ItemGuid = clientItem.Id });

            // Otherwise a still-pending cooldown re-enable (StartActionBarCooldown, scheduled for later)
            // fires after this and un-deletes the slot.
            connection.Player.CancelScheduledSlotPacket(2, actionBarSlot);

            var slotPacket = new ClientUpdatePacketUpdateActionBarSlot { Data = { Id = 2, Slot = actionBarSlot } };
            slotPacket.Slot.IsEmpty = true;

            if (connection.Player.ActionBarItemGuids.TryGetValue(2, out var trackedItems))
                trackedItems.Remove(actionBarSlot);

            connection.SendTunneled(slotPacket);
        }
        else
        {
            clientItem.Count--;

            connection.SendTunneled(new ClientUpdatePacketItemUpdate
            {
                ItemGuid = clientItem.Id,
                Count = clientItem.Count,
                ConsumedCount = clientItem.ConsumedCount,
                AbilityCount = clientItem.AbilityCount,
                RentalExpirationTime = 0
            });

            var slotPacket = new ClientUpdatePacketUpdateActionBarSlot { Data = { Id = 2, Slot = actionBarSlot } };
            slotPacket.Slot.IsEmpty = false;
            slotPacket.Slot.IconId = clientItemDefinition.Icon.Id;
            slotPacket.Slot.IconTintId = IconTintId(clientItem, clientItemDefinition);
            slotPacket.Slot.NameId = clientItemDefinition.NameId;
            slotPacket.Slot.Unknown5 = 1;
            slotPacket.Slot.Unknown6 = 4;
            slotPacket.Slot.Unknown7 = 15;
            slotPacket.Slot.Enabled = true;
            slotPacket.Slot.Unknown10 = 1000;
            slotPacket.Slot.TotalRefreshTime = 1000;
            slotPacket.Slot.Quantity = clientItem.Count;
            slotPacket.Slot.ForceDismount = true;
            slotPacket.Slot.Unknown15 = 1000;

            connection.SendTunneled(slotPacket);
        }

        return true;
    }

    internal static void TriggerAbilityEffect(GatewayConnection connection, ClientItemDefinition clientItemDefinition)
    {
        _resourceManager.Consumables.FoodEffects.TryGetValue(clientItemDefinition.ActivatableAbilityId, out var foodEffect);

        var effectId = foodEffect?.CompositeEffectId ?? clientItemDefinition.CompositeEffectId;
        var quickChatId = foodEffect?.QuickChatId ?? 0;
        var effectDelayMs = foodEffect?.EffectDelayMs ?? 0;

        if (quickChatId != 0)
        {
            connection.Player.SendTunneledToVisible(new QuickChatSendChatToChannelPacket
            {
                Id = quickChatId,
                Guid = connection.Player.Guid,
                Name = connection.Player.Name ?? new NameData(),
                Channel = ChatChannel.WorldArea,
                AreaNameId = 0,
                GuildGuid = 0
            }, true);
        }

        if (effectId != 0)
        {
            var effectPacket = new PlayerUpdatePacketPlayCompositeEffect
            {
                Guid = connection.Player.Guid,
                CompositeEffectId = effectId,
                Clear = true
            };

            if (effectDelayMs > 0)
                connection.Player.SendTunneledToVisibleDelayed(effectPacket, effectDelayMs, true);
            else
                connection.Player.SendTunneledToVisible(effectPacket, true);
        }
    }

    internal static void DespawnNpc(Npc npc, int effectId)
    {
        var removePacket = new PlayerUpdatePacketRemovePlayerGracefully
        {
            Guid = npc.Guid,
            Animate = false,
            Delay = 0,
            EffectDelay = 0,
            CompositeEffectId = effectId,
            Duration = 500
        };

        foreach (var player in npc.Zone.Players)
            player.SendTunneled(removePacket);

        npc.Dispose();
    }

    internal static bool SendFailure(GatewayConnection connection)
    {
        connection.SendTunneled(new AbilityPacketFailed { StringId = 3079 });

        return true;
    }

    internal static void ApplyTransform(GatewayConnection connection, int temporaryAppearance, int durationMs, int effectId = 0)
        => connection.Player.ApplyTemporaryAppearance(temporaryAppearance, durationMs, effectId);

    internal static void RemoveTransform(GatewayConnection connection)
        => connection.Player.RemoveTemporaryAppearance();

}
