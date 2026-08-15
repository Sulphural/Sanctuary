using System;
using System.Linq;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Sanctuary.Game;
using Sanctuary.Game.Quests;
using Sanctuary.Packet;
using Sanctuary.Packet.Common.Attributes;
using Sanctuary.Packet.Common.Chat;

namespace Sanctuary.Gateway.Handlers;

[PacketHandler]
public static class QuickChatSendChatToChannelPacketHandler
{
    private static ILogger _logger = null!;
    private static IQuestManager _questManager = null!;

    public static void ConfigureServices(IServiceProvider serviceProvider)
    {
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        _logger = loggerFactory.CreateLogger(nameof(QuickChatSendChatToChannelPacketHandler));
        _questManager = serviceProvider.GetRequiredService<IQuestManager>();
    }

    public static bool HandlePacket(GatewayConnection connection, ReadOnlySpan<byte> data)
    {
        if (!QuickChatSendChatToChannelPacket.TryDeserialize(data, out var packet))
        {
            _logger.LogError("Failed to deserialize {packet}.", nameof(QuickChatSendChatToChannelPacket));
            return false;
        }

        _logger.LogTrace("Received {name} packet. ( {packet} )", nameof(QuickChatSendChatToChannelPacket), packet);

        // Emotes come through here, not through any emote packet: the client's EmoteHandler binds every
        // /emote to Ui.ProcessQuickChatCommand. /scare (219) is what a trick-or-treat target reacts to.
        _questManager.OnQuickChatEmote(connection.Player, packet.Id);

        packet.Guid = connection.Player.Guid;
        packet.Name = connection.Player.Name;

        switch (packet.Channel)
        {
            case ChatChannel.WorldTrade:
            case ChatChannel.WorldLfg:
            case ChatChannel.WorldArea:
            case ChatChannel.WorldMembersOnly:
                {
                    connection.Player.SendTunneled(packet);

                    foreach (var visiblePlayer in connection.Player.VisiblePlayers)
                    {
                        if (visiblePlayer.Value.ChatChannelStatus.TryGetValue(packet.Channel, out var channelStatus) && !channelStatus)
                            continue;

                        if (visiblePlayer.Value.Ignores.Any(x => x.Guid == connection.Player.Guid))
                            continue;

                        visiblePlayer.Value.SendTunneled(packet);
                    }
                }
                break;

            default:
                {
                    connection.Player.SendTunneled(packet);

                    foreach (var visiblePlayer in connection.Player.VisiblePlayers)
                    {
                        if (visiblePlayer.Value.Ignores.Any(x => x.Guid == connection.Player.Guid))
                            continue;

                        visiblePlayer.Value.SendTunneled(packet);
                    }
                }
                break;
        }

        return true;
    }
}