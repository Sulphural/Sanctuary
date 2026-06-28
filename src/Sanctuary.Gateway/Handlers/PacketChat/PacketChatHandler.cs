using System;
using System.Linq;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Sanctuary.Game;
using Sanctuary.Gateway.Commands;
using Sanctuary.Packet;
using Sanctuary.Packet.Common.Attributes;
using Sanctuary.Packet.Common.Chat;

namespace Sanctuary.Gateway.Handlers;

[PacketHandler]
public static class PacketChatHandler
{
    private static ILogger _logger = null!;
    private static ILogger _chatLogger = null!;
    private static IZoneManager _zoneManager = null!;
    private static bool _commandRouterInitialized = false;

    public static void ConfigureServices(IServiceProvider serviceProvider)
    {
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        _logger = loggerFactory.CreateLogger(nameof(PacketChatHandler));
        _chatLogger = loggerFactory.CreateLogger("Chat");

        _zoneManager = serviceProvider.GetRequiredService<IZoneManager>();

        // Initialize CommandRouter
        if (!_commandRouterInitialized)
        {
            CommandRouter.Initialize(serviceProvider);
            _commandRouterInitialized = true;
        }
    }

    public static bool HandlePacket(GatewayConnection connection, ReadOnlySpan<byte> data)
    {
        if (!PacketChat.TryDeserialize(data, out var packet))
        {
            _logger.LogError("Failed to deserialize {packet}.", nameof(PacketChat));
            return false;
        }

        _logger.LogTrace("Received {name} packet. ( {packet} )", nameof(PacketChat), packet);
        _logger.LogInformation("Chat message from {Player}: {Message}", connection.Player.Name, packet.Message);

        // Check if this is a command
        if (CommandRouter.TryHandle(connection, packet.Message))
        {
            _logger.LogInformation("Command was handled by CommandRouter");
            return true; // Command was handled
        }

        packet.FromGuid = connection.Player.Guid;
        packet.FromName = connection.Player.Name;

        switch (packet.Channel)
        {
            case ChatChannel.Tell:
                {
                    if (_zoneManager.TryGetPlayer(packet.ToName.FullName, out var toPlayer))
                    {
                        _chatLogger.LogInformation("Tell|From: \"{FromName}\" ({FromGuid}), To: \"{ToName}\" ({ToGuid}), Msg: \"{Message}\"",
                            packet.FromName,
                            packet.FromGuid,
                            packet.ToName,
                            toPlayer.Guid,
                            packet.Message
                        );

                        if (!toPlayer.Ignores.Any(x => x.Guid == connection.Player.Guid))
                            toPlayer.SendTunneled(packet);

                        var tellEchoPacket = new TellEchoPacket();

                        tellEchoPacket.Name = packet.ToName;
                        tellEchoPacket.Message = packet.Message;

                        connection.Player.SendTunneled(tellEchoPacket);
                    }
                }
                break;

            case ChatChannel.WorldShout:
                {
                    _chatLogger.LogInformation("WorldShout|From: \"{FromName}\" ({FromGuid}), Msg: \"{Message}\"",
                        packet.FromName,
                        packet.FromGuid,
                        packet.Message
                    );

                    foreach (var zonePlayer in connection.Player.Zone.Players)
                    {
                        if (zonePlayer.Ignores.Any(x => x.Guid == connection.Player.Guid))
                            continue;

                        zonePlayer.SendTunneled(packet);
                    }
                }
                break;

            case ChatChannel.WorldTrade:
            case ChatChannel.WorldLfg:
            case ChatChannel.WorldArea:
            case ChatChannel.WorldMembersOnly:
                {
                    _chatLogger.LogInformation("{Channel}|Area: {AreaNameId}, From: \"{FromName}\" ({FromGuid}), Msg: \"{Message}\"",
                        packet.Channel,
                        packet.AreaNameId,
                        packet.FromName,
                        packet.FromGuid,
                        packet.Message
                    );

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
                    _chatLogger.LogInformation("{Channel}|From: \"{FromName}\" ({FromGuid}), Msg: \"{Message}\"",
                        packet.Channel,
                        packet.FromName,
                        packet.FromGuid,
                        packet.Message
                    );

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