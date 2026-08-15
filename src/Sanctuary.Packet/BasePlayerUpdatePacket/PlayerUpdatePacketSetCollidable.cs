using Sanctuary.Core.IO;

namespace Sanctuary.Packet;

public class PlayerUpdatePacketSetCollidable : BasePlayerUpdatePacket, ISerializablePacket
{
    public new const short OpCode = 50;

    public ulong Guid;
    public bool Collidable;

    public PlayerUpdatePacketSetCollidable() : base(OpCode)
    {
    }

    public byte[] Serialize()
    {
        using var writer = new PacketWriter();

        Write(writer);

        return writer.Buffer;
    }

    public override void Write(PacketWriter writer)
    {
        base.Write(writer);
        writer.Write(Guid);
        writer.Write(Collidable);
    }
}
