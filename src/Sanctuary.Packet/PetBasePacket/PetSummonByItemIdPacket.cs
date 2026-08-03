using System;

using Sanctuary.Core.IO;
using Sanctuary.Packet.Common;

namespace Sanctuary.Packet;

// Client sends this when the player uses/right-clicks a pet-summoning item in their bag (a toy/egg
// item, distinct from the pet's own DB row) - traced via decompile of the client's item
// context-menu action dispatcher (FUN_00A1EC00 -> FUN_00B60110 -> this packet's ctor at
// 0x00B50940, sub-opcode confirmed 0x20 = 32). The trigger site passes 2 fields straight from the
// clicked item's record, matching this project's existing ItemRecord (Definition, Tint) convention
// used by every other "...ByItemRecord" packet (e.g. InventoryPacketEquipByItemRecord).
public class PetSummonByItemIdPacket : PetBasePacket, IDeserializable<PetSummonByItemIdPacket>
{
    public new const byte OpCode = 32;

    public ItemRecord ItemRecord = new();

    public PetSummonByItemIdPacket() : base(OpCode)
    {
    }

    // NOTE: PetBasePacketHandler already consumed the opcode + sub-opcode bytes before dispatching
    // here (same convention as PetSummonRecallPacket) - 'data' starts at the ItemRecord body directly.
    public static bool TryDeserialize(ReadOnlySpan<byte> data, out PetSummonByItemIdPacket value)
    {
        value = new PetSummonByItemIdPacket();

        var reader = new PacketReader(data);

        if (!value.ItemRecord.TryRead(ref reader))
            return false;

        return true;
    }
}
