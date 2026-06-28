using System;
using System.Linq;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Sanctuary.Game;
using Sanctuary.Game.Entities;
using Sanctuary.Packet;
using Sanctuary.Packet.Common;
using Sanctuary.Packet.Common.Attributes;

namespace Sanctuary.Gateway.Handlers;

[PacketHandler]
public static class PetSummonRecallPacketHandler
{
    private static ILogger _logger = null!;
    private static IResourceManager _resourceManager = null!;

    public static void ConfigureServices(IServiceProvider serviceProvider)
    {
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        _logger = loggerFactory.CreateLogger(nameof(PetSummonRecallPacketHandler));

        _resourceManager = serviceProvider.GetRequiredService<IResourceManager>();
    }

    public static bool HandlePacket(GatewayConnection connection, ReadOnlySpan<byte> data)
    {
        // NOTE: PetBasePacketHandler already consumed the Pet sub-opcode byte.
        // 'data' starts at the PetSummonRecallPacket body. Read only the payload here.
        var reader = new Sanctuary.Core.IO.PacketReader(data);
        PetSummonRecallPacket packet = new();
        if (!reader.TryRead(out packet.Id))
        {
            _logger.LogError("Failed to read PetSummonRecallPacket body (Id). Remaining: {data}", Convert.ToHexString(data));
            return false;
        }

        _logger.LogTrace("Received {name} packet. ( {packet} )", nameof(PetSummonRecallPacket), packet);

        var petInfo = connection.Player.Pets.SingleOrDefault(x => x.Id == packet.Id);

        if (petInfo is null)
        {
            _logger.LogWarning("User tried to summon unknown pet. {id}", packet.Id);
            return true;
        }

        // If pet is already spawned, recall it
        if (connection.Player.Pet is not null)
        {
            connection.Player.Pet.Dispose();
            connection.Player.Pet = null;
            return true;
        }

        // Otherwise, spawn the pet
        SpawnPet(connection, petInfo);

        return true;
    }

    public static void SpawnPet(GatewayConnection connection, PacketPetInfo petInfo)
    {
        if (!_resourceManager.Pets.TryGetValue(petInfo.Definition, out var petDefinition))
            return;

        if (!connection.Player.Zone.TryCreatePet(connection.Player, petDefinition, out var pet))
            return;

        pet.Visible = true;

        pet.Name = string.Empty; // Pet name not sent in PacketPetInfo (uses NameId for localization)
        pet.NameId = petDefinition.NameId;
        pet.ModelId = petDefinition.ModelId;

        pet.TextureAlias = petDefinition.TextureAlias;
        pet.TintAlias = petDefinition.TintAlias;
        pet.TintId = petInfo.TintId;

        pet.Scale = petDefinition.Scale;
        pet.Disposition = 1;
        pet.Speed = 4.5f; // Default walking speed

        pet.HideNamePlate = false;

        pet.ImageSetId = petDefinition.ImageSetId;

        connection.Player.Pet = pet;

        pet.UpdatePosition(connection.Player.Position, connection.Player.Rotation);

        var petActivePacket = new PetActivePacket();

        petActivePacket.OwnerGuid = connection.Player.Guid;
        petActivePacket.PetGuid = pet.Guid;

        petActivePacket.CompositeEffectId = 46; // PFX_Teleport_Flash

        connection.Player.SendTunneledToVisible(petActivePacket, true);
    }
}
