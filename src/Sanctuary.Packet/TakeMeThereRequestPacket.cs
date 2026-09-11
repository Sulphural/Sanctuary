using System;

using Sanctuary.Core.IO;

namespace Sanctuary.Packet;

// Fires when the client's "Take Me There" quest-tracker button is clicked, and keeps
// retrying every ~30s until it gets a reply. Wire shape confirmed against real captured
// traffic; Unknown1 and Unknown2 are constant across every capture, but their meaning is
// not verified (not a CRC32/FNV1a of the action name).
public class TakeMeThereRequestPacket : IDeserializable<TakeMeThereRequestPacket>
{
    public const short OpCode = 142;

    public int Unknown1;
    public int Unknown2;
    public string ActionName = string.Empty;
    public int RequestId;

    public static bool TryDeserialize(ReadOnlySpan<byte> data, out TakeMeThereRequestPacket value)
    {
        value = new TakeMeThereRequestPacket();

        var reader = new PacketReader(data);

        if (!reader.TryRead(out short opCode) && opCode != OpCode)
            return false;

        if (!reader.TryRead(out value.Unknown1))
            return false;

        if (!reader.TryRead(out value.Unknown2))
            return false;

        if (!reader.TryRead(out value.ActionName))
            return false;

        if (!reader.TryRead(out value.RequestId))
            return false;

        return reader.RemainingLength == 0;
    }
}
