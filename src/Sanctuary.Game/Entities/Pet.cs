using System.Collections.Generic;
using System.Numerics;

using Sanctuary.Game.Resources.Definitions;
using Sanctuary.Game.Zones;
using Sanctuary.Packet;

namespace Sanctuary.Game.Entities;

public class Pet : Npc
{
    public Player Owner { get; init; }
    public Resources.Definitions.PetDefinition Definition { get; init; }

    public new string Name { get; set; } = string.Empty;

    /// <summary>
    /// Last position sent to the client for this pet
    /// </summary>
    public System.Numerics.Vector4 LastSentPosition { get; set; } = System.Numerics.Vector4.Zero;

    /// <summary>
    /// Last known owner position to detect if owner is moving
    /// </summary>
    public System.Numerics.Vector4 OwnerLastPosition { get; set; } = System.Numerics.Vector4.Zero;

    public Pet(IZone zone, Player owner, PetDefinition definition) : base(zone)
    {
        Owner = owner;
        Definition = definition;
    }

    public override void TeleportToZone(IZone zone, Vector4 position, Quaternion rotation)
    {
        // Alert/Remove visible entities
        foreach (var visiblePlayer in VisiblePlayers)
            visiblePlayer.Value.OnRemoveVisibleNpcs([this]);

        OnRemoveVisiblePlayers(VisiblePlayers.Values);

        ZoneTile.Entities.Remove(Guid, out _);

        Zone.TryRemoveNpc(Guid);

        // Add to new zone/zonetile

        zone.TryAddPet(this);

        // Teleport to new zone

        Visible = false;

        Zone = zone;

        ZoneTile = ZoneTile.Empty;

        UpdatePosition(position, rotation);
    }

    public override PlayerUpdatePacketAddNpc GetAddNpcPacket()
    {
        var packet = base.GetAddNpcPacket();

        // Pets don't have a rider - they follow their owner
        // The PetActivePacket tells the client which pet is active and should follow

        return packet;
    }

    public override void Dispose()
    {
        base.Dispose();
    }
}
