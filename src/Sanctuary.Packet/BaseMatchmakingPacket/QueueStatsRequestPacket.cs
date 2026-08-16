using System;

using Sanctuary.Core.IO;

namespace Sanctuary.Packet;

// Client -> server: "refresh the queue numbers". Sent by the Matchmaking panel behind
// Matchmaking:UpdateAllQueueStats (lobby.lua) while the list is open.
//
// Opcode 141 sub 13 - from the client's own ctor at 0x00a7fd69, which pushes 0x0d. Its constructor writes
// only the vtable and zeroes nothing else, exactly like ListQueuesRequestPacket (which carries a single
// ulong Guid), so the body is read the same way.
//
// ★ The body is NOT length-checked here on purpose: the layout is inferred from the sibling packet rather
// than observed, and the only thing this drives is a list re-send, so a trailing byte we did not expect
// should not turn into a dropped refresh.
public class QueueStatsRequestPacket : BaseMatchmakingPacket, IDeserializable<QueueStatsRequestPacket>
{
    public new const short OpCode = 13;

    public ulong Guid;

    public QueueStatsRequestPacket() : base(OpCode)
    {
    }

    public static bool TryDeserialize(ReadOnlySpan<byte> data, out QueueStatsRequestPacket value)
    {
        value = new QueueStatsRequestPacket();

        var reader = new PacketReader(data);

        if (!value.TryRead(ref reader))
            return false;

        reader.TryRead(out value.Guid); // optional - see the note above

        return true;
    }
}
