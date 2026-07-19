using Sanctuary.Core.IO;

namespace Sanctuary.Packet;

// op35/62 - the NATIVE retail projectile packet (PlayerUpdateLaunchProjectile). Found via live runtime
// trace: router FUN_009471a0 -> op35 dispatch FUN_0092f460 case 0x3e -> deserializer FUN_00919fd0 ->
// reader FUN_00911b40. Wire (after the [35][62] header that base.Write emits, which the client's 008d6830
// re-reads as 2 shorts):
//     FUN_008e8910  = the SAME nested trajectory struct as op36/4 LaunchAndLand (~149 bytes): header
//                     ints/floats (0..23), Vector4 START @24, Vector4 END @40, variable blob @56, two
//                     polymorphic sub-object desers @60/64 (one carries the source guid), Vector4 VELOCITY
//                     @68, then a list + float. Zero-filled parses; wall @56..67 passable when its counts=0.
//     int32         = a trailing field (dest+0xe0).
// SELF-CONTAINED: the source entity guid is read FROM this packet (984960 resolves it), NOT from client
// combat state - so unlike op36/4 this should render a projectile from a raw server packet.
//
// Body is left as a raw byte[] while the nested struct's guid/model/trail/impact fields are probed live
// (same approach that recovered op36/4). Serialize pads nothing - the caller supplies an exact body.
public class PlayerUpdateLaunchProjectilePacket : BasePlayerUpdatePacket, ISerializablePacket
{
    public new const short OpCode = 62;

    // Everything after the [op35][sub62] header: the 008e8910 trajectory struct + the trailing int32.
    public byte[] Body = [];

    public PlayerUpdateLaunchProjectilePacket() : base(OpCode)
    {
    }

    public byte[] Serialize()
    {
        using var writer = new PacketWriter();

        base.Write(writer); // [short 35][short 62]

        foreach (var b in Body)
            writer.Write(b);

        return writer.Buffer;
    }
}
