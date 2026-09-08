using System;
using System.Numerics;

using Sanctuary.Core.IO;

namespace Sanctuary.Packet;

public class ClientPathRequestPacket : ClientPathBasePacket, IDeserializable<ClientPathRequestPacket>
{
    public new const byte OpCode = 1;

    public int RequestId;
    public int Unknown1;
    public int Mode;
    public int Unknown3;
    public Vector4 Start;
    public Vector4 End;
    public int Unknown4;

    public ClientPathRequestPacket() : base(OpCode)
    {
    }

    public static bool TryDeserialize(ReadOnlySpan<byte> data, out ClientPathRequestPacket value)
    {
        value = new ClientPathRequestPacket();

        var reader = new PacketReader(data);

        if (!reader.TryRead(out short opCode)) return false;
        if (!reader.TryRead(out byte subOpCode)) return false;

        if (!reader.TryRead(out value.RequestId)) return false;
        if (!reader.TryRead(out value.Unknown1)) return false;
        if (!reader.TryRead(out value.Mode)) return false;
        if (!reader.TryRead(out value.Unknown3)) return false;
        if (!reader.TryRead(out value.Start)) return false;
        if (!reader.TryRead(out value.End)) return false;
        if (!reader.TryRead(out value.Unknown4)) return false;

        return true;
    }
}
