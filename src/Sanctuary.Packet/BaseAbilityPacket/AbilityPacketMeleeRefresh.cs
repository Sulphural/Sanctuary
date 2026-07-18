using Sanctuary.Core.IO;

namespace Sanctuary.Packet;

// op36/11 AbilityPacketMeleeRefresh (S2C) — drives the attack-cooldown sweep on the ability button. The client
// sets its cooldown-end time = now + this value; until then the slot shows a radial cooldown counting down.
// Reversed from the client (op36 dispatcher FUN_00a35cc0 case 0xb + reader FUN_00a31430): a single int32 after
// the BaseAbilityPacket header, added to the client's 64-bit game clock ([+0xb8]) to set the end time ([+0x68]).
// Value is in game-clock ms (same scale as the swing cadence).
public class AbilityPacketMeleeRefresh : BaseAbilityPacket, ISerializablePacket
{
    public new const short OpCode = 11;

    public int CooldownMs;

    public AbilityPacketMeleeRefresh() : base(OpCode)
    {
    }

    public byte[] Serialize()
    {
        using var writer = new PacketWriter();

        base.Write(writer); // [op36][sub11]

        writer.Write(CooldownMs);

        return writer.Buffer;
    }
}
