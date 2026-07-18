using Sanctuary.Core.IO;

namespace Sanctuary.Packet;

// Server -> client: start/stop the ground-target reticle ("pulse location" targeting). The client answers
// the player's click with op36/16 ReceivePulseLocation - which is why we have never seen sub 16 arrive:
// it only fires once the server has opened a targeting session with THIS packet.
//
// LAYOUT RECOVERED FROM THE CLIENT. op36 dispatcher FUN_00a35cc0, case 0xf -> deserializer a31db0, inner
// reader a30170, which reads in order:
//   FUN_00a2fc10   -> the [short op][short sub] header (BaseAbilityPacket.Write already emits it)
//   1 byte         -> +0x0c   (bool - almost certainly enable/disable of the reticle)
//   4 bytes        -> +0x10   (float, NaN-checked - radius or duration)
//
// Field TYPES and ORDER are confirmed by the reader; the MEANINGS above are inference and must be
// confirmed live before being relied on.
public class AbilityPacketPulseLocationTargeting : BaseAbilityPacket, ISerializablePacket
{
    public new const short OpCode = 15;

    public bool Enabled;    // start (true) / stop (false) the reticle - to be confirmed
    public float Unknown;   // radius or duration - to be confirmed

    public AbilityPacketPulseLocationTargeting() : base(OpCode)
    {
    }

    public byte[] Serialize()
    {
        using var writer = new PacketWriter();

        base.Write(writer);   // [op 36][sub 15]

        writer.Write(Enabled);
        writer.Write(Unknown);

        return writer.Buffer;
    }
}
