using System;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Sanctuary.Packet;
using Sanctuary.Packet.Common.Attributes;

namespace Sanctuary.Gateway.Handlers;

[PacketHandler]
public static class PetListPacketHandler
{
    private static ILogger _logger = null!;

    public static void ConfigureServices(IServiceProvider serviceProvider)
    {
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        _logger = loggerFactory.CreateLogger(nameof(PetListPacketHandler));
    }

    public static bool HandlePacket(GatewayConnection connection)
    {
        _logger.LogInformation("Client requested pet list. CharacterId={characterId}, CurrentPetsCount={count}",
            connection.Player.CharacterId, connection.Player.Pets.Count);

        var packet = new PetListPacket
        {
            Pets = connection.Player.Pets
        };

        foreach (var pet in packet.Pets)
        {
            _logger.LogInformation("Sending pet to client: PetId={petId}, Definition={definition}, NameId={nameId}, ImageSetId={imageSetId}, Guid={guid}, TintId={tintId}, TintAlias={tintAlias}",
                pet.Id, pet.Definition, pet.NameId, pet.ImageSetId, pet.Guid, pet.TintId, pet.TintAlias);
        }

        _logger.LogInformation("Sending PetListPacket to client. TotalPetsCount={count}, PacketOpCode={opCode}, SubOpCode={subOpCode}",
            packet.Pets.Count, PetBasePacket.OpCode, PetListPacket.OpCode);

        connection.Player.SendTunneled(packet);
        return true;
    }
}
