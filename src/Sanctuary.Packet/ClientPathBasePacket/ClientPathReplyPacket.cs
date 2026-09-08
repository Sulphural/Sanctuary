using System.Collections.Generic;
using System.Numerics;

using Sanctuary.Core.IO;

namespace Sanctuary.Packet;

public class ClientPathReplyPacket : ClientPathBasePacket, ISerializablePacket
{
    public new const byte OpCode = 2;

    public int ResultType = 1;

    public int RequestId;

    public List<Vector4> Path = new();

    public ClientPathReplyPacket() : base(OpCode)
    {
    }

    public byte[] Serialize()
    {
        using var writer = new PacketWriter();

        Write(writer);

        writer.Write(ResultType);
        writer.Write(RequestId);

        writer.Write(Path.Count);
        foreach (var point in Path)
        {
            writer.Write(point.X);
            writer.Write(point.Y);
            writer.Write(point.Z);
            writer.Write(point.W);
        }

        return writer.Buffer;
    }
}
