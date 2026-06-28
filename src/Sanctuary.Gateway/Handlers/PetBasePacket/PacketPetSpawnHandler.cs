using System;
using System.Linq;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Sanctuary.Game;
using Sanctuary.Packet;
using Sanctuary.Packet.Common;
using Sanctuary.Packet.Common.Attributes;

namespace Sanctuary.Gateway.Handlers;

[PacketHandler]
public static class PacketPetSpawnHandler
{
    private static ILogger _logger = null!;
    private static IResourceManager _resourceManager = null!;

    public static void ConfigureServices(IServiceProvider serviceProvider)
    {
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        _logger = loggerFactory.CreateLogger(nameof(PacketPetSpawnHandler));

        _resourceManager = serviceProvider.GetRequiredService<IResourceManager>();
    }

    public static bool HandlePacket(GatewayConnection connection, ReadOnlySpan<byte> data)
    {
        if (!PacketPetSpawn.TryDeserialize(data, out var packet))
        {
            _logger.LogError("Failed to deserialize {packet}.", nameof(PacketPetSpawn));
            return false;
        }

        _logger.LogTrace("Received {name} packet. ( {packet} )", nameof(PacketPetSpawn), packet);

        var petInfo = connection.Player.Pets.SingleOrDefault(x => x.Id == packet.Id);

        if (petInfo is null)
        {
            _logger.LogWarning("Player attempted to spawn pet with Id {petId} but it doesn't exist in their pet collection.", packet.Id);
            return true;
        }

        // If pet is already spawned, dismiss it
        if (connection.Player.Pet is not null)
        {
            _logger.LogInformation("Pet already active, dismissing it. PetGuid={petGuid}", connection.Player.Pet.Guid);

            var petDismountResponsePacket = new PetDismountResponsePacket
            {
                OwnerGuid = connection.Player.Guid,
                CompositeEffectId = 46 // Teleport flash effect
            };

            connection.Player.SendTunneledToVisible(petDismountResponsePacket, true);

            connection.Player.Pet.Dispose();
            connection.Player.Pet = null;

            return true;
        }

        SpawnPet(connection, petInfo);

        return true;
    }

    public static void SpawnPet(GatewayConnection connection, PacketPetInfo petInfo)
    {
        if (!_resourceManager.Pets.TryGetValue(petInfo.Definition, out var petDefinition))
        {
            _logger.LogWarning("Pet definition not found for pet definition id {petDefinitionId}.", petInfo.Definition);
            return;
        }

        if (!connection.Player.Zone.TryCreatePet(connection.Player, petDefinition, out var pet))
        {
            _logger.LogWarning("Failed to create pet in zone.");
            return;
        }

        pet.Visible = true;

        pet.NameId = petDefinition.NameId;
        pet.ModelId = petDefinition.ModelId;

        pet.TextureAlias = petDefinition.TextureAlias;
        pet.TintAlias = petDefinition.TintAlias;
        pet.TintId = petInfo.TintId;

        pet.Scale = 1f;
        pet.Disposition = 1;
        pet.Speed = 4.5f; // Default walking speed

        pet.HideNamePlate = true;

        pet.ImageSetId = petDefinition.ImageSetId;

        connection.Player.Pet = pet;

        pet.UpdatePosition(connection.Player.Position, connection.Player.Rotation);

        var petSpawnResponsePacket = new PetSpawnResponsePacket();

        petSpawnResponsePacket.OwnerGuid = connection.Player.Guid;
        petSpawnResponsePacket.PetGuid = pet.Guid;
        petSpawnResponsePacket.CompositeEffectId = 0;

        connection.Player.SendTunneledToVisible(petSpawnResponsePacket, true);

        _logger.LogInformation("Pet spawned for player {playerGuid}. PetId={petId}, PetGuid={petGuid}", 
            connection.Player.Guid, petInfo.Id, pet.Guid);
    }
}
