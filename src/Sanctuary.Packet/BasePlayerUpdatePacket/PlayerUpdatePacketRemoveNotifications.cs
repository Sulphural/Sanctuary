using System.Collections.Generic;

using Sanctuary.Core.IO;

namespace Sanctuary.Packet;

public class PlayerUpdatePacketRemoveNotifications : BasePlayerUpdatePacket, ISerializablePacket
{
    public new const short OpCode = 11;

    public List<ulong> Guids = new();

    public PlayerUpdatePacketRemoveNotifications() : base(OpCode)
    {
    }

    public byte[] Serialize()
    {
        using var writer = new PacketWriter();

        Write(writer);

        writer.Write(Guids.Count);

        foreach (var guid in Guids)
        {
            writer.Write(guid);
            writer.Write(0);
            writer.Write(0);
        }

        return writer.Buffer;
    }
}
