using Sanctuary.Core.IO;

namespace Sanctuary.Packet;

// Server -> client: "you are no longer in the queue" - takes the row back out of the client's
// `BaseClient.Matchmaking.Requests` data source, which is what makes the QuickMatch waiting indicator (and
// the row's `*WAITING*` marker) disappear and lets the Lobby drop out of its waiting state.
//
// Needed because 141/6 CancelMatchRequest is the client ASKING to leave: it only ever comes from the one
// player who pressed the button, so the other members of a group that queued together get no such event
// and would sit there still showing the indicator.
//
// Opcode 141 sub 5 (client ctor 0x00aa123c pushes 5). Like 141/3 and 141/4 it holds a single
// MatchmakingRequest at +0x10 and nothing else - its member ctor call lands on the same 0x0106F3F0 - so
// this is again the original request echoed back rather than a record rebuilt by hand.
//
// ★ The DIRECTION is inferred from the name and from 141/6 being the client's own leave request; it has
// not been seen on the wire. If it turns out to be client-to-server, the indicator simply won't clear -
// nothing breaks - and the next candidate is a second 141/4 carrying a cleared/blank request.
public class ClearMatchRequestPacket : BaseMatchmakingPacket, ISerializablePacket
{
    public new const short OpCode = 5;

    // The serialized MatchmakingRequest being withdrawn - the same bytes the join arrived with.
    public byte[] Request = [];

    public ClearMatchRequestPacket() : base(OpCode)
    {
    }

    public byte[] Serialize()
    {
        using var writer = new PacketWriter();

        Write(writer);

        writer.Write(Request);

        return writer.Buffer;
    }
}
