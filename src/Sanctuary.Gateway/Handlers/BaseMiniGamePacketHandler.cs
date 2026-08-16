using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Sanctuary.Core.Helpers;
using Sanctuary.Core.IO;
using Sanctuary.Database;
using Sanctuary.Database.Entities;
using Sanctuary.Game;
using Sanctuary.Game.Zones;
using Sanctuary.Packet;
using Sanctuary.Packet.Common;
using Sanctuary.Packet.Common.Attributes;

namespace Sanctuary.Gateway.Handlers;

// INSTANCE WIP (Frostfang Fury): C2S dispatcher for BaseMiniGamePacket (op39) — ported from the team's
// `minigame` branch. LIVE TEST 1 (2026-07-01) taught us the GO! button does NOT send op41/sub108
// (EncounterParticipantRequestEntrance) as assumed — the only thing we logged was CommandPacket sub42
// ClosedMinigameEndScreen (the panel closing). The branch's flow says starting a minigame sends
// op39/sub5 MiniGameStartGame -> server acks with sub17 GameStart. This handler observe-logs EVERY op39
// sub-opcode and treats sub5 as the GO! press: ack + enter the Frostfang arena.
[PacketHandler]
public static class BaseMiniGamePacketHandler
{
    // op39 sub-opcodes (byte-sized!) from the minigame branch.
    private const byte MiniGameStartGame = 5;              // C2S — pressing GO!/start on a minigame offer panel
    private const byte LootWheelOnRotationStopped = 46;    // C2S — the victory wheel finished spinning (04-01 idx 38115)

    // The Battle Item Mystery Pack wheel prize: on live (04-01 idx 38142) it opened INSTANTLY into
    // battle items. Client locale (DescriptionId 6667): "This prize grants the winner 3 battle items!".
    // The real loot TABLE was server-side and is lost — the pack def's Param1 = 636 is its table id
    // (the same 636 echoes as the trailing int of the live contents banner). RECONSTRUCTED pool: the
    // six Cost-50 combat SPHERES — the family the one captured opening drew from (3x Flabbergast) and
    // the same set duplicated as grant-copies right next to the pack's id block (10516-10520):
    //   Sleep 3011 · Unmoving 3013 · Flabbergast 3015 · Frag 3025 · Blast 3074 · Confusion 3089.
    // One random sphere type x3 (the live grant was a single 3x stack). Weights unknown -> uniform.
    private const int MysteryPackDefId = 10482;
    private const int MysteryPackContentsCount = 3;
    private const int MysteryPackTableId = 636;
    private static readonly int[] MysteryPackSpherePool = [3011, 3013, 3015, 3025, 3074, 3089];
    private static readonly Random _rng = new();

    private static ILogger _logger = null!;
    private static IResourceManager _resourceManager = null!;
    private static IDbContextFactory<DatabaseContext> _dbContextFactory = null!;

    public static void ConfigureServices(IServiceProvider serviceProvider)
    {
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        _logger = loggerFactory.CreateLogger(nameof(BaseMiniGamePacketHandler));

        _resourceManager = serviceProvider.GetRequiredService<IResourceManager>();
        _dbContextFactory = serviceProvider.GetRequiredService<IDbContextFactory<DatabaseContext>>();
    }

    public static bool HandlePacket(GatewayConnection connection, PacketReader reader)
    {
        if (!reader.TryRead(out byte subOpCode))
        {
            _logger.LogError("Failed to read minigame sub-opcode. ( Data: {data} )", Convert.ToHexString(reader.Span));
            return false;
        }

        _logger.LogInformation("BaseMiniGamePacket sub-opcode={sub} | bytes={hex}",
            subOpCode, Convert.ToHexString(reader.Span));

        return subOpCode switch
        {
            MiniGameStartGame => HandleStartGame(connection, reader),
            LootWheelOnRotationStopped => HandleLootWheelStopped(connection),
            MiniGameEndPacket.OpCode => MiniGameEndPacketHandler.HandlePacket(connection, reader.Span),
            MiniGamePayloadPacket.OpCode => MiniGamePayloadPacketHandler.HandlePacket(connection, reader.Span),
            // observe-only: log-and-accept unknown minigame sub-opcodes while we reverse the family
            _ => true
        };
    }

    // ★ LOOT WHEEL PAYOUT (op39/sub46, body = base 3 ints only). The wheel finished spinning on the
    // prize WE preselected in FrostfangArenaZone.WinEncounter (SetItemToLandOn) — grant it now, exactly
    // like the live server did after 04-01 idx 38115: inventory add/update + RewardBundlePacket
    // (op50/sub1) grant banners. Mystery Pack opens into battle items (contents banner first, then the
    // prize banner — the live order).
    private static bool HandleLootWheelStopped(GatewayConnection connection)
    {
        var player = connection.Player;

        // If this is a combat-encounter win wheel, this is the REAL "the player has now seen their prize"
        // signal - see EncounterArenaZone.NotifyRewardWheelStopped/ReturnHome. No-op for every other wheel
        // context (the daily "Spin For The Win!" wheel, or if the player isn't in a combat encounter at
        // all right now).
        if (player.Zone is CombatEncounterZone encounter)
            encounter.NotifyRewardWheelStopped(player);
        else if (player.Zone is Sanctuary.Game.Zones.SnowballArenaZone snowballArena)
            snowballArena.NotifyRewardWheelStopped(player); // same deferred-return contract, non-combat zone

        var prize = player.PendingWheelPrize;
        var coins = player.PendingWheelCoins;
        var xp = player.PendingWheelXp;
        player.PendingWheelPrize = null;
        player.PendingWheelCoins = 0;
        player.PendingWheelXp = 0;

        if (prize is null && coins <= 0 && xp <= 0)
        {
            _logger.LogInformation("LootWheelOnRotationStopped with no pending prize — ignoring.");
            return true;
        }

        if (prize is null)
        {
            // COINS slice (+ whatever XP the encounter win already granted — same combined banner).
            if (coins > 0)
                GrantCoins(connection, coins);
            connection.SendTunneled(new RewardBundlePacket { Coins = coins, Xp = xp, Unknown15 = 957 });
            SendReceiveText(connection, coins, xp);
            _logger.LogInformation("Loot wheel payout: {coins} coins, {xp} xp -> {name}.", coins, xp, player.Name);
            return true;
        }

        if (prize.ItemDefId == MysteryPackDefId)
        {
            OpenMysteryPack(connection);
            // The wheel-prize banner itself (pack icon/name — live sent it AFTER the contents banner).
            connection.SendTunneled(new RewardBundlePacket { IconId = prize.IconId, NameId = prize.NameId, Xp = xp, Unknown15 = 957 });
            SendReceiveItemText(connection, prize.DisplayName);
            return true;
        }

        // Plain item prize.
        var granted = GrantItem(connection, prize.ItemDefId, prize.Quantity);
        if (granted is not null)
        {
            connection.SendTunneled(new RewardBundlePacket { IconId = prize.IconId, NameId = prize.NameId, Xp = xp, Unknown15 = 957 });
            SendReceiveItemText(connection, prize.DisplayName);
        }

        _logger.LogInformation("Loot wheel payout: item def {def} x{qty}, {xp} xp -> {name} ({ok}).",
            prize.ItemDefId, prize.Quantity, xp, player.Name, granted is not null ? "granted" : "FAILED");

        return true;
    }

    // Blue "You receive..." toast for wheel payouts - own copy of EncounterArenaZone.SendReceiveItemText
    // (Gateway can't reference that Game-layer class). Real client locale strings for this event (mined
    // from en_us_data: id 2 "You receive #count([*item*])", id 3 "You receive #count([*experience*]) and
    // #count([*coins*])") use a #count(...) placeholder whose wire substitution mechanism from
    // ChatPacketFromStringId isn't confirmed - a wrong guess would print the literal broken placeholder on
    // screen, so this pre-substitutes the real value into plain text on a packet (ChatPacketDebugChat)
    // that's confirmed to support <font color> markup instead.
    private static void SendReceiveItemText(GatewayConnection connection, string displayName, int quantity = 1) =>
        connection.SendTunneled(new ChatPacketDebugChat
        {
            Message = $"<font color='#0000FF'>You receive {quantity} {(string.IsNullOrEmpty(displayName) ? "item" : displayName)}.</font>",
            PrintToChat = true,
        });

    private static void SendReceiveText(GatewayConnection connection, int coins, int xp)
    {
        if (coins <= 0 && xp <= 0)
            return;

        var parts = new List<string>();
        if (xp > 0) parts.Add($"{xp} experience");
        if (coins > 0) parts.Add($"{coins} coins");

        connection.SendTunneled(new ChatPacketDebugChat
        {
            Message = $"<font color='#0000FF'>You receive {string.Join(" and ", parts)}.</font>",
            PrintToChat = true,
        });
    }

    // Open one Battle Item Mystery Pack: roll a sphere from the reconstructed loot table,
    // grant 3 to inventory, send the contents banner. Public so the "!pack" test command can sample
    // the distribution without replaying the encounter — each roll logs "Mystery Pack -> 3x ...".
    public static void OpenMysteryPack(GatewayConnection connection)
    {
        var sphereDefId = MysteryPackSpherePool[_rng.Next(MysteryPackSpherePool.Length)];
        var contents = GrantItem(connection, sphereDefId, MysteryPackContentsCount);
        if (contents is not null)
        {
            connection.SendTunneled(new RewardBundlePacket
            {
                Entries =
                [
                    new RewardEntry
                    {
                        IconId = contents.Definition?.Icon.Id ?? 0,
                        TintId = contents.Definition?.Icon.TintId ?? 0,
                        NameId = contents.Definition?.NameId ?? 0,
                        Quantity = MysteryPackContentsCount,
                        ItemDefId = sphereDefId,
                        TailItemGuid = contents.ItemGuid,
                    }
                ],
                Unknown15 = MysteryPackTableId, // live banner carried the pack's loot-table id (Param1)
            });
            SendReceiveItemText(connection, MysteryPackSphereNames.GetValueOrDefault(sphereDefId, "Sphere"), MysteryPackContentsCount);
        }

        // Sphere names for the log (defs don't carry the comment string):
        // 3011 Sleep · 3013 Unmoving · 3015 Flabbergast · 3025 Frag · 3074 Blast · 3089 Confusion.
        _logger.LogInformation("Mystery Pack -> {n}x sphere def {def} for {name} ({ok}).",
            MysteryPackContentsCount, sphereDefId, connection.Player.Name, contents is not null ? "granted" : "GRANT FAILED");
    }

    private static readonly Dictionary<int, string> MysteryPackSphereNames = new()
    {
        [3011] = "Sleep Sphere",
        [3013] = "Unmoving Sphere",
        [3015] = "Flabbergast Sphere",
        [3025] = "Frag Sphere",
        [3074] = "Blast Sphere",
        [3089] = "Confusion Sphere",
    };

    internal sealed record GrantedItem(int ItemGuid, ClientItemDefinition? Definition);

    // Add an item to the player's persistent inventory + live client state (same DB/packet
    // flow as the coin-store buy handler, minus the cost). Returns the inventory item guid.
    // Shared with DailyWheelGame, which pays out the same way.
    internal static GrantedItem? GrantItem(GatewayConnection connection, int definitionId, int quantity)
    {
        if (!_resourceManager.ClientItemDefinitions.TryGetValue(definitionId, out var definition))
        {
            _logger.LogWarning("Loot wheel grant: unknown item definition {def}.", definitionId);
            return null;
        }

        using var dbContext = _dbContextFactory.CreateDbContext();

        var dbQuery = dbContext.Characters
            .Where(x => x.Id == GuidHelper.GetPlayerId(connection.Player.Guid))
            .Select(x => new
            {
                Character = x,
                Item = x.Items.SingleOrDefault(i => i.Definition == definition.Id && i.Tint == 0),
                NextId = x.Items.Max(i => i.Id)
            })
            .SingleOrDefault();

        if (dbQuery is null)
        {
            _logger.LogWarning("Loot wheel grant: character row missing for {guid}.", connection.Player.Guid);
            return null;
        }

        var dbItem = dbQuery.Item;

        if (dbItem is not null)
        {
            dbItem.Count += quantity;
        }
        else
        {
            dbItem = new DbItem
            {
                Id = dbQuery.NextId + 1,
                Definition = definition.Id,
                Tint = 0,
                Count = quantity
            };

            dbQuery.Character.Items.Add(dbItem);
        }

        if (dbContext.SaveChanges() <= 0)
        {
            _logger.LogWarning("Loot wheel grant: DB save failed for def {def}.", definitionId);
            return null;
        }

        var clientItem = connection.Player.Items.SingleOrDefault(x => x.Definition == definition.Id && x.Tint == 0);

        if (clientItem is not null)
        {
            clientItem.Count = dbItem.Count;

            connection.SendTunneled(new ClientUpdatePacketItemUpdate
            {
                ItemGuid = clientItem.Id,
                Count = clientItem.Count,
            });
        }
        else
        {
            clientItem = new ClientItem
            {
                Id = dbItem.Id,
                Tint = dbItem.Tint,
                Count = dbItem.Count,
                Definition = dbItem.Definition
            };

            connection.Player.Items.Add(clientItem);

            using var writer = new PacketWriter();
            clientItem.Serialize(writer);

            connection.SendTunneled(new ClientUpdatePacketItemAdd { Payload = writer.Buffer });
        }

        return new GrantedItem(clientItem.Id, definition);
    }

    internal static void GrantCoins(GatewayConnection connection, int coins)
    {
        using var dbContext = _dbContextFactory.CreateDbContext();

        var dbCharacter = dbContext.Characters.SingleOrDefault(x => x.Id == GuidHelper.GetPlayerId(connection.Player.Guid));
        if (dbCharacter is null)
            return;

        dbCharacter.Coins += coins;
        dbContext.SaveChanges();

        connection.Player.Coins = dbCharacter.Coins;

        connection.SendTunneled(new ClientUpdatePacketCoinCount { Coins = connection.Player.Coins });
    }

    // ClientActivityDefinitions Id=8, "Spin For The Win!" - PreselectedGameId comes back in the FIRST
    // body int, not the third (live-confirmed 2026-07-24: every MiniGameStartGame logged today for this
    // activity showed "8" in the field originally labeled StateId, with GroupId/GameId both -1 - the
    // original [StateId][GroupId][GameId] field-order assumption from the `minigame` branch port was
    // wrong for this activity).
    private const int SpinForTheWinActivityId = 8;

    private static bool HandleStartGame(GatewayConnection connection, PacketReader reader)
    {
        // body: [int GameId][int GroupId][int StateId] (see field-order note above)
        if (!reader.TryRead(out int gameId) || !reader.TryRead(out int groupId) || !reader.TryRead(out int stateId))
        {
            _logger.LogWarning("MiniGameStartGame: short body ( {hex} ) — acking anyway.", Convert.ToHexString(reader.Span));
            gameId = -1; groupId = -1; stateId = 0;
        }

        _logger.LogInformation("MiniGameStartGame (GO! pressed): GameId={game} GroupId={group} StateId={state}",
            gameId, groupId, stateId);

        // GO! pressed on the daily wheel's launch panel: ack the start, then name the movie - the ack alone
        // loads nothing (that was the earlier "start screen appears, then a blank screen" behaviour). Same
        // pair Mining Practice sends, see MiniGameStartGamePacketHandler. Normally the wheel skips this
        // panel entirely and opens straight from 26/11 - see BaseCommandPacketHandler.HandleStartWheel.
        if (gameId == SpinForTheWinActivityId)
        {
            connection.SendTunneled(new MiniGameGameStartPacket(0, -1, -1));
            connection.SendTunneled(DailyWheelGame.CreateStartPacket());

            return true;
        }

        // Same entry as the sub108 GO! path: proper server-side zone transfer into the arena
        // (also sends the GameStart ack).
        EncounterParticipantRequestEntranceHandler.EnterFrostfangArena(connection);

        return true;
    }
}
