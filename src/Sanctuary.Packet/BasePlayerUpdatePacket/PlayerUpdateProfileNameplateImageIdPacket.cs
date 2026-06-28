using Sanctuary.Core.IO;

namespace Sanctuary.Packet;

public class PlayerUpdateProfileNameplateImageIdPacket : BasePlayerUpdatePacket, ISerializablePacket
{
    public new const short OpCode = 71;

    public ulong Guid;
    public int NameplateImageId;

    public PlayerUpdateProfileNameplateImageIdPacket() : base(OpCode)
    {
    }

    public byte[] Serialize()
    {
        using var writer = new PacketWriter();

        Write(writer);

        writer.Write(Guid);
        writer.Write(NameplateImageId);

        return writer.Buffer;
    }
}
