using System.Linq;

using Sanctuary.Game;
using Sanctuary.Game.ChatCommands;
using Sanctuary.Game.Entities;
using Sanctuary.Gateway.Handlers;
using Sanctuary.Packet;
using Sanctuary.Packet.Common.Chat;

namespace Sanctuary.Gateway.ChatCommands;

// "Spin For The Win!" daily wheel - opening it, handing out spins, and the calibration switches used to
// line the widget's slices up with what they pay. See DailyWheelGame for the protocol.
public class WheelChatCommand : GatewayChatCommand
{
    private readonly IZoneManager _zoneManager;
    private readonly IResourceManager _resourceManager;

    public WheelChatCommand(GatewayServer server, IZoneManager zoneManager, IResourceManager resourceManager)
        : base(server)
    {
        _zoneManager = zoneManager;
        _resourceManager = resourceManager;
    }

    public override string KeyWord => "wheel";
    public override string Usage => "[go | give <count> [player] | reset | rig <slot> | slots <cat...> | flash [swf] | add <count> | state <count>]";
    public override string Description => "Opens the daily wheel, or hands out and calibrates its spins.";
    public override ChatCommandRole RequiredRole => ChatCommandRole.Admin;

    public override bool Handle(Player invoker, string[] args)
    {
        var connection = GetConnection(invoker);
        if (connection is null)
            return true;

        var sub = args.Length > 0 ? args[0].ToLowerInvariant() : "";

        switch (sub)
        {
            case "give":
                return HandleGive(invoker, args);

            case "reset":
                DailyWheelGame.ResetDailySpin(invoker.Guid);
                DailyWheelGame.SendSpinAvailability(invoker);
                Reply(invoker, "Daily spin lock cleared, Play button re-enabled.");
                return true;

            case "rig":
                return HandleRig(invoker, args);

            case "add":
            case "state":
                return HandleRepeatingActivity(connection, invoker, args, sub);

            case "slots":
                return HandleSlots(connection, invoker, args);

            case "flash":
                return HandleFlash(connection, invoker, args);

            case "minigame":
                // Just the activity launch, without the start that follows it - for telling the two halves
                // of the open sequence apart.
                if (invoker.Zone is Sanctuary.Game.Zones.StartingZone startingZone &&
                    _resourceManager.ClientActivityDefinitions.TryGetValue(8, out var activityDefinition))
                {
                    startingZone.LaunchSpinForTheWinGame(invoker, activityDefinition);
                    Reply(invoker, "Sent the minigame launch for activity 8; press GO! on the panel.");
                }
                else
                {
                    Reply(invoker, "You must be in the starting zone to use this.");
                }
                return true;

            case "":
            case "go":
                connection.SendTunneled(new RepeatingActivityAddPacket
                {
                    ActivityId = 1,
                    Count = 1,
                    Name = DailyWheelGame.WheelActivityName
                });

                DailyWheelGame.OpenWheel(connection);

                Reply(invoker, "Spin granted and the wheel launched - press GO! on the panel.");
                return true;

            default:
                return false;
        }
    }

    // Extra spins for a player (negative takes them away). They persist on the character row.
    private bool HandleGive(Player invoker, string[] args)
    {
        var count = args.Length > 1 && int.TryParse(args[1], out var n) ? n : 1;

        var target = invoker;

        if (args.Length > 2)
        {
            var pattern = string.Join(' ', args, 2, args.Length - 2);

            if (!_zoneManager.TryGetPlayer(pattern, out var resolved))
            {
                Reply(invoker, $"Player '{pattern}' not found.");
                return true;
            }

            target = resolved;
        }

        var total = DailyWheelGame.GrantSpins(target.Guid, count);

        // Push the new count so their Play button updates without a relog.
        DailyWheelGame.SendSpinAvailability(target);

        Reply(invoker, $"{target.Name.FullName} now has {total} bonus spin(s) on top of today's free one.");

        if (!ReferenceEquals(target, invoker))
            target.SendTunneled(new ChatPacketDebugChat
            {
                PrintToChat = true,
                Message = $"You've been given {count} daily wheel spin(s)!",
            });

        return true;
    }

    // Force every spin onto one slice, for checking that the slice the wheel stops on is the one that pays.
    private bool HandleRig(Player invoker, string[] args)
    {
        DailyWheelGame.RiggedSlot = args.Length > 1 && int.TryParse(args[1], out var slot) ? slot : -1;

        if (DailyWheelGame.RiggedSlot < 0)
        {
            Reply(invoker, "Back to the weighted random roll.");
            return true;
        }

        var wheel = _resourceManager.DailyWheels.Values.OrderBy(x => x.Id).FirstOrDefault();
        var rigged = wheel is not null && DailyWheelGame.RiggedSlot < wheel.Slots.Count
            ? wheel.Slots[DailyWheelGame.RiggedSlot]
            : null;

        Reply(invoker, $"Every spin lands on slot {DailyWheelGame.RiggedSlot}" +
                       (rigged is null ? "." : $": art {rigged.Category}, pays {rigged.Comment}.") +
                       " Spin and check the slice it stops on against that.");
        return true;
    }

    // The repeating-activity grant on its own - this is what un-greys the minigames-menu Play button.
    private bool HandleRepeatingActivity(GatewayConnection connection, Player invoker, string[] args, string sub)
    {
        int Arg(int index, int fallback) =>
            args.Length > index && int.TryParse(args[index], out var value) ? value : fallback;

        var count = Arg(1, 1);
        var id = Arg(2, 1);
        var consecutive = Arg(3, 0);
        var nextTime = Arg(4, 0);
        var unknown = Arg(5, 0);
        var name = args.Length > 6 ? args[6] : DailyWheelGame.WheelActivityName;

        if (sub == "add")
            connection.SendTunneled(new RepeatingActivityAddPacket
            {
                ActivityId = id,
                Count = count,
                Consecutive = consecutive,
                NextTime = nextTime,
                Unknown = unknown,
                Name = name,
            });
        else
            connection.SendTunneled(new RepeatingActivityStatePacket
            {
                ActivityId = id,
                Count = count,
                Consecutive = consecutive,
                NextTime = nextTime,
                Unknown = unknown,
            });

        Reply(invoker, $"op143/{(sub == "add" ? 1 : 2)} \"{name}\" id={id} count={count} " +
                       $"consecutive={consecutive} nextTime={nextTime} unknown={unknown}.");
        return true;
    }

    // Appends a preview wheel painted with the given slice art ids, for mapping art to numbers.
    private bool HandleSlots(GatewayConnection connection, Player invoker, string[] args)
    {
        var categories = args.Skip(1)
            .Select(x => int.TryParse(x, out var value) ? value : 0)
            .Where(x => x > 0)
            .ToArray();

        if (categories.Length == 0)
            categories = Enumerable.Range(1, 10).ToArray();

        DailyWheelGame.SendPreviewWheel(connection, 1, categories);

        Reply(invoker, $"Preview wheel added with categories [{string.Join(' ', categories)}]. " +
                       "Use the arrows beside the wheel to page to it.");
        return true;
    }

    // The Flash-game start on its own, with an overridable movie / Lua window class.
    private bool HandleFlash(GatewayConnection connection, Player invoker, string[] args)
    {
        var packet = DailyWheelGame.CreateStartPacket();

        if (args.Length > 1) packet.Swf = args[1];
        if (args.Length > 2) packet.LuaClass = args[2];
        packet.Unknown = args.Length > 3 && args[3] != "0";

        connection.SendTunneled(packet);

        Reply(invoker, $"26/12 StartFlashGame(\"{packet.LuaClass}\", \"{packet.Swf}\", {packet.Unknown}).");
        return true;
    }
}
