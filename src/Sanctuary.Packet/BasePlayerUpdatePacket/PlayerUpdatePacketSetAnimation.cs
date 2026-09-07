using Sanctuary.Core.IO;

namespace Sanctuary.Packet;

// op35 sub 8 "SetAnimation": play an animation on an entity. Wire layout (21 bytes) traced from the
// client dispatcher FUN_0092f460 case 8:
//   [int16 35][int16 8][ulong Guid][int32 AnimationId][int32 Unknown = 0][byte PlayType]
// PlayType (client +0x20): 2 = play now; bit0 set (1) = write the entity's BASE animation instead
// (stored at entity+0x51c), which loops until it is replaced.
public class PlayerUpdatePacketSetAnimation : BasePlayerUpdatePacket, ISerializablePacket
{
    public new const short OpCode = 8;

    public ulong Guid;
    public int AnimationId;
    public int Unknown;
    public byte PlayType = 2;

    public PlayerUpdatePacketSetAnimation() : base(OpCode)
    {
    }

    public byte[] Serialize()
    {
        using var writer = new PacketWriter();

        Write(writer);

        writer.Write(Guid);
        writer.Write(AnimationId);
        writer.Write(Unknown);
        writer.Write(PlayType);

        return writer.Buffer;
    }
}
