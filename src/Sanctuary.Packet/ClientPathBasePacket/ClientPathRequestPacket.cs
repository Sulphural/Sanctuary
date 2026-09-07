using System;
using System.Numerics;

using Sanctuary.Core.IO;

namespace Sanctuary.Packet;

// Client -> server (opcode 98, sub 1): a path request for the "Take Me There" breadcrumb system.
// Start is the player's exact position. End is NOT necessarily the final destination: the client first
// searches its own shipped per-zone roadmap ("<zone>.map") for a full route to its target, then asks us
// only for the hop from Start to the FIRST node of that route, walking its own path from there. It
// refuses to send at all when that hop is over 300 units, so requests keep arriving while the objective
// is far away. In a zone with no roadmap its search finds nothing and End is the raw target.
public class ClientPathRequestPacket : ClientPathBasePacket, IDeserializable<ClientPathRequestPacket>
{
    public new const byte OpCode = 1;

    public int RequestId;
    public int Unknown1;
    // Which client controller asked; the reply's ResultType routes the answer back to it.
    public int Mode;      // 1 = breadcrumb trail follower, 2 = auto-move controller
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
