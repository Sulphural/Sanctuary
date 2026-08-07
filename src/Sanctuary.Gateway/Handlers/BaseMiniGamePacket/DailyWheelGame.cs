using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Sanctuary.Core.Helpers;
using Sanctuary.Database;
using Sanctuary.Game;
using Sanctuary.Game.Entities;
using Sanctuary.Game.Resources.Definitions;
using Sanctuary.Packet;
using Sanctuary.Packet.Common.Attributes;

namespace Sanctuary.Gateway.Handlers;

// ★ "SPIN FOR THE WIN!" — the daily prize wheel, driven end-to-end against the client's own native
// widget (Client\UI\game_wheel.gfx, launched as minigame Type=22 by StartingZone.LaunchSpinForTheWinGame).
//
// The widget is a SOE "microgame": it talks to the server over the minigame PAYLOAD channel (op39/sub14,
// MiniGamePayloadPacket) with tab-delimited text messages, NOT with dedicated opcodes. Full protocol
// reversed 2026-08-06 out of the .gfx's AS2 (classes GameClientNetwork / GameServerNetwork /
// Wheel.WheelGameClient / Wheel.WheelGameServer — the last one is SOE's own local-mode reference server,
// so the sequence below is exactly what the retail server did):
//
//   S2C (client-side handler signature = wire arg order; max 7 args, tab-separated):
//     OnWheelDataMsg   (id, type, slots, wheelStringID, msgStringID)   slots = SPACE-separated category list
//     OnWheelUpdateMsg (id, spins, consecutiveTimes, timeUntilNextSpin)
//     OnServerReadyMsg ()                                              MUST come after the wheel data:
//                                                                      it moves in the first wheel with spins
//     OnWheelChangedMsg(wheelID)
//     OnSpinInfoMsg    (desiredCategory)                               despite the name: the SLOT INDEX to
//                                                                      land on (0-based, wheel order)
//     OnRewardInfoMsg  (itemIconID, tooltipID, quantity, itemNameID, rewardMsgStringID, tintId)
//
//   C2S (what the widget sends back):
//     OnConnectMsg ()                       -> reply with the wheel data, then OnServerReadyMsg
//     OnChangeWheelRequestMsg (wheelID)     -> ack with OnWheelChangedMsg
//     OnWheelSpinRequestMsg   (wheelID)     -> roll the prize, answer with OnSpinInfoMsg
//     OnWheelStopMsg          (wheelID)     -> the wheel finished animating: PAY OUT + OnRewardInfoMsg
//
// The spin is pure theater — the server decides the slice up front (weighted, from DailyWheel.json) and
// the widget just animates to it, the same way the dungeon loot wheel works.
//
// [PacketHandler] is only here so ConfigureServices gets called - the messages arrive through
// MiniGamePayloadPacketHandler, not through a dispatcher entry of our own.
[PacketHandler]
public static class DailyWheelGame
{
    private static ILogger _logger = null!;
    private static IResourceManager _resourceManager = null!;
    private static IDbContextFactory<DatabaseContext> _dbContextFactory = null!;

    private static readonly Random _rng = new();

    public static void ConfigureServices(IServiceProvider serviceProvider)
    {
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        _logger = loggerFactory.CreateLogger(nameof(DailyWheelGame));

        _resourceManager = serviceProvider.GetRequiredService<IResourceManager>();
        _dbContextFactory = serviceProvider.GetRequiredService<IDbContextFactory<DatabaseContext>>();
    }

    // Every message the widget sends arrives here (routed from MiniGamePayloadPacketHandler). Returns
    // false for messages that aren't ours, so the caller can keep logging them while we reverse the rest.
    public static bool HandleMessage(GatewayConnection connection, string message, int stateId)
    {
        var args = message.Split('\t');

        switch (args[0])
        {
            case "OnConnectMsg":
                SendWheels(connection, stateId);
                Send(connection, stateId, "OnServerReadyMsg");
                return true;

            case "OnChangeWheelRequestMsg":
                // The widget asks to make a wheel current (it does this itself right after ServerReady).
                Send(connection, stateId, "OnWheelChangedMsg", Arg(args, 1));
                return true;

            case "OnWheelSpinRequestMsg":
                return HandleSpinRequest(connection, stateId, Arg(args, 1));

            case "OnWheelStopMsg":
                return HandleSpinStopped(connection, stateId, Arg(args, 1));

            case "FRServer_GameClose":
                // The player shut the wheel. Drop any spin that never reported its stop so a re-open
                // can't pay it out twice.
                connection.Player.PendingDailyWheelSlot = -1;
                connection.Player.PendingDailyWheelId = 0;
                return true;

            default:
                return false;
        }
    }

    // The player pressed the green spin button: pick the winning slice now and tell the wheel where to
    // stop. Nothing is granted yet — the payout waits for OnWheelStopMsg so the banner lands with the
    // animation (same rule as the dungeon loot wheel).
    private static bool HandleSpinRequest(GatewayConnection connection, int stateId, int wheelId)
    {
        var player = connection.Player;

        if (!TryGetWheel(wheelId, out var wheel))
            return true;

        if (GetSpinsLeft(player.Guid, wheel) <= 0)
        {
            _logger.LogInformation("Daily wheel: {name} asked to spin with no spins left — ignoring.", player.Name);
            return true;
        }

        var slot = RiggedSlot >= 0 && RiggedSlot < wheel.Slots.Count ? RiggedSlot : RollSlot(wheel);

        player.PendingDailyWheelSlot = slot;
        player.PendingDailyWheelId = wheel.Id;

        // OnSpinInfoMsg is the ABSOLUTE slice to stop on. The widget adds TICK_DECAY(10) x this value to
        // its spin energy and mCostPerDeg = TICK_DECAY x slots / 360, so it buys exactly this many slices
        // of travel - and Spin() zeroes both mTotalRotation and mcSpinner._rotation first, so every spin
        // starts from slice 0 rather than carrying the last landing over. (Sending it as a relative
        // advance instead was tried live and is wrong: the first spin after opening the widget matches,
        // because the wheel really is at 0 then, and every spin after it drifts.) The Random(2,8) energy
        // Spin() adds is sub-slice jitter, ~7-29 of the 36 degrees a slice spans, which just stops the
        // pointer off-centre.
        _logger.LogInformation("Daily wheel: {name} spins wheel {wheel} -> slot {slot} (art {cat}, {prize}).",
            player.Name, wheel.Id, slot, wheel.Slots[slot].Category, wheel.Slots[slot].Comment);

        Send(connection, stateId, "OnSpinInfoMsg", slot);

        return true;
    }

    // The wheel finished spinning on the slice we chose: consume the daily spin, grant the prize and fill
    // in the "Congratulations! You won" window.
    private static bool HandleSpinStopped(GatewayConnection connection, int stateId, int wheelId)
    {
        var player = connection.Player;

        var slotIndex = player.PendingDailyWheelSlot;

        player.PendingDailyWheelSlot = -1;
        player.PendingDailyWheelId = 0;

        if (slotIndex < 0 || !TryGetWheel(wheelId, out var wheel) || slotIndex >= wheel.Slots.Count)
        {
            _logger.LogInformation("Daily wheel: stop with no pending spin — ignoring.");
            return true;
        }

        var slot = wheel.Slots[slotIndex];

        ConsumeDailySpin(player.Guid);

        var iconId = slot.IconId;
        var nameId = slot.NameStringId;
        var tooltipId = slot.TooltipId;
        var tintId = slot.TintId;
        var quantity = slot.Spins > 0 ? slot.Spins
            : slot.Coins > 0 ? slot.Coins
            : slot.Quantity;

        if (slot.Spins > 0)
        {
            // The wheel's own "extra spins" medallion: hand back more spins instead of an item.
            GrantSpins(player.Guid, slot.Spins);

            if (iconId == 0)
                iconId = ExtraSpinIconId;
            if (nameId == 0)
                nameId = ExtraSpinNameStringId;
        }
        else if (slot.Coins > 0 || slot.CoinAmounts.Count > 0)
        {
            // A coin slice can carry several amounts; each brings its own name string, because the window
            // shows no quantity and the amount would otherwise be invisible.
            var coins = slot.Coins;

            if (slot.CoinAmounts.Count > 0)
            {
                var rolled = slot.CoinAmounts[_rng.Next(slot.CoinAmounts.Count)];

                coins = rolled.Coins;

                if (rolled.NameStringId != 0)
                    nameId = rolled.NameStringId;

                _logger.LogInformation("Daily wheel: slot {slot} rolled {coins} coins from {count} amount(s).",
                    slotIndex, coins, slot.CoinAmounts.Count);
            }

            quantity = coins;

            BaseMiniGamePacketHandler.GrantCoins(connection, coins);

            // 4809 is the coin icon the widget itself swaps in for its coin prize (icon id 22663 is the
            // Station Cash special case, which also pops a "Shop Now!" button we don't want here).
            if (iconId == 0)
                iconId = CoinIconId;

            // Coins have no item definition to take a name from, and the client renders an unresolved id
            // as "##0" in the prize label - give it the real "Coins" string.
            if (nameId == 0)
                nameId = CoinNameStringId;
        }
        else if (slot.ItemId != 0 || slot.ItemIds.Count > 0)
        {
            // A slice can carry a POOL of items of the kind its picture shows - roll one.
            var itemId = slot.ItemIds.Count > 0
                ? slot.ItemIds[_rng.Next(slot.ItemIds.Count)]
                : slot.ItemId;

            var granted = BaseMiniGamePacketHandler.GrantItem(connection, itemId, slot.Quantity);

            if (granted?.Definition is { } definition)
            {
                if (iconId == 0)
                    iconId = definition.Icon.Id;
                if (nameId == 0)
                    nameId = definition.NameId;
                if (tintId == 0)
                    tintId = definition.Icon.TintId;
            }

            // The widget's tooltip is a normal item tooltip, looked up by item definition id.
            if (tooltipId == 0)
                tooltipId = itemId;

            _logger.LogInformation("Daily wheel: slot {slot} rolled item {item} from {count} candidate(s).",
                slotIndex, itemId, Math.Max(1, slot.ItemIds.Count));
        }

        // A zero message id leaves the movie's authored placeholder text ("test") in the reward window, so
        // fall back to the wheel's default line.
        var rewardMsgStringId = slot.RewardMsgStringId != 0 ? slot.RewardMsgStringId : wheel.RewardMsgStringId;

        Send(connection, stateId, "OnRewardInfoMsg",
            iconId, tooltipId, quantity, nameId, rewardMsgStringId, tintId);

        // Grey the wheel out for the rest of the day, in the widget and in the minigames menu.
        SendWheelUpdate(connection, stateId, wheel, player.Guid);
        SendSpinAvailability(connection);

        _logger.LogInformation("Daily wheel: {name} won {prize} (slot {slot}).", player.Name, slot.Comment, slotIndex);

        return true;
    }

    // One OnWheelDataMsg + OnWheelUpdateMsg per wheel, sent before OnServerReadyMsg.
    private static void SendWheels(GatewayConnection connection, int stateId)
    {
        foreach (var wheel in _resourceManager.DailyWheels.Values.OrderBy(x => x.Id))
        {
            var slots = string.Join(' ', wheel.Slots.Select(x => x.Category));

            Send(connection, stateId, "OnWheelDataMsg",
                wheel.Id, wheel.Type, slots, wheel.NameStringId, wheel.MsgStringId);

            SendWheelUpdate(connection, stateId, wheel, connection.Player.Guid);
        }
    }

    private static void SendWheelUpdate(GatewayConnection connection, int stateId, DailyWheelDefinition wheel, ulong playerGuid)
    {
        var spins = GetSpinsLeft(playerGuid, wheel);

        var secondsUntilTomorrow = (int)(DateTime.UtcNow.Date.AddDays(1) - DateTime.UtcNow).TotalSeconds;

        Send(connection, stateId, "OnWheelUpdateMsg", wheel.Id, spins, 1, spins > 0 ? 0 : secondsUntilTomorrow);
    }

    private static bool TryGetWheel(int wheelId, out DailyWheelDefinition wheel)
    {
        if (_resourceManager.DailyWheels.TryGetValue(wheelId, out wheel!))
            return true;

        _logger.LogWarning("Daily wheel: no DailyWheel.json entry for wheel {id}.", wheelId);
        return false;
    }

    // Weighted pick over the slices. Zero-weight slices are shown but never landed on.
    private static int RollSlot(DailyWheelDefinition wheel)
    {
        var total = wheel.Slots.Sum(x => Math.Max(0, x.Weight));

        if (total <= 0)
            return _rng.Next(wheel.Slots.Count);

        var roll = _rng.Next(total);

        for (var i = 0; i < wheel.Slots.Count; i++)
        {
            roll -= Math.Max(0, wheel.Slots[i].Weight);

            if (roll < 0)
                return i;
        }

        return wheel.Slots.Count - 1;
    }

    // One free spin per UTC calendar day, plus any bonus spins granted by "/wheel give" - both live on the
    // character row (the daily part is shared with the no-UI /spinwheel path).
    public static int GetSpinsLeft(ulong playerGuid, DailyWheelDefinition wheel)
    {
        using var dbContext = _dbContextFactory.CreateDbContext();

        var row = dbContext.Characters
            .Where(x => x.Id == GuidHelper.GetPlayerId(playerGuid))
            .Select(x => new { x.LastDailyWheelSpinUtc, x.DailyWheelBonusSpins })
            .SingleOrDefault();

        if (row is null)
            return 0;

        var usedToday = row.LastDailyWheelSpinUtc is { } last &&
                        last.UtcDateTime.Date == DateTimeOffset.UtcNow.UtcDateTime.Date;

        return (usedToday ? 0 : wheel.SpinsPerDay) + Math.Max(0, row.DailyWheelBonusSpins);
    }

    // Spends the free daily spin first, then a bonus spin.
    private static void ConsumeDailySpin(ulong playerGuid)
    {
        using var dbContext = _dbContextFactory.CreateDbContext();

        var dbCharacter = dbContext.Characters.SingleOrDefault(x => x.Id == GuidHelper.GetPlayerId(playerGuid));
        if (dbCharacter is null)
            return;

        var usedToday = dbCharacter.LastDailyWheelSpinUtc is { } last &&
                        last.UtcDateTime.Date == DateTimeOffset.UtcNow.UtcDateTime.Date;

        if (usedToday)
            dbCharacter.DailyWheelBonusSpins = Math.Max(0, dbCharacter.DailyWheelBonusSpins - 1);
        else
            dbCharacter.LastDailyWheelSpinUtc = DateTimeOffset.UtcNow;

        dbContext.SaveChanges();
    }

    // "/wheel rig <slot>": force every spin to land on one slice (-1 = back to the weighted roll), so the
    // slice the wheel visually stops on can be checked against the prize that slot is supposed to pay.
    public static int RiggedSlot = -1;

    // "/wheel give": hand a player extra spins (negative to take them away). Returns their new total.
    public static int GrantSpins(ulong playerGuid, int count)
    {
        using var dbContext = _dbContextFactory.CreateDbContext();

        var dbCharacter = dbContext.Characters.SingleOrDefault(x => x.Id == GuidHelper.GetPlayerId(playerGuid));
        if (dbCharacter is null)
            return 0;

        dbCharacter.DailyWheelBonusSpins = Math.Max(0, dbCharacter.DailyWheelBonusSpins + count);
        dbContext.SaveChanges();

        return dbCharacter.DailyWheelBonusSpins;
    }

    // The coin icon the widget uses for its own coin prize, and the client's "Coins" string.
    private const int CoinIconId = 4809;
    private const int CoinNameStringId = 4923;

    // The wheel's own extra-spins medallion: image set 4347 (Images.txt 20784/20785/20786 =
    // icon_wheel_reward_extra_spins at 128/32/64), and the client's "Extra Spin" string.
    private const int ExtraSpinIconId = 4347;
    private const int ExtraSpinNameStringId = 409187;

    // ---- Opening the widget ----

    // The client asks for the wheel (26/11) and we answer with this: load game_wheel into the MiniGameFlash
    // window. The name follows the client's own UI convention - every Flashanim in UI\UiModules\Main\*.xml
    // points at "UI\<name>.swf" and the client resolves that to the packed .gfx itself, so the raw asset
    // name ("game_wheel.gfx") is NOT what it wants.
    public static CommandPacketStartFlashGame CreateStartPacket() => new()
    {
        LuaClass = "MiniGameFlash",
        Swf = @"UI\game_wheel.swf"
    };

    // The wheel is a MICRO game (the client's own MiniGameTypeData.txt is the only type-22 row with
    // IS_MICRO=1), and MiniGameManager::StartFlashGame @0x009BD650 only takes its ShowMicro path when the
    // client already holds minigame data - `test ebp,ebp` on the current-game pointer, else it falls into a
    // degraded "%s:Show" call that loads nothing. So the state has to exist BEFORE the start packet.
    //
    // NOTE (live 2026-08-06): this state on its own is NOT enough - only the full activity launch
    // (StartingZone.LaunchSpinForTheWinGame) produced a working wheel. Kept because it is what the GO!
    // press and the client's own 26/11 request pair with.
    public static void SendWheelState(GatewayConnection connection)
    {
        var info = new MiniGameInfo
        {
            NameId = WheelNameId,
            IconId = WheelIconId,
            DescriptionId = WheelDescriptionId,
            Difficulty = 1,
            ProfileType = 0,
            Type = 22,                 // Wheel
            PreselectedGameId = WheelGameId,
            Unknown11 = true,
            Unknown13 = @"UI\game_wheel.swf"
        };

        connection.SendTunneled(new MiniGameInfoPacket(WheelGameId, -1, -1) { Info = info });
        connection.SendTunneled(new MiniGameGameStartPacket(WheelGameId, -1, -1));
    }

    // Publishes how many spins the player has left today as the "wheel" repeating activity. This is what
    // un-greys Spin For The Win's Play button in the minigames menu (the Browser's Lua gates it on
    // Ui.GetRepeatingActivityCount("wheel")), so it goes out at login and again after a spin is used.
    public static void SendSpinAvailability(GatewayConnection connection) =>
        SendSpinAvailability(connection.Player);

    public static void SendSpinAvailability(Player player)
    {
        var wheel = _resourceManager.DailyWheels.Values.OrderBy(x => x.Id).FirstOrDefault();
        if (wheel is null)
            return;

        var spins = GetSpinsLeft(player.Guid, wheel);

        player.SendTunneled(new RepeatingActivityAddPacket
        {
            Name = WheelActivityName,
            ActivityId = wheel.Id,
            Count = spins,
            NextTime = spins > 0 ? 0 : (int)(DateTime.UtcNow.Date.AddDays(1) - DateTime.UtcNow).TotalSeconds,
        });

        _logger.LogInformation("Daily wheel: {name} has {spins} spin(s) available.", player.Name, spins);
    }

    // The key the client's Lua looks the activity up by - Ui.GetRepeatingActivityCount("wheel").
    public const string WheelActivityName = "wheel";

    // ★ Opens the wheel — the sequence that is live-confirmed to work (2026-08-06):
    //
    //   1. this: the activity launch, which puts up the start panel AND is what makes the widget loadable
    //      at all (StartFlashGame's ShowMicro path needs the minigame state the launch creates),
    //   2. the player presses GO!, so the client runs its own load sequence and asks us to start
    //      (op39/sub5), and
    //   3. BaseMiniGamePacketHandler answers with the game-start ack + op26/sub12 StartFlashGame, the
    //      movie loads and connects itself (OnConnectMsg) to the payload conversation below.
    //
    // Sending step 3 ourselves does NOT skip the panel: fired immediately it lands before the client
    // reaches BeginLoad, and fired on a timer it consumes the start so the real GO! press does nothing.
    // Both were tried live and both leave a blank frame. Removing the panel therefore needs a different
    // lever (a MiniGameInfo/MiniGameGroupInfo flag, most likely) rather than a race against the client.
    public static void OpenWheel(GatewayConnection connection)
    {
        if (connection.Player.Zone is not Sanctuary.Game.Zones.StartingZone startingZone ||
            !_resourceManager.ClientActivityDefinitions.TryGetValue(WheelGameId, out var activityDefinition))
        {
            _logger.LogWarning("Daily wheel: can't open the wheel (not in the starting zone, or no activity {id}).",
                WheelGameId);
            return;
        }

        startingZone.LaunchSpinForTheWinGame(connection.Player, activityDefinition);
    }

    // The client's own MiniGameData.txt row: 8^22^409962^409969^20985^1^...
    private const int WheelGameId = 8;
    private const int WheelNameId = 409962;
    private const int WheelDescriptionId = 409969;
    private const int WheelIconId = 20985;

    // ---- Dev helpers (/wheel) ----

    // Clears today's spin lock so the wheel can be spun again without waiting for UTC midnight.
    public static void ResetDailySpin(ulong playerGuid)
    {
        using var dbContext = _dbContextFactory.CreateDbContext();

        var dbCharacter = dbContext.Characters.SingleOrDefault(x => x.Id == GuidHelper.GetPlayerId(playerGuid));
        if (dbCharacter is null)
            return;

        dbCharacter.LastDailyWheelSpinUtc = null;
        dbContext.SaveChanges();
    }


    // Pushes an extra wheel into the OPEN widget, painted with the given category art ids - the only way
    // to see which of the 25 mcCategory frames is which picture, since that mapping isn't in any data we
    // have. Each call appends another page (use the arrows either side of the wheel to reach it).
    public static void SendPreviewWheel(GatewayConnection connection, int type, int[] categories)
    {
        var wheel = _resourceManager.DailyWheels.Values.OrderBy(x => x.Id).FirstOrDefault();

        Send(connection, PreviewStateId, "OnWheelDataMsg",
            PreviewWheelId, type, string.Join(' ', categories),
            wheel?.NameStringId ?? 0, wheel?.MsgStringId ?? 0);

        Send(connection, PreviewStateId, "OnWheelUpdateMsg", PreviewWheelId, 1, 1, 0);
    }

    // Preview wheels are served under their own id so a spin on one can't be mistaken for the real wheel
    // (TryGetWheel finds no definition for it and the spin request is dropped).
    private const int PreviewWheelId = 9001;
    private const int PreviewStateId = 8;

    private static int Arg(string[] args, int index) =>
        args.Length > index && int.TryParse(args[index], out var value) ? value : 0;

    // Messages go out as one tab-delimited, null-terminated string in a MiniGamePayloadPacket - the exact
    // shape the widget's SoeNetworkTypeFreeRealms.OnData() splits back apart.
    private static void Send(GatewayConnection connection, int stateId, string name, params object[] args)
    {
        var message = args.Length > 0
            ? name + '\t' + string.Join('\t', args)
            : name;

        _logger.LogTrace("Daily wheel S2C: {message}", message);

        connection.SendTunneled(new MiniGamePayloadPacket
        {
            StateId = stateId,
            Payload = Encoding.UTF8.GetBytes(message + '\0')
        });
    }
}
