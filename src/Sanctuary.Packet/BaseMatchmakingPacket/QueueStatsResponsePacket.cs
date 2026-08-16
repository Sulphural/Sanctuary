using System.Collections.Generic;

using Sanctuary.Core.IO;

namespace Sanctuary.Packet;

// Server -> client: the "N Waiting / Avg Wait" numbers for the Matchmaking panel, answering 141/13.
//
// ★★ THESE NUMBERS ARE **NOT** IN MatchmakingQueueDefinition, contrary to what lobby.lua's
// `GetData(row, 15/16)` reads suggested. Proved 2026-08-15 by stamping EVERY int field of a queue row with
// a recognisable 1000+index marker and looking at the panel: Min Players showed 1003, Max Players 1004 and
// the label became string 1015 - but **Waiting still read 0 and Avg Wait still read "-"**. Nothing in that
// record feeds them, so they come from here.
//
// Opcode 141 sub 14 (client ctor 0x00a9df67 pushes 0x0e). That ctor initialises TWO lists, at +0x18 and
// +0x28, and both carry the SAME list vtable 0x017eb8c0 - which resolves through RTTI to
// `SoeUtil::List<H>`, i.e. **List<int>**. Two parallel int lists is exactly enough for a waiting count and
// an average wait per queue, and nothing else in the object could carry them.
//
// ★★ THE SECOND LIST IS THE WAITING COUNT - live-proven 2026-08-15. Sent as [waiting][avgWait] first, the
// panel showed the AVG WAIT values under "N Waiting" (20/87/76/63/61 against the five queues), so the
// client takes its count from whichever list is written second. Both lists run parallel to the queue order
// from 141/2 - that much is confirmed too, since those five numbers landed against the right five games.
// The leading Guid mirrors ListQueuesResponse, whose wire form ([ulong Guid][list]) is known good, and the
// row values landing correctly says the framing in front of the lists is right.
//
// ★ WHAT THE FIRST LIST IS, IS STILL OPEN, and "Avg Wait" still renders as "-". The avg wait is the
// obvious candidate for it and is what is sent there now; if that turns out to be wrong, the other
// possibilities are a queue-id key list, or an avg wait the client averages locally and simply has no
// samples for yet (the row text drops the ", Avg Wait: ..." clause entirely when it has nothing).
public class QueueStatsResponsePacket : BaseMatchmakingPacket, ISerializablePacket
{
    public new const short OpCode = 14;

    public ulong Guid;

    // One entry per queue, in the order ListQueuesResponse sent them. Serialize order matters: the count
    // has to go SECOND (see above).
    public List<int> PlayersWaiting = [];
    public List<int> AverageWaitSeconds = [];

    public QueueStatsResponsePacket() : base(OpCode)
    {
    }

    public byte[] Serialize()
    {
        using var writer = new PacketWriter();

        Write(writer);

        writer.Write(Guid);

        writer.Write(AverageWaitSeconds); // first list - contents unconfirmed, see above
        writer.Write(PlayersWaiting);     // second list - THIS is the one the panel shows as "N Waiting"

        return writer.Buffer;
    }
}
