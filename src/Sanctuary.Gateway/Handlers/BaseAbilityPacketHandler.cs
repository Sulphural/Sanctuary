using System;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Sanctuary.Core.IO;
using Sanctuary.Packet;
using Sanctuary.Packet.Common.Attributes;

namespace Sanctuary.Gateway.Handlers;

[PacketHandler]
public static class BaseAbilityPacketHandler
{
    private static ILogger _logger = null!;

    // Boombox ability IDs — all get a 60-second cooldown definition.
    private static readonly System.Collections.Generic.HashSet<int> BoomboxAbilityIds = new()
    {
        255, 257, 361, 1013, 1018, 1027, 1037, 1052, 1057, 1062, 1862, 3761, 3971, 4370, 5087, 5161
    };

    public static void ConfigureServices(IServiceProvider serviceProvider)
    {
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        _logger = loggerFactory.CreateLogger(nameof(BaseAbilityPacketHandler));
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
            AbilityPacketClientRequestStartAbility.OpCode => AbilityPacketClientRequestStartAbilityHandler.HandlePacket(connection, reader.Span),
            AbilityPacketRequestAbilityDefinition.OpCode => HandleAbilityDefinitionRequest(connection, reader.Span),
            _ => false
        };
    }

    private static bool HandleAbilityDefinitionRequest(GatewayConnection connection, ReadOnlySpan<byte> data)
    {
        if (!AbilityPacketRequestAbilityDefinition.TryDeserialize(data, out var packet))
            return false;

        int cooldownMs = BoomboxAbilityIds.Contains(packet.AbilityId) ? 60_000 : 0;

        connection.SendTunneled(new AbilityPacketAbilityDefinition
        {
            AbilityId = packet.AbilityId,
            CooldownMs = cooldownMs,
            CastTimeMs = 0
        });

        return true;
    }
}
