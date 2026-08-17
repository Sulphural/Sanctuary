using System;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Sanctuary.Core.IO;
using Sanctuary.Packet;
using Sanctuary.Packet.Common.Attributes;

namespace Sanctuary.Gateway.Handlers;

[PacketHandler]
public static class BaseCommandPacketHandler
{
    private static ILogger _logger = null!;

    public static void ConfigureServices(IServiceProvider serviceProvider)
    {
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        _logger = loggerFactory.CreateLogger(nameof(BaseCommandPacketHandler));
    }

    public static bool HandlePacket(GatewayConnection connection, PacketReader reader)
    {
        if (!reader.TryRead(out short opCode))
        {
            _logger.LogError("Failed to read opcode from packet. ( Data: {data} )", Convert.ToHexString(reader.Span));
            return false;
        }

        return opCode switch
        {
            CommandPacketInteractRequest.OpCode => CommandPacketInteractRequestHandler.HandlePacket(connection, reader.Span),
            CommandPacketFreeInteractionNpc.OpCode => CommandPacketFreeInteractionNpcHandler.HandlePacket(connection, reader.Span),
            ClearInteractionMerchantSetId.OpCode => ClearInteractionMerchantSetIdHandler.HandlePacket(connection, reader.Span),
            CommandPacketInteractionSelect.OpCode => CommandPacketInteractionSelectHandler.HandlePacket(connection, reader.Span),
            CommandPacketSetProfile.OpCode => CommandPacketSetProfileHandler.HandlePacket(connection, reader.Span),
            CommandPacketAddFriendRequest.OpCode => CommandPacketAddFriendRequestHandler.HandlePacket(connection, reader.Span),
            CommandPacketRemoveFriendRequest.OpCode => CommandPacketRemoveFriendRequestHandler.HandlePacket(connection, reader.Span),
            CommandPacketConfirmFriendResponse.OpCode => CommandPacketConfirmFriendResponseHandler.HandlePacket(connection, reader.Span),
            CommandPacketSetChatBubbleColor.OpCode => CommandPacketSetChatBubbleColorHandler.HandlePacket(connection, reader.Span),
            CommandPacketSelectPlayer.OpCode => CommandPacketSelectPlayerHandler.HandlePacket(connection, reader.Span),
            CommandPacketFriendsPositionRequest.OpCode => CommandPacketFriendsPositionRequestHandler.HandlePacket(connection),
            CommandPacketIgnoreRequest.OpCode => CommandPacketIgnoreRequestHandler.HandlePacket(connection, reader.Span),
            CommandPacketChatChannelOn.OpCode => CommandPacketChatChannelOnHandler.HandlePacket(connection, reader.Span),
            CommandPacketChatChannelOff.OpCode => CommandPacketChatChannelOffHandler.HandlePacket(connection, reader.Span),
            23 => CommandPacketQuestAbandonHandler.HandlePacket(connection, reader.Span), // "Drop Quest" (journal)
            6 => HandleDialogResponse(connection, reader),                                 // 26/6 PacketDialogResponse
            11 => HandleStartWheel(connection),                                           // 26/11 InteractionStartWheel
            _ => LogUnhandled(opCode, reader)
        };
    }

    // The player clicked a response button on a CommandPacketShowDialog (26/3) NPC conversation. Wire-
    // confirmed: the client sends 26/6 (payload = int response Id). Respond with the proper NPC-dialog
    // teardown CommandPacketEndDialog (26/4 -> client FUN_008a7ce0 frees the native dialog object at
    // +0x654 and restores the camera via FUN_009f6890). NOT sub-opcode 29 (QuestDialogComplete): that
    // dispatches "QuestStartHandler:DismissEndScreen", which is for the quest END SCREEN - sending it
    // here hid the whole HUD and locked player movement (no end screen was open to dismiss).
    private static bool HandleDialogResponse(GatewayConnection connection, PacketReader reader)
    {
        // A dialog with more than one button cares WHICH was clicked, so read the response Id the client
        // echoes back (the body after the 26/6 header). Checked before the single-action path below,
        // because a multi-choice dialog owns the whole click.
        if (connection.Player.PendingDialogChoices is { } choices)
        {
            connection.Player.PendingDialogChoices = null;

            if (reader.TryRead(out int responseId) && choices.TryGetValue(responseId, out var choice))
                choice();

            // ★★ A MULTI-CHOICE DIALOG CAN RUN MORE THAN ONE TURN, and this used to make that impossible:
            // the teardown below went out unconditionally, so an answer that opened a FOLLOW-UP dialog had
            // it freed by the EndDialog arriving right behind it. The second panel of Trina Turtledove's
            // 12 Days introduction simply never appeared - the server sent it, the client built it, and the
            // teardown killed it in the same breath.
            //
            // A choice that continues the conversation installs the next set of buttons as it sends the
            // next panel, so a non-null value here means "the NPC is still talking". Same rule the quest
            // path below already follows via QuestDialogue.TryAdvance - this just gives the multi-choice
            // path the equivalent. A choice that ends the conversation installs nothing and still gets its
            // teardown.
            if (connection.Player.PendingDialogChoices is not null)
                return true;

            connection.Player.SendTunneled(new CommandPacketEndDialog());
            return true;
        }

        // A non-quest dialog (the treasure chest) owns its own button - let it consume the click first.
        if (connection.Player.PendingDialogAction is { } dialogAction)
        {
            connection.Player.PendingDialogAction = null;
            dialogAction();
            connection.Player.SendTunneled(new CommandPacketEndDialog());
            return true;
        }

        // A quest conversation can run several turns (NPC speaks -> player replies -> NPC speaks again).
        // While turns remain, the click advances the exchange instead of ending it - tearing the dialog
        // down here would cut the NPC off mid-conversation.
        if (Sanctuary.Game.Quests.QuestDialogue.TryAdvance(connection.Player))
            return true;

        connection.Player.SendTunneled(new CommandPacketEndDialog());
        return true;
    }

    // ★ 26/11 CommandPacketInteractionStartWheel (empty body) — the client's own "open the daily wheel"
    // request, sent by the native MiniGameFlashC:StartWheel once the player holds a "wheel" repeating
    // activity spin. Answer with the Flash-game start naming the widget (same packet Mining Practice uses,
    // just a different movie); the wheel then talks to us over the minigame payload channel — see
    // DailyWheelGame.
    private static bool HandleStartWheel(GatewayConnection connection)
    {
        _logger.LogInformation("Daily wheel: client asked to start the wheel (26/11) — sending game_wheel.gfx.");

        DailyWheelGame.OpenWheel(connection);

        return true;
    }

    // INSTANCE WIP: observe-log unmapped command sub-opcodes so we can see what the offer popup requests when it
    // opens — e.g. CommandPacketRequestRewardPreviewUpdate (sub37, the "Prizes" loader the spinner waits on).
    private static bool LogUnhandled(short subOpCode, PacketReader reader)
    {
        _logger.LogInformation("BaseCommandPacket UNHANDLED sub-opcode={sub} | remaining bytes={hex}",
            subOpCode, Convert.ToHexString(reader.Span));
        return true;
    }
}