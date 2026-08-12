using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Sanctuary.Core.Helpers;
using Sanctuary.Database;
using Sanctuary.Database.Entities;
using Sanctuary.Game;
using Sanctuary.Game.Entities;
using Sanctuary.Game.Resources.Definitions;
using Sanctuary.Packet;
using Sanctuary.Packet.Common.Attributes;

namespace Sanctuary.Gateway.Handlers;

// "Spin For The Win!" - the daily prize wheel. Drives the client's own game_wheel.gfx widget, which
// StartingZone.LaunchSpinForTheWinGame opens as minigame Type=22.
//
// It's a SOE "microgame", so it talks over the payload channel (op39/sub14) in tab-delimited text rather
// than its own opcodes:
//   S2C  OnWheelDataMsg(id, type, slots, nameId, msgId) | OnWheelUpdateMsg(id, spins, streak, nextSpin)
//        OnServerReadyMsg() | OnWheelChangedMsg(id) | OnSpinInfoMsg(slot) | OnRewardInfoMsg(icon, tooltip,
//        quantity, name, msg, tint)
//   C2S  OnConnectMsg() | OnChangeWheelRequestMsg(id) | OnWheelSpinRequestMsg(id) | OnWheelStopMsg(id)
//
// The spin is theater: we pick the slice up front (weighted, from DailyWheel.json) and the widget animates
// to it. [PacketHandler] is only here for ConfigureServices - messages arrive via
// MiniGamePayloadPacketHandler.
[PacketHandler]
public static class DailyWheelGame
{
    private static ILogger _logger = null!;
    private static IResourceManager _resourceManager = null!;
    private static IDbContextFactory<DatabaseContext> _dbContextFactory = null!;

    // Random.Shared is thread-safe; a shared `new Random()` isn't, and tearing it pins Next() to 0.
    private static Random _rng => Random.Shared;

    // What each slice's pool last gave a player, so it can't hand out the same thing twice running.
    private static readonly Dictionary<(ulong Player, int Wheel, int Slot, PrizePool Kind), int> _lastPrize = [];

    private enum PrizePool
    {
        Items,
        Coins,
        Spins
    }

    // Picks from a slice's pool, re-rolling over the other entries if it lands on last time's prize.
    private static int RollIndex(ulong playerGuid, int wheelId, int slotIndex, PrizePool kind, int count)
    {
        if (count <= 1)
            return 0;

        var pick = _rng.Next(count);

        var key = (playerGuid, wheelId, slotIndex, kind);

        if (_lastPrize.TryGetValue(key, out var previous) && pick == previous)
            pick = (previous + 1 + _rng.Next(count - 1)) % count;   // uniform over the OTHER entries

        _lastPrize[key] = pick;

        return pick;
    }

    public static void ConfigureServices(IServiceProvider serviceProvider)
    {
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        _logger = loggerFactory.CreateLogger(nameof(DailyWheelGame));

        _resourceManager = serviceProvider.GetRequiredService<IResourceManager>();
        _dbContextFactory = serviceProvider.GetRequiredService<IDbContextFactory<DatabaseContext>>();
    }

    // Routed here from MiniGamePayloadPacketHandler. Returns false for messages we don't handle.
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

    // Pick the winning slice and tell the wheel where to stop. Nothing is granted until OnWheelStopMsg,
    // so the payout lands with the animation.
    private static bool HandleSpinRequest(GatewayConnection connection, int stateId, int wheelId)
    {
        var player = connection.Player;

        if (!TryGetWheel(wheelId, out var wheel))
            return true;

        if (GetSpinsLeft(player.Guid, wheel) <= 0)
        {
            _logger.LogInformation("Daily wheel: {name} asked to spin with no spins left - ignoring.", player.Name);
            return true;
        }

        var slot = RiggedSlot >= 0 && RiggedSlot < wheel.Slots.Count ? RiggedSlot : RollSlot(wheel);

        player.PendingDailyWheelSlot = slot;
        player.PendingDailyWheelId = wheel.Id;

        // Absolute slice index, not a relative advance - Spin() zeroes the rotation first, so every spin
        // starts from slice 0.
        _logger.LogInformation("Daily wheel: {name} spins wheel {wheel} -> slot {slot} (art {cat}, {prize}).",
            player.Name, wheel.Id, slot, wheel.Slots[slot].Category, wheel.Slots[slot].Comment);

        Send(connection, stateId, "OnSpinInfoMsg", slot);

        return true;
    }

    // Spin finished: consume the daily spin and pay out. PayOut is separate so the grab bag can repeat it.
    private static bool HandleSpinStopped(GatewayConnection connection, int stateId, int wheelId)
    {
        var player = connection.Player;

        var slotIndex = player.PendingDailyWheelSlot;

        player.PendingDailyWheelSlot = -1;
        player.PendingDailyWheelId = 0;

        if (slotIndex < 0 || !TryGetWheel(wheelId, out var wheel) || slotIndex >= wheel.Slots.Count)
        {
            _logger.LogInformation("Daily wheel: stop with no pending spin - ignoring.");
            return true;
        }

        var slot = wheel.Slots[slotIndex];

        ConsumeDailySpin(connection, wheel.Id);

        PayOut(connection, stateId, wheel, slotIndex);

        // The grab bag: this many further slices are rolled and paid alongside it, each with its own
        // reward window (the widget shows them one after another as the player dismisses each).
        for (var i = 0; i < slot.GrabBagPrizes; i++)
        {
            var extraIndex = RollSlot(wheel);

            if (extraIndex == slotIndex)
                extraIndex = (extraIndex + 1) % wheel.Slots.Count;

            _logger.LogInformation("Daily wheel: grab bag also paying slot {slot} ({prize}).",
                extraIndex, wheel.Slots[extraIndex].Comment);

            PayOut(connection, stateId, wheel, extraIndex);
        }

        // Grey the wheel out for the rest of the day, in the widget and in the minigames menu.
        SendWheelUpdate(connection, stateId, wheel, player.Guid);
        SendSpinAvailability(connection);

        _logger.LogInformation("Daily wheel: {name} won {prize} (slot {slot}).", player.Name, slot.Comment, slotIndex);

        MoveToNextSpinnableWheel(connection, stateId, wheel);

        return true;
    }

    // This wheel is spent, so page the widget on to the next one the player can still spin. Without this
    // they'd be left looking at a dead wheel and have to find the arrows themselves.
    private static void MoveToNextSpinnableWheel(GatewayConnection connection, int stateId,
        DailyWheelDefinition current)
    {
        if (GetSpinsLeft(connection.Player.Guid, current) > 0)
            return;

        var wheels = InSeasonWheels().ToList();

        var from = wheels.FindIndex(x => x.Id == current.Id);

        // Walk forward from the current wheel so it wraps round to the ones before it.
        for (var i = 1; i < wheels.Count; i++)
        {
            var next = wheels[(from + i) % wheels.Count];

            if (GetSpinsLeft(connection.Player.Guid, next) <= 0)
                continue;

            Send(connection, stateId, "OnWheelChangedMsg", next.Id);

            _logger.LogInformation("Daily wheel: {name} is out of spins on wheel {from}, moving to {to}.",
                connection.Player.Name, current.Id, next.Id);

            return;
        }
    }

    // Grants one slice's prize and shows its "Congratulations! You won" window.
    private static void PayOut(GatewayConnection connection, int stateId, DailyWheelDefinition wheel, int index)
    {
        var player = connection.Player;
        var slot = wheel.Slots[index];

        var iconId = slot.IconId;
        var nameId = slot.NameStringId;
        var tooltipId = slot.TooltipId;
        var tintId = slot.TintId;
        var quantity = slot.Spins > 0 ? slot.Spins
            : slot.Coins > 0 ? slot.Coins
            : slot.Quantity;

        if (slot.Spins > 0 || slot.SpinAmounts.Count > 0)
        {
            // The wheel's own "extra spins" medallion: hand back more spins instead of an item.
            var spins = slot.SpinAmounts.Count > 0
                ? slot.SpinAmounts[RollIndex(player.Guid, wheel.Id, index, PrizePool.Spins, slot.SpinAmounts.Count)]
                : slot.Spins;

            GrantSpins(player.Guid, wheel.Id, spins);

            quantity = spins;

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
                var rolled = slot.CoinAmounts[
                    RollIndex(player.Guid, wheel.Id, index, PrizePool.Coins, slot.CoinAmounts.Count)];

                coins = rolled.Coins;

                if (rolled.NameStringId != 0)
                    nameId = rolled.NameStringId;

                _logger.LogInformation("Daily wheel: slot {slot} rolled {coins} coins from {count} amount(s).",
                    index, coins, slot.CoinAmounts.Count);
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
                ? slot.ItemIds[RollIndex(player.Guid, wheel.Id, index, PrizePool.Items, slot.ItemIds.Count)]
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
                index, itemId, Math.Max(1, slot.ItemIds.Count));
        }

        // A zero message id leaves the movie's authored placeholder text ("test") in the reward window, so
        // fall back to the wheel's default line.
        var rewardMsgStringId = slot.RewardMsgStringId != 0 ? slot.RewardMsgStringId : wheel.RewardMsgStringId;

        Send(connection, stateId, "OnRewardInfoMsg",
            iconId, tooltipId, quantity, nameId, rewardMsgStringId, tintId);
    }

    // One OnWheelDataMsg + OnWheelUpdateMsg per wheel, sent before OnServerReadyMsg.
    private static void SendWheels(GatewayConnection connection, int stateId)
    {
        // Only the wheels running today: the everyday one plus whatever seasonal wheels are in their
        // window. The widget pages between however many it is sent, with the arrows either side.
        foreach (var wheel in InSeasonWheels())
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

        Send(connection, stateId, "OnWheelUpdateMsg", wheel.Id, spins, GetStreak(playerGuid, wheel.Id),
            spins > 0 ? 0 : secondsUntilTomorrow);
    }

    // Wheels whose season covers today, everyday ones first. "/wheel season all" lifts the date check so
    // the seasonal wheels can be looked at out of season.
    private static IEnumerable<DailyWheelDefinition> InSeasonWheels() =>
        _resourceManager.DailyWheels.Values
            .Where(x => IgnoreSeasons || x.IsInSeason(DateTime.UtcNow))
            .OrderBy(x => x.Id);

    // Set by "/wheel season all": send every wheel whatever the date.
    public static bool IgnoreSeasons;

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

    // One free spin per wheel per UTC day, plus that wheel's bonus spins. Each wheel keeps its own row,
    // so spinning one doesn't use up another.
    public static int GetSpinsLeft(ulong playerGuid, DailyWheelDefinition wheel)
    {
        using var dbContext = _dbContextFactory.CreateDbContext();

        var row = FindRow(dbContext, playerGuid, wheel.Id);

        if (row is null)
            return wheel.SpinsPerDay;

        var usedToday = row.LastSpinUtc is { } last &&
                        last.UtcDateTime.Date == DateTimeOffset.UtcNow.UtcDateTime.Date;

        return (usedToday ? 0 : wheel.SpinsPerDay) + Math.Max(0, row.BonusSpins);
    }

    private static DbCharacterDailyWheel? FindRow(DatabaseContext dbContext, ulong playerGuid, int wheelId)
    {
        var characterId = GuidHelper.GetPlayerId(playerGuid);

        return dbContext.CharacterDailyWheels
            .SingleOrDefault(x => x.CharacterId == characterId && x.WheelId == wheelId);
    }

    // The row for this wheel, created on first use.
    private static DbCharacterDailyWheel GetOrAddRow(DatabaseContext dbContext, ulong playerGuid, int wheelId)
    {
        var row = FindRow(dbContext, playerGuid, wheelId);

        if (row is null)
        {
            row = new DbCharacterDailyWheel
            {
                CharacterId = GuidHelper.GetPlayerId(playerGuid),
                WheelId = wheelId
            };

            dbContext.CharacterDailyWheels.Add(row);
        }

        return row;
    }

    // Spends this wheel's free spin first, then one of its bonus spins. Taking the free one moves that
    // wheel's streak on and pays its milestones.
    private static void ConsumeDailySpin(GatewayConnection connection, int wheelId)
    {
        using var dbContext = _dbContextFactory.CreateDbContext();

        var row = GetOrAddRow(dbContext, connection.Player.Guid, wheelId);

        var today = DateTimeOffset.UtcNow.UtcDateTime.Date;
        var lastSpinDay = row.LastSpinUtc?.UtcDateTime.Date;

        if (lastSpinDay == today)
        {
            // The day's free spin is gone, so this was a bonus spin - the streak doesn't move.
            row.BonusSpins = Math.Max(0, row.BonusSpins - 1);
            dbContext.SaveChanges();
            return;
        }

        row.LastSpinUtc = DateTimeOffset.UtcNow;

        // Spinning on consecutive days builds the streak; any gap starts it over.
        row.Streak = lastSpinDay == today.AddDays(-1) ? row.Streak + 1 : 1;

        // Retail's milestones: a bonus spin on the third day running, two on the seventh. After the
        // seventh the streak restarts, so the run of bonuses repeats week on week.
        var bonus = row.Streak switch
        {
            StreakSmallBonusDay => 1,
            StreakLargeBonusDay => 2,
            _ => 0
        };

        if (bonus > 0)
        {
            row.BonusSpins += bonus;

            connection.SendTunneled(new ChatPacketDebugChat
            {
                PrintToChat = true,
                Message = $"{row.Streak} days in a row - here " +
                          (bonus == 1 ? "is an extra wheel spin!" : $"are {bonus} extra wheel spins!"),
            });

            _logger.LogInformation("Daily wheel: {name} hit a {days}-day streak on wheel {wheel}, +{bonus} spin(s).",
                connection.Player.Name, row.Streak, wheelId, bonus);
        }

        if (row.Streak >= StreakLargeBonusDay)
            row.Streak = 0;

        dbContext.SaveChanges();
    }

    // How many days running this wheel has been spun, which the widget is told about.
    private static int GetStreak(ulong playerGuid, int wheelId)
    {
        using var dbContext = _dbContextFactory.CreateDbContext();

        return FindRow(dbContext, playerGuid, wheelId)?.Streak ?? 0;
    }

    // "/wheel streak": sets the counter on every wheel so the 3- and 7-day bonuses can be tested without
    // waiting a week.
    public static void SetStreak(ulong playerGuid, int days)
    {
        using var dbContext = _dbContextFactory.CreateDbContext();

        foreach (var wheel in InSeasonWheels())
        {
            var row = GetOrAddRow(dbContext, playerGuid, wheel.Id);

            row.Streak = Math.Max(0, days);

            // The streak only advances when the day's FREE spin is taken, so push the last spin back a
            // day - otherwise a streak set today can't be continued until tomorrow.
            row.LastSpinUtc = days > 0 ? DateTimeOffset.UtcNow.AddDays(-1) : null;
        }

        dbContext.SaveChanges();
    }

    private const int StreakSmallBonusDay = 3;
    private const int StreakLargeBonusDay = 7;

    // "/wheel rig <slot>": force every spin to land on one slice (-1 = back to the weighted roll), so the
    // slice the wheel visually stops on can be checked against the prize that slot is supposed to pay.
    public static int RiggedSlot = -1;

    // Extra spins on one wheel (negative takes them away). Returns the new bonus total for that wheel.
    public static int GrantSpins(ulong playerGuid, int wheelId, int count)
    {
        using var dbContext = _dbContextFactory.CreateDbContext();

        var row = GetOrAddRow(dbContext, playerGuid, wheelId);

        row.BonusSpins = Math.Max(0, row.BonusSpins + count);
        dbContext.SaveChanges();

        return row.BonusSpins;
    }

    // "/wheel give": the same, across every wheel running today.
    public static void GrantSpinsOnAllWheels(ulong playerGuid, int count)
    {
        using var dbContext = _dbContextFactory.CreateDbContext();

        foreach (var wheel in InSeasonWheels())
        {
            var row = GetOrAddRow(dbContext, playerGuid, wheel.Id);
            row.BonusSpins = Math.Max(0, row.BonusSpins + count);
        }

        dbContext.SaveChanges();
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
        var wheel = InSeasonWheels().FirstOrDefault();
        if (wheel is null)
            return;

        // Every wheel has its own spins, but this one count drives the Play button - so it's the total.
        var spins = InSeasonWheels().Sum(x => GetSpinsLeft(player.Guid, x));

        player.SendTunneled(new RepeatingActivityAddPacket
        {
            Name = WheelActivityName,
            ActivityId = wheel.Id,
            Count = spins,
            NextTime = spins > 0 ? 0 : (int)(DateTime.UtcNow.Date.AddDays(1) - DateTime.UtcNow).TotalSeconds,
        });

        _logger.LogInformation("Daily wheel: {name} has {spins} spin(s) available across all wheels.",
            player.Name, spins);
    }

    // The key the client's Lua looks the activity up by - Ui.GetRepeatingActivityCount("wheel").
    public const string WheelActivityName = "wheel";

    // There's no "open the wheel at login" packet. op171 PacketClientNotifyCoinSpinAvailable looks like
    // one and isn't: the client builds that packet itself, and sending it does nothing (tested with the
    // grant in place and spins available). See the packet class for the details. The welcome screen gets
    // a What's New tile instead - StartingZone.SendWelcomeAnnouncements.

    // Opens the wheel through the minigame launch. That brings the start panel and framed window with it,
    // which retail's wheel didn't have, but it's the only way in - the client's own StartWheel path needs
    // state only the launch creates.
    public static void OpenWheel(GatewayConnection connection)
    {
        if (connection.Player.Zone is not Sanctuary.Game.Zones.StartingZone startingZone ||
            !_resourceManager.ClientActivityDefinitions.TryGetValue(WheelGameId, out var activityDefinition))
        {
            _logger.LogWarning("Daily wheel: can't open the wheel (not in the starting zone, or no activity {id}).",
                WheelGameId);
            return;
        }

        // Keep the client's spin count current first - the widget reads it as soon as it loads.
        SendSpinAvailability(connection.Player);

        startingZone.LaunchSpinForTheWinGame(connection.Player, activityDefinition);
    }

    // The client's own MiniGameData.txt row: 8^22^409962^409969^20985^1^...
    private const int WheelGameId = 8;
    private const int WheelNameId = 409962;
    private const int WheelDescriptionId = 409969;
    private const int WheelIconId = 20985;

    // ---- Dev helpers (/wheel) ----

    // Clears today's spin lock on every wheel, so they can be spun again without waiting for UTC midnight.
    public static void ResetDailySpin(ulong playerGuid)
    {
        using var dbContext = _dbContextFactory.CreateDbContext();

        foreach (var row in dbContext.CharacterDailyWheels
                     .Where(x => x.CharacterId == GuidHelper.GetPlayerId(playerGuid)))
        {
            row.LastSpinUtc = null;
        }

        dbContext.SaveChanges();
    }


    // Pushes an extra wheel into the OPEN widget, painted with the given category art ids - the only way
    // to see which of the 25 mcCategory frames is which picture, since that mapping isn't in any data we
    // have. Each call appends another page (use the arrows either side of the wheel to reach it).
    public static void SendPreviewWheel(GatewayConnection connection, int type, int[] categories)
    {
        var wheel = InSeasonWheels().FirstOrDefault();

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
