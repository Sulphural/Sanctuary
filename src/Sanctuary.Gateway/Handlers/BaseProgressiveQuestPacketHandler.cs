using System;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Sanctuary.Core.IO;
using Sanctuary.Game.Zones;
using Sanctuary.Packet.Common.Attributes;

namespace Sanctuary.Gateway.Handlers;

// C2S dispatcher for op207 ProgressiveQuest - the buttons on the 12 Days of Presents browser.
//
// ★ THE CLIENT'S OWN DISPATCHER IGNORES 2/3/4 (see the jump table at 0x00bdeaa0, where those three entries
// point at the bail stub) - which is exactly right, because they are the SEND direction. Their layouts come
// from the packet constructors rather than from a receive handler:
//   sub 2 RequestStartQuest  ctor 0x00c144f0, `ret 8`  -> body = 2 x int32 (quest id, slot id)
//   sub 3 RequestClaimSlot   ctor 0x00c145b0, same shape
//   sub 4 RequestClaimPrize  ctor 0x00c14670, same shape
// The base ctor stores the sub-opcode at +8 and each of these writes its two args at +0x0c/+0x10, so the
// wire is `[int16 207][int32 sub][int32 questId][int32 slotId]`.
//
// What each one means, from the panel: "Start Quest" begins that day's challenge, "Open Present" claims a
// finished day's present, and the bottom row's Big Presents claim through ClaimPrize. Handled here by
// acknowledging with a fresh ClientData so the panel redraws from server state - the same shape the rest of
// this codebase uses for UI that must not drift from the server.
[PacketHandler]
public static class BaseProgressiveQuestPacketHandler
{
    public const short OpCode = 207;

    private const int RequestStartQuest = 2;
    private const int RequestClaimSlot = 3;
    private const int RequestClaimPrize = 4;

    private static ILogger _logger = null!;

    public static void ConfigureServices(IServiceProvider serviceProvider)
    {
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        _logger = loggerFactory.CreateLogger(nameof(BaseProgressiveQuestPacketHandler));
    }

    public static bool HandlePacket(GatewayConnection connection, PacketReader reader)
    {
        // ★ AN INT SUB-OPCODE, not the byte/short the other families use - see the base header reader
        // 0x00bdddd0, which reads [int16 opcode][int32 subOpcode].
        if (!reader.TryRead(out int subOpCode))
        {
            _logger.LogError("Failed to read ProgressiveQuest sub-opcode. ( Data: {data} )",
                Convert.ToHexString(reader.Span));
            return false;
        }

        reader.TryRead(out int questId);
        reader.TryRead(out int slotId);

        _logger.LogInformation("ProgressiveQuest sub={sub} questId={quest} slotId={slot} from {name}.",
            subOpCode, questId, slotId, connection.Player.Name?.FullName);

        var startingZone = connection.Player.Zone as StartingZone;

        switch (subOpCode)
        {
            // "Open Present" - raise the reward popup (op207/7), which is the client's shared
            // UnifiedMessageWindow:ShowItemPanel, then redraw the grid behind it.
            case RequestClaimSlot:
                startingZone?.ClaimTwelveDaysPresent(connection.Player, slotId);
                return true;

            case RequestStartQuest:
            case RequestClaimPrize:
                // The 12 Days chain itself is not wired yet (most days need minigames this server does not
                // have - see StartingZone.TrinaTurtledove), so there is nothing to advance. Redrawing from
                // server state is the honest response: the panel stays truthful instead of showing a
                // client-side guess at what the click did.
                startingZone?.ResendTwelveDaysState(connection.Player);
                return true;

            default:
                return true; // observe-only for the rest of the family
        }
    }
}
