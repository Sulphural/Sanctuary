using System;

using Sanctuary.Core.IO;

namespace Sanctuary.Packet;

// Sent by client for a single (non-repeating) attack on a target (OpCode 32, SubOpCode 3).
// C2S only — the client's op32 S2C dispatcher (FUN_00a2c370) handles 4/5/6/7/9; 1 and 3 are requests.
public class CombatPacketSingleAttackTarget : BaseCombatPacket, IDeserializable<CombatPacketSingleAttackTarget>
{
    public new const short OpCode = 3;

    // GUID of the target entity to attack.
    public ulong TargetGuid;

    public CombatPacketSingleAttackTarget() : base(OpCode)
    {
    }

    public static bool TryDeserialize(ReadOnlySpan<byte> data, out CombatPacketSingleAttackTarget value)
    {
        value = new CombatPacketSingleAttackTarget();

        var reader = new PacketReader(data);

        if (!value.TryRead(ref reader))
            return false;

        if (!reader.TryRead(out value.TargetGuid))
            return false;

        return true;
    }
}
