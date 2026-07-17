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
using Sanctuary.Game.Resources.Definitions;
using Sanctuary.Game.Zones;
using Sanctuary.Packet;
using Sanctuary.Packet.Common;
using Sanctuary.Packet.Common.Attributes;
using Sanctuary.Packet.Common.Chat;

namespace Sanctuary.Gateway.Handlers;

[PacketHandler]
public static class AbilityPacketClientRequestStartAbilityHandler
{
    private static ILogger _logger = null!;
    private static IResourceManager _resourceManager = null!;
    private static IDbContextFactory<DatabaseContext> _dbContextFactory = null!;

    private static readonly ConcurrentDictionary<ulong, ConcurrentDictionary<int, DateTimeOffset>> _itemCooldowns = new();

    // Back to the normal standing idle after a boombox dance.
    private const int BoomboxIdleAnimId = 1;

    // How long a boombox stays out, which is also its use cooldown.
    private const int BoomboxDurationMs = 120_000;

    private const int FoodEffectCooldownMs = 120_000;

    // Basic attack is essentially spammable (2014 capture: presses as fast as 0.17s apart, no real cooldown).
    // StartCasting ActionTime locks the action-bar slot: tiny window for the basic, short wind-up for specials.
    private const float MeleeActionTime = 0.15f;   // slot 0 basic attack — spammable, matches live cadence
    private const float SpecialActionTime = 0.4f;  // slot 1 named special — a real wind-up
    private const float MeleeDamageDelay = 0.15f;  // number pops as the fast swing lands
    private const float SpecialDamageDelay = 0.4f; // number pops at the end of the special's animation

    // Basic attack resolves ONE swing per animation, not per key-press (the client can fire faster than the
    // clip plays). Pacing is server-side: gate basic resolution to the swing cadence; presses inside the window
    // are ignored so the animation plays fully and one number lands per swing. ~0.66s between hits (2014-04-01
    // capture median 0.662s; sub-0.1s bursts are AoE specials). Specials are rate-gated by energy instead.
    private const int BasicSwingMs = 660;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, long> _nextBasicSwingTicks = new();

    // Energy (from the 2014-04-01 capture): max 100, regen +4/s time-based (full refill 25s, in & out of combat,
    // no kill chunks). Special (slot 1) costs the whole bar (100); basic (slot 0) costs nothing. Reported on the
    // same op38/sub13 ClientUpdatePacketMana the real server used.
    private const int MaxEnergy = 100;
    private const int SpecialEnergyCost = NinjaWeaponAbilities.SpecialEnergyCost; // 100 — shared with the toolbar's slot ManaCost (client grey-out)
    // Live value = 4 (25s refill, 04-01 capture). Bump locally for faster energy while iterating.
    private const int EnergyRegenPerSec = 4;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, int> _energy = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, bool> _regenRunning = new();

    private static int GetEnergy(Player player) => _energy.TryGetValue(player.Guid, out var e) ? e : MaxEnergy;

    private static void SendEnergy(Player player, int energy) =>
        player.SendTunneled(new ClientUpdatePacketMana { CurrentMana = energy, MaxMana = MaxEnergy });

    // Time-based +4/sec regen loop, running only while the player's energy is below max (mirrors the real
    // server, which only streamed op38/sub13 while the bar was refilling).
    private static void StartEnergyRegen(Player player)
    {
        if (!_regenRunning.TryAdd(player.Guid, true))
            return; // already regenerating

        _ = Task.Run(async () =>
        {
            try
            {
                while (GetEnergy(player) < MaxEnergy)
                {
                    await Task.Delay(1000);
                    var next = Math.Min(MaxEnergy, GetEnergy(player) + EnergyRegenPerSec);
                    _energy[player.Guid] = next;
                    SendEnergy(player, next);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Energy regen loop failed.");
            }
            finally
            {
                _regenRunning.TryRemove(player.Guid, out _);
            }
        });
    }

    // Archer traits (passive, on basic + specials): Precision L5 (+dmg/+crit chance), Marksmanship L10 (crits
    // harder), Lucky Shot L20 (hit sometimes restores energy); Reflexes L15 = run speed + dodge (elsewhere).
    // Levels/magnitudes in ArcherWeaponAbilities.

    // Apply the Archer damage traits to one hit: Precision's flat bonus + crit-chance, and (on a
    // crit) Marksmanship's extra crit damage. Returns the final damage for this hit.
    private static int ApplyArcherTraitDamage(Player player, int baseDamage)
    {
        var dmg = (float)baseDamage;

        if (ArcherWeaponAbilities.HasTrait(player, ArcherWeaponAbilities.PrecisionLevel))
            dmg *= 1f + ArcherWeaponAbilities.PrecisionDamageBonus;

        // Crit chance: base + Precision's bonus (only archers with Precision roll crits here).
        var critChance = 0;
        if (ArcherWeaponAbilities.HasTrait(player, ArcherWeaponAbilities.PrecisionLevel))
            critChance = ArcherWeaponAbilities.BaseCritChancePercent + ArcherWeaponAbilities.PrecisionCritChanceBonus;

        if (critChance > 0 && Random.Shared.Next(100) < critChance)
        {
            var critMult = ArcherWeaponAbilities.BaseCritMultiplier;
            if (ArcherWeaponAbilities.HasTrait(player, ArcherWeaponAbilities.MarksmanshipLevel))
                critMult += ArcherWeaponAbilities.MarksmanshipCritBonus;
            dmg *= critMult;
        }

        return Math.Max(1, (int)dmg);
    }

    // Brawler offensive traits: Bruising Strikes adds crit CHANCE (an unlocked Brawler rolls crits here);
    // Savvy makes those crits hit harder (crit MULTIPLIER). Rolled per hit, so AoE specials can crit some
    // targets and not others. A no-op for non-Brawlers / a Brawler below level 5.
    private static int ApplyBrawlerTraitDamage(Player player, int baseDamage)
    {
        if (!BrawlerWeaponAbilities.HasTrait(player, BrawlerWeaponAbilities.BruisingStrikesLevel))
            return baseDamage;

        var critChance = BrawlerWeaponAbilities.BaseCritChancePercent + BrawlerWeaponAbilities.BruisingStrikesCritChanceBonus;
        if (Random.Shared.Next(100) >= critChance)
            return baseDamage;

        var critMult = BrawlerWeaponAbilities.BaseCritMultiplier;
        if (BrawlerWeaponAbilities.HasTrait(player, BrawlerWeaponAbilities.SavvyLevel))
            critMult += BrawlerWeaponAbilities.SavvyCritBonus;

        return Math.Max(1, (int)(baseDamage * critMult));
    }

    // Lucky Shot (L20): a chance on each landed hit to refund a little energy (and kick the regen
    // loop so the bar visibly ticks up).
    private static void TryLuckyShotEnergy(Player player)
    {
        if (!ArcherWeaponAbilities.HasTrait(player, ArcherWeaponAbilities.LuckyShotLevel))
            return;
        if (Random.Shared.Next(100) >= ArcherWeaponAbilities.LuckyShotChancePercent)
            return;

        var energy = GetEnergy(player);
        if (energy >= MaxEnergy)
            return;

        var next = Math.Min(MaxEnergy, energy + ArcherWeaponAbilities.LuckyShotEnergyRestore);
        _energy[player.Guid] = next;
        SendEnergy(player, next);
    }

    // The ability comes from the pressed slot + equipped weapon (the job kit): slot 0 = melee, slot 1 = the
    // weapon's special. Damage / animation / hit-FX all from that table.

    // Unique effect-tag ids for the lingering cast-FX plays (start high to stay clear of
    // the zones' heal-shower tag range).
    private static int _castFxTagCounter = 5000;

    public static int? DebugAnimationOverride;

    public static void ConfigureServices(IServiceProvider serviceProvider)
    {
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        _logger = loggerFactory.CreateLogger(nameof(AbilityPacketClientRequestStartAbilityHandler));

        _resourceManager = serviceProvider.GetRequiredService<IResourceManager>();
        _dbContextFactory = serviceProvider.GetRequiredService<IDbContextFactory<DatabaseContext>>();
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

        if (_resourceManager.Consumables.Boomboxes.ContainsKey(itemDefinition.Id))
            return HandleBoombox(connection, packet.Data.Slot, clientItem, itemDefinition);

        if (_resourceManager.Consumables.Cakes.TryGetValue(itemDefinition.Id, out var cakeDefinition))
            return HandleCake(connection, packet.Data.Slot, clientItem, itemDefinition, cakeDefinition);

        // Random-transform foods (e.g. Jack-O-Lantern) roll one of their listed
        // transformations instead of using the item's fixed ability id.
        var transformAbilityId = itemDefinition.ActivatableAbilityId;

        if (_resourceManager.Consumables.RandomTransformFoods.TryGetValue(itemDefinition.Id, out var randomFood) && randomFood.TransformAbilityIds.Length > 0)
            transformAbilityId = randomFood.TransformAbilityIds[Random.Shared.Next(randomFood.TransformAbilityIds.Length)];

        if (_resourceManager.Consumables.Transformations.TryGetValue(transformAbilityId, out var transform))
            return HandleTransformFood(connection, packet.Data.Slot, clientItem, itemDefinition, transform);

        if (_resourceManager.Consumables.FoodEffects.ContainsKey(itemDefinition.ActivatableAbilityId))
            return HandleFoodEffect(connection, packet.Data.Slot, clientItem, itemDefinition);

        TriggerAbilityEffect(connection, itemDefinition);

        if (itemDefinition.SingleUse)
            return ConsumeItem(connection, clientItem, itemDefinition, packet.Data.Slot);

        return true;
    }

    private static bool HandleBoombox(GatewayConnection connection, int slot, ClientItem clientItem, ClientItemDefinition itemDefinition)
    {
        if (IsOnCooldown(connection.Player.Guid, itemDefinition.Id))
            return SendFailure(connection);

        SpawnBoomboxNpc(connection, itemDefinition);

        StartCooldown(connection.Player.Guid, itemDefinition.Id, BoomboxDurationMs);
        connection.Player.StartActionBarCooldown(2, slot, itemDefinition.Icon.Id, itemDefinition.NameId, clientItem.Count, BoomboxDurationMs);

        return true;
    }

    private static bool HandleCake(GatewayConnection connection, int slot, ClientItem clientItem, ClientItemDefinition itemDefinition, CakeItemDefinition cakeDefinition)
    {
        if (IsOnCooldown(connection.Player.Guid, itemDefinition.Id))
            return SendFailure(connection);

        SpawnCakeNpc(connection, cakeDefinition);

        StartCooldown(connection.Player.Guid, itemDefinition.Id, cakeDefinition.CooldownMs);
        connection.Player.StartActionBarCooldown(2, slot, itemDefinition.Icon.Id, itemDefinition.NameId, clientItem.Count, cakeDefinition.CooldownMs);

        return true;
    }

    private static bool HandleTransformFood(GatewayConnection connection, int slot, ClientItem clientItem, ClientItemDefinition itemDefinition, TransformAbilityDefinition transform)
    {
        if (IsOnCooldown(connection.Player.Guid, itemDefinition.Id))
            return SendFailure(connection);

        if (connection.Player.TemporaryAppearance != 0)
            return SendFailure(connection);

        connection.Player.ApplyTemporaryAppearance(transform.ModelId, transform.DurationMs, transform.CompositeEffectId);

        StartCooldown(connection.Player.Guid, itemDefinition.Id, transform.CooldownMs);

        var count = clientItem.Count;

        if (itemDefinition.SingleUse)
            ConsumeItem(connection, clientItem, itemDefinition, slot);

        if (count > 1)
            connection.Player.StartActionBarCooldown(2, slot, itemDefinition.Icon.Id, itemDefinition.NameId, count - 1, transform.CooldownMs);

        return true;
    }

    private static bool HandleFoodEffect(GatewayConnection connection, int slot, ClientItem clientItem, ClientItemDefinition itemDefinition)
    {
        if (IsOnCooldown(connection.Player.Guid, itemDefinition.Id))
            return SendFailure(connection);

        StartCooldown(connection.Player.Guid, itemDefinition.Id, FoodEffectCooldownMs);

        TriggerAbilityEffect(connection, itemDefinition);

        var count = clientItem.Count;
        var hasItemLeft = !itemDefinition.SingleUse || count > 1;

        if (itemDefinition.SingleUse)
            ConsumeItem(connection, clientItem, itemDefinition, slot);

        if (hasItemLeft)
            connection.Player.StartActionBarCooldown(2, slot, itemDefinition.Icon.Id, itemDefinition.NameId,
                itemDefinition.SingleUse ? count - 1 : count, FoodEffectCooldownMs);

        return true;
    }

    private static bool IsOnCooldown(ulong playerGuid, int itemDefinitionId)
    {
        return _itemCooldowns.TryGetValue(playerGuid, out var cooldowns) &&
               cooldowns.TryGetValue(itemDefinitionId, out var expiry) &&
               DateTimeOffset.UtcNow < expiry;
    }

    private static void StartCooldown(ulong playerGuid, int itemDefinitionId, int cooldownMs)
    {
        var cooldowns = _itemCooldowns.GetOrAdd(playerGuid, _ => new ConcurrentDictionary<int, DateTimeOffset>());

        cooldowns[itemDefinitionId] = DateTimeOffset.UtcNow.AddMilliseconds(cooldownMs);
    }

    private static bool ConsumeItem(GatewayConnection connection, ClientItem clientItem, ClientItemDefinition clientItemDefinition, int actionBarSlot)
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

    private static void TriggerAbilityEffect(GatewayConnection connection, ClientItemDefinition clientItemDefinition)
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

    private static void SpawnCakeNpc(GatewayConnection connection, CakeItemDefinition cakeDefinition)
    {
        if (connection.Player.Zone is not StartingZone startingZone)
            return;

        if (!startingZone.TryCreateNpc(out var cakeNpc))
            return;

        cakeNpc.NameId = cakeDefinition.NameId;
        cakeNpc.ModelId = cakeDefinition.ModelId;
        cakeNpc.TextureAlias = "";
        cakeNpc.TintAlias = "";
        cakeNpc.Scale = 1.0f;
        cakeNpc.Animation = cakeDefinition.Animation;
        cakeNpc.HideNamePlate = false;
        cakeNpc.IsInteractable = true;
        cakeNpc.CursorId = (byte)cakeDefinition.CursorId;

        var forwardDirection = Vector3.Transform(new Vector3(0, 0, 1), connection.Player.Rotation);
        var spawnPosition = new Vector4(
            connection.Player.Position.X + forwardDirection.X * 1.5f,
            connection.Player.Position.Y + forwardDirection.Y * 1.5f,
            connection.Player.Position.Z + forwardDirection.Z * 1.5f,
            connection.Player.Position.W
        );

        cakeNpc.Visible = true;
        cakeNpc.UpdatePosition(spawnPosition, connection.Player.Rotation);

        if (cakeDefinition.Type == CakeItemType.BossCake)
        {
            cakeNpc.InteractAction = player =>
            {
                var abilityId = cakeDefinition.TransformAbilityIds[Random.Shared.Next(cakeDefinition.TransformAbilityIds.Length)];

                if (_resourceManager.Consumables.Transformations.TryGetValue(abilityId, out var transform))
                    player.ApplyTemporaryAppearance(transform.ModelId, transform.DurationMs, transform.CompositeEffectId);
            };
        }
        else
        {
            var scareReadyTime = DateTimeOffset.MinValue;

            cakeNpc.InteractAction = player =>
            {
                if (DateTimeOffset.UtcNow < scareReadyTime)
                    return;

                scareReadyTime = DateTimeOffset.UtcNow.AddMilliseconds(cakeDefinition.ScareCooldownMs);

                // Every scare group and transform is equally likely.
                var roll = Random.Shared.Next(cakeDefinition.ScareGroups.Length + cakeDefinition.TransformAbilityIds.Length);

                if (roll < cakeDefinition.ScareGroups.Length)
                {
                    foreach (var effectId in cakeDefinition.ScareGroups[roll])
                    {
                        player.SendTunneledToVisible(new PlayerUpdatePacketPlayCompositeEffect
                        {
                            Guid = cakeNpc.Guid,
                            CompositeEffectId = effectId,
                            Position = cakeNpc.Position,
                            Clear = true
                        }, true);
                    }
                }
                else
                {
                    var abilityId = cakeDefinition.TransformAbilityIds[roll - cakeDefinition.ScareGroups.Length];

                    if (_resourceManager.Consumables.Transformations.TryGetValue(abilityId, out var transform))
                        player.ApplyTemporaryAppearance(transform.ModelId, transform.DurationMs, transform.CompositeEffectId);
                }
            };
        }

        var poofEffect = new PlayerUpdatePacketPlayCompositeEffect
        {
            Guid = cakeNpc.Guid,
            CompositeEffectId = cakeDefinition.SpawnPoofEffectId,
            Position = spawnPosition,
            Clear = false
        };

        connection.Player.SendTunneled(poofEffect);
        connection.Player.OnAddVisibleNpcs([cakeNpc]);

        foreach (var player in connection.Player.VisiblePlayers.Values)
        {
            player.SendTunneled(poofEffect);
            player.OnAddVisibleNpcs([cakeNpc]);
        }

        var despawnTime = DateTimeOffset.UtcNow.AddMilliseconds(cakeDefinition.LifetimeMs);

        cakeNpc.UpdateEverySecondAction = () =>
        {
            if (DateTimeOffset.UtcNow >= despawnTime)
                DespawnNpc(cakeNpc, cakeDefinition.SpawnPoofEffectId);
        };
    }

    private static void SpawnBoomboxNpc(GatewayConnection connection, ClientItemDefinition itemDefinition)
    {
        if (connection.Player.Zone is not StartingZone startingZone)
            return;

        if (!startingZone.TryCreateNpc(out var boomboxNpc))
            return;

        _resourceManager.Consumables.Boomboxes.TryGetValue(itemDefinition.Id, out var boomboxDefinition);

        var modelId = boomboxDefinition?.ModelId ?? 1062;
        var effectId = boomboxDefinition?.EffectId ?? 0;
        var danceSequence = boomboxDefinition?.DanceSequence ?? [3501, 3502, 3503, 3504, 3505];

        boomboxNpc.NameId = 0;
        boomboxNpc.ModelId = modelId;
        boomboxNpc.Name = "Boombox";
        boomboxNpc.TextureAlias = itemDefinition.TextureAlias ?? "";
        boomboxNpc.TintAlias = itemDefinition.TintAlias ?? "";
        boomboxNpc.Scale = 1.0f;
        boomboxNpc.Animation = 2100; // Bouncing animation
        boomboxNpc.CompositeEffectId = effectId; // Owned by the entity, so the client stops it on RemovePlayer
        boomboxNpc.HideNamePlate = true;
        boomboxNpc.IsInteractable = false;

        var leftDirection = Vector3.Transform(new Vector3(-1, 0, 0), connection.Player.Rotation);
        var spawnPosition = new Vector4(
            connection.Player.Position.X + leftDirection.X * 2.0f,
            connection.Player.Position.Y + leftDirection.Y * 2.0f,
            connection.Player.Position.Z + leftDirection.Z * 2.0f,
            connection.Player.Position.W
        );

        // Visible must be set before UpdatePosition so the zone tile system sends AddNpc to players in range.
        boomboxNpc.Visible = true;
        boomboxNpc.UpdatePosition(spawnPosition, connection.Player.Rotation);

        var poofEffect = new PlayerUpdatePacketPlayCompositeEffect
        {
            Guid = boomboxNpc.Guid,
            CompositeEffectId = 21, // PFX_smoke_black_explosion
            Position = spawnPosition,
            Clear = false
        };

        var poofRecipients = boomboxNpc.VisiblePlayers.Values.ToList();

        if (!boomboxNpc.VisiblePlayers.ContainsKey(connection.Player.Guid))
        {
            // Spawner is outside zone tile range, send the packets manually.
            connection.Player.SendTunneled(boomboxNpc.GetAddNpcPacket());
            poofRecipients.Insert(0, connection.Player);
        }

        foreach (var player in poofRecipients)
            player.SendTunneled(poofEffect);

        StartDanceLoop(startingZone, boomboxNpc, spawnPosition, danceSequence);
    }

    private static void StartDanceLoop(StartingZone startingZone, Npc boomboxNpc, Vector4 spawnPosition, int[] danceSequence)
    {
        const float BoomboxRangeInMeters = 15.0f;
        const int SwitchMs = 4000;

        var danceCenter = new Vector3(spawnPosition.X, spawnPosition.Y, spawnPosition.Z);

        var dancing = new HashSet<ulong>();
        var elapsedMs = 0;
        var sinceSwitch = SwitchMs; // so a dance starts on the first tick
        var sequenceIndex = 0;
        var previousAnim = -1;
        var currentAnim = 0;

        boomboxNpc.UpdateEverySecondAction = () =>
        {
            if (elapsedMs >= BoomboxDurationMs)
            {
                foreach (var player in startingZone.Players.Where(p => dancing.Contains(p.Guid)))
                    StopDancing(player);

                DespawnNpc(boomboxNpc, 21);
                return;
            }

            // Rotate to the next dance when due. Only flag a change when the id actually
            // differs, so single-dance boomboxes don't restart the crowd every rotation.
            var animChanged = false;

            if (sinceSwitch >= SwitchMs)
            {
                var selected = danceSequence.Length > 0 ? danceSequence[sequenceIndex % danceSequence.Length] : 3501;
                sequenceIndex++;
                sinceSwitch = 0;

                if (selected != previousAnim)
                {
                    currentAnim = selected;
                    previousAnim = selected;
                    animChanged = true;
                }
            }

            var players = startingZone.Players.ToList();
            var inRange = players.Where(p =>
                Vector3.Distance(new Vector3(p.Position.X, p.Position.Y, p.Position.Z), danceCenter) <= BoomboxRangeInMeters)
                .ToList();
            var inRangeGuids = inRange.Select(p => p.Guid).ToHashSet();

            foreach (var player in players.Where(p => dancing.Contains(p.Guid) && !inRangeGuids.Contains(p.Guid)))
                StopDancing(player);

            var newcomers = inRange.Where(p => !dancing.Contains(p.Guid)).ToList();
            dancing = inRangeGuids;

            // On a rotation, re-sync the whole crowd so it stays phase-locked. Otherwise just
            // start late arrivals on the current dance without hitching everyone else.
            if (animChanged)
                SyncDance(inRange, currentAnim);
            else if (newcomers.Count > 0)
                SyncDance(newcomers, currentAnim);

            elapsedMs += 1000;
            sinceSwitch += 1000;
        };
    }

    private static void SyncDance(List<Player> targets, int animationId)
    {
        if (targets.Count == 0)
            return;

        var sync = new PlayerUpdatePacketSetSynchronizedAnimations();

        foreach (var player in targets)
            sync.Animations.Add(new PlayerUpdatePacketSetSynchronizedAnimations.Animation { Guid = player.Guid, AnimationId = animationId });

        var recipients = new HashSet<Player>(targets);

        foreach (var player in targets)
            foreach (var visiblePlayer in player.VisiblePlayers.Values)
                recipients.Add(visiblePlayer);

        foreach (var recipient in recipients)
            recipient.SendTunneled(sync);
    }

    private static void StopDancing(Player player)
    {
        player.SendTunneledToVisible(new PlayerUpdatePacketSetAnimation
        {
            Guid = player.Guid,
            AnimationId = BoomboxIdleAnimId,
            PlayType = 1
        }, true);
    }

    private static void DespawnNpc(Npc npc, int effectId)
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

    private static bool SendFailure(GatewayConnection connection)
    {
        connection.SendTunneled(new AbilityPacketFailed { StringId = 3079 });

        return true;
    }

    internal static void ApplyTransform(GatewayConnection connection, int temporaryAppearance, int durationMs, int effectId = 0)
        => connection.Player.ApplyTemporaryAppearance(temporaryAppearance, durationMs, effectId);

    internal static void RemoveTransform(GatewayConnection connection)
        => connection.Player.RemoveTemporaryAppearance();

    // COMBAT (combat branch): an ability-bar press — resolve the target + the equipped weapon's ability,
    // play the cast, then resolve damage. See NinjaWeaponAbilities for the slot -> ability mapping.
    private static bool HandleCombatAbility(GatewayConnection connection, AbilityPacketClientRequestStartAbility packet, ReadOnlySpan<byte> data)
    {
        // COMBAT WIP: capture the live client->server StartAbility fields so we can map
        // action-bar slots to abilities and implement real resolution. Remove/lower once mapped.
        _logger.LogInformation(
            "StartAbility: ActionBar.Id={id} Slot={slot} Target={target} Guid={guid} Pos=({px},{py},{pz},{pw}) Raw={raw}",
            packet.Data.Id, packet.Data.Slot, packet.Target, packet.Guid,
            packet.Position.X, packet.Position.Y, packet.Position.Z, packet.Position.W,
            Convert.ToHexString(data));

        var player = connection.Player;
        var zone = player.Zone;

        // We DON'T enter world-combat just for pressing fire — entry is gated on actually hitting an enemy (see
        // EnterWorldCombat once a target resolves, + the re-stamp in ResolveDamageAfterCast). Swinging at air
        // animates but doesn't flag you. The killing blow keeps you in combat for the decay window so the bow
        // auto-fires at the next enemy after a kill.

        // Resolve the target: honor the client's selected-enemy guid if it sent one; otherwise hit the nearest
        // live hostile within reach (a swing at nothing whiffs — StartCasting plays, no damage). (Old code
        // grabbed the first hostile anywhere in the zone — the "random wolf across the arena gets hit" bug.)
        Npc? targetNpc = null;

        if (packet.Guid != 0 && zone.TryGetNpc(packet.Guid, out var selected) && selected.IsDamageable && selected.IsAlive)
        {
            targetNpc = selected;
        }
        else
        {
            // Auto-target for an unselected swing = nearest live hostile within range (the SOE server chose the
            // target when the client sent Target=0; "nearest in range" reconstructs it). The range cap stops the
            // "random far wolf gets hit" bug; closest (not first-in-list) hits the one on you. No facing cone —
            // the client only sends facing while moving, so a cone whiffs when you stand still. Horizontal (X/Z)
            // radius. Melee = 7u (04-01 capture: 37 hits ran 0.6–9.2, median 2.3; 7 is forgiving of tick lag
            // without grabbing far wolves — lower toward 5 if grabby). Archers use the bow range instead.
            var attackReach = JobWeaponAbilities.AutoTargetReach(player);
            var reach2 = attackReach * attackReach;
            var best2 = reach2;

            foreach (var n in zone.Npcs)
            {
                if (!n.IsHostile || !n.IsDamageable || !n.IsAlive)
                    continue;

                var dx = n.Position.X - player.Position.X;
                var dz = n.Position.Z - player.Position.Z;
                var d2 = dx * dx + dz * dz;
                if (d2 >= best2)
                    continue;

                best2 = d2;
                targetNpc = n;
            }
        }

        var targetGuid = targetNpc?.Guid ?? (packet.Guid != 0 ? packet.Guid : player.Guid);

        // Resolve the ability from the pressed slot + equipped weapon for the ACTIVE JOB's kit
        // (slot 0 = basic attack/shot, slot 1 = the weapon's named special).
        var ability = JobWeaponAbilities.ResolveAbility(player, packet.Data.Slot);

        // Basic attack (slot 0) is fast/spammable; specials wind up. This controls both the client-side
        // slot lock (StartCasting.ActionTime) and when the damage number resolves.
        var isBasicMelee = packet.Data.Slot <= 0;
        var actionTime = isBasicMelee ? MeleeActionTime : SpecialActionTime;
        var damageDelay = isBasicMelee ? MeleeDamageDelay : SpecialDamageDelay;

        // Pace the basic attack to the swing animation: drop presses that arrive before the current swing
        // finishes so we get one swing + one damage number per animation, not one per key-press.
        if (isBasicMelee && BasicSwingMs > 0)
        {
            var now = Environment.TickCount64;
            if (_nextBasicSwingTicks.TryGetValue(player.Guid, out var next) && now < next)
                return true; // still mid-swing — ignore this extra click (no cast, no number)
            _nextBasicSwingTicks[player.Guid] = now + BasicSwingMs;
        }

        // Energy gate (non-basic slots): each ability drains its EnergyCost (weapon specials = full 100, archer
        // level abilities = 50). Can't afford it => drop the press. Matches the server-gated special.
        if (!isBasicMelee)
        {
            var cost = ability.EnergyCost;
            var energy = GetEnergy(player);
            if (energy < cost)
            {
                _logger.LogInformation("StartAbility: ability blocked — energy {e}/{max} < {cost}.",
                    energy, MaxEnergy, cost);
                return true;
            }

            var remaining = energy - cost;
            _energy[player.Guid] = remaining;
            SendEnergy(player, remaining);   // op38/sub13: bar drops by the cost
            StartEnergyRegen(player);        // begin the +4/sec refill
        }

        // Lingering cast FX (CastEffectStopMs > 0: projectile trails / loops that never self-terminate): play as
        // an effect tag on the caster and remove after the window, so the trail flashes with the shot instead of
        // lingering. One-shot cast FX keep riding StartCasting's CompositeEffectId.
        var startCastingFx = ability.CastEffectId;
        if (startCastingFx > 0 && ability.CastEffectStopMs > 0)
        {
            startCastingFx = 0;

            var tagId = System.Threading.Interlocked.Increment(ref _castFxTagCounter);
            player.SendTunneledToVisible(new PlayerUpdatePacketAddEffectTagCompositeEffect
            {
                Guid = player.Guid,
                TagId = tagId,
                CompositeEffectId = ability.CastEffectId,
                SourceGuid = player.Guid,
            }, sendToSelf: true);
            var stopMs = ability.CastEffectStopMs;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(stopMs);
                    player.SendTunneledToVisible(new PlayerUpdatePacketRemoveEffectTagCompositeEffect
                    {
                        Guid = player.Guid,
                        TagId = tagId,
                    }, sendToSelf: true);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lingering cast-FX stop failed.");
                }
            });
        }

        // COMBAT WIP: respond to an ability press with a real StartCasting (proven to render a cast bar
        // + play the caster's animation) instead of the AbilityPacketFailed stub.
        var startCasting = new AbilityPacketStartCasting
        {
            Unknown = player.Guid,            // caster
            Unknown2 = targetGuid,            // target
            CompositeEffectId = startCastingFx, // one-shot FX on the caster during the cast
            Animation = DebugAnimationOverride ?? ability.Animation, // override via !anim for live probing
            AbilityId = packet.Data.Slot + 1, // cast identifier (not visual-critical)
            ActionTime = actionTime,
            HasActionProgress = false,        // no cast/progress bar for a basic melee swing
        };

        // Broadcast the cast to everyone who can see the caster (not just their own screen) so party members
        // see each other's moves/FX. Was caster-only, which is why teammates saw enemies die but not the moves.
        player.SendTunneledToVisible(startCasting, sendToSelf: true);

        // Weapon-empowering specials (Mysticism / Mystical Blade) bind their FX to the sword (item slot 7)
        // instead of the body. SlotCompositeEffectOverride op35/sub31: Guid + slot + composite effect.
        if (ability.SwordEffectId > 0)
        {
            player.SendTunneledToVisible(new PlayerUpdatePacketSlotCompositeEffectOverride
            {
                Guid = player.Guid,
                Slot = NinjaWeaponAbilities.WeaponSlot, // 7 = the equipped weapon
                CompositeEffect = ability.SwordEffectId,
            }, sendToSelf: true);
        }

        // COMBAT WIP: Shadow Army (any special with SummonCount>0) spawns temporary shadow-clone NPCs
        // around the caster (using the caster's model), then they poof away after a few seconds.
        if (ability.SummonCount > 0 && zone is StartingZone summonZone)
            summonZone.SummonShadowClones(player, ability.SummonCount, 12);

        // AOE specials (AoeRadius > 0) hit EVERY live hostile within the radius of the CASTER — the whole
        // pack, not just the selected target. Single-target abilities keep the resolved target.
        System.Collections.Generic.List<Npc> targets;
        if (ability.AoeRadius > 0)
        {
            var r2 = ability.AoeRadius * ability.AoeRadius;
            var c = player.Position;
            targets = zone.Npcs
                .Where(n => n.IsHostile && n.IsDamageable && n.IsAlive)
                .Where(n =>
                {
                    var dx = n.Position.X - c.X;
                    var dz = n.Position.Z - c.Z;
                    return dx * dx + dz * dz <= r2;
                })
                .ToList();
        }
        else
        {
            targets = targetNpc is null ? [] : [targetNpc];
        }

        if (targets.Count == 0)
        {
            _logger.LogInformation("StartAbility: no damageable target found (slot {slot}, aoe {radius}).",
                packet.Data.Slot, ability.AoeRadius);
            return true;
        }

        // A real enemy is being engaged (at least one live hostile target) — NOW enter world-combat. Gating it
        // here (instead of on every key press) is what stops firing into empty air from flagging you in-combat.
        player.EnterWorldCombat();

        _logger.LogInformation("Ability slot {slot} = '{name}' (dmg {dmg}, anim {anim}, fx {fx}, targets {count})",
            packet.Data.Slot, ability.Name, ability.Damage, ability.Animation, ability.EffectId, targets.Count);

        ResolveDamageAfterCast(player, targets, ability.Damage, ability.EffectId, damageDelay,
            ability.CasterEndEffectId, ability.EnemyExtraEffectId);

        return true;
    }


    // After the cast bar completes: apply damage, play the hit FX, push each health bar, kill/respawn at 0 HP.
    // Runs off-thread so the cast time elapses first. AoE specials pass the whole in-radius pack (one
    // HitPointModification per victim in a burst, like the 04-01 capture).
    private static void ResolveDamageAfterCast(Player player, System.Collections.Generic.IReadOnlyList<Npc> targets,
        int damage, int effectId, float damageDelay, int casterEndEffectId = 0, int enemyExtraEffectId = 0)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay((int)(damageDelay * 1000));

                // Landing a hit puts you in world-combat (sub132 SetInWorldCombat + sub133 SetIsFighting),
                // which opens the client's floating-damage-number gate and job-locks while fighting (released by
                // the decay). Player owns the state machine, so getting HIT enters it too.
                player.EnterWorldCombat();

                // Caster-side end FX plays ONCE regardless of how many victims (e.g. Dragonstrike's land FX).
                // Broadcast to visible players (sendToSelf) so teammates see it too.
                if (casterEndEffectId > 0)
                {
                    player.SendTunneledToVisible(new PlayerUpdatePacketPlayCompositeEffect
                    {
                        Guid = player.Guid,
                        CompositeEffectId = casterEndEffectId,
                        Position = player.Position,
                    }, sendToSelf: true);
                }

                foreach (var target in targets)
                {
                    if (!target.IsAlive)
                        continue; // e.g. died to an earlier hit this same tick

                    // Job crit traits (each gated to its own job, so only the active job's applies): Archer
                    // Precision/Marksmanship, Brawler Bruising Strikes/Savvy. Rolled per hit so AoE specials
                    // can crit some targets and not others.
                    var hitDamage = ApplyBrawlerTraitDamage(player, ApplyArcherTraitDamage(player, damage));

                    var killed = target.ApplyDamage(hitDamage);

                    // Impact FX on the victim (the ability's EffectId). HitPointModification has no effect field,
                    // so play it explicitly (the switch away from AttackProcessed had dropped every impact FX).
                    if (effectId > 0)
                    {
                        player.SendTunneledToVisible(new PlayerUpdatePacketPlayCompositeEffect
                        {
                            Guid = target.Guid,
                            CompositeEffectId = effectId,
                            Position = target.Position,
                        }, sendToSelf: true);
                    }

                    // EnemyExtraEffectId plays an ADDITIONAL effect on each victim on top of the hit FX
                    // (e.g. Soul Power's purple ring around the enemy).
                    if (enemyExtraEffectId > 0)
                    {
                        player.SendTunneledToVisible(new PlayerUpdatePacketPlayCompositeEffect
                        {
                            Guid = target.Guid,
                            CompositeEffectId = enemyExtraEffectId,
                            Position = target.Position,
                        }, sendToSelf: true);
                    }

                    // Deal the player's own hits via HitPointModification (op35/35), NOT AttackProcessed:
                    // AttackProcessed resets the action-bar melee timer when attacker == local player (the [1]
                    // cooldown bug); HitPointModification gives the number + bar + recoil without touching it.
                    // Wire (04-01): Guid=source(player), Guid2=victim, leading bool=01, i2=maxHP, i3=curHP-after,
                    // i4=-damage.
                    player.SendTunneledToVisible(new PlayerUpdatePacketHitPointModification
                    {
                        Guid = player.Guid,           // source / attacker
                        Guid2 = target.Guid,          // victim
                        Unknown = true,               // player->NPC sample had the leading bool = 01
                        Unknown2 = target.MaxHealth,  // max HP (bar denominator)
                        Unknown3 = target.Health,     // current HP AFTER the hit (bar position)
                        Unknown4 = -hitDamage,        // delta = -damage -> the floating number
                    }, sendToSelf: true);

                    // ARCHER TRAIT — Lucky Shot (L20): a landed hit sometimes restores a little energy.
                    TryLuckyShotEnergy(player);

                    _logger.LogInformation(
                        "Ability hit {name} ({guid}) for {dmg} -> {hp}/{max} HP (killed={killed})",
                        target.Name, target.Guid, hitDamage, target.Health, target.MaxHealth, killed);

                    // Route the kill to the zone (OnNpcKilled): starting zone resets the training dummy, Frostfang
                    // advances the encounter. Non-fatal hits go to OnNpcDamaged so the zone can react to HP
                    // thresholds (the Alpha flees at low health instead of dying).
                    if (killed)
                        player.Zone.OnNpcKilled(player, target);
                    else
                        player.Zone.OnNpcDamaged(player, target);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ability damage resolution failed.");
            }
        });
    }
}
