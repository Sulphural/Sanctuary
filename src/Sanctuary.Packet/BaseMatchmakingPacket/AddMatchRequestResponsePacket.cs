using Sanctuary.Core.IO;

namespace Sanctuary.Packet;

// Server -> client: "you're in the queue" - the confirmation for a 141/3 join.
//
// This is what puts the player into the client's `BaseClient.Matchmaking.Requests` data source, which is
// what the QuickMatch widget (quickMatch.gfx / Main.wndQuickMatch) draws as the waiting indicator, and
// what the Lobby watches to stay in its "Waiting..." state instead of dropping back to the game list.
//
// Opcode 141 sub 4 (client ctor 0x00aa117c pushes 4).
//
// ★ ITS BODY IS THE SAME RECORD THE JOIN SENT. Both packets construct a single member at +0x10 and hold
// nothing else, and both constructor calls land on the SAME function (0x0106F3F0 - MatchmakingRequest's
// default ctor). So the reply is the request handed straight back, and this echoes the bytes verbatim
// rather than re-deriving a ~69-byte record that has a string buried in the middle of it. That record's
// object layout, for reference if it ever does need building by hand: ints at +0x08/+0x0c/+0x10/+0x14/
// +0x18/+0x1c/+0x20, a string at +0x24, more fields from +0x50.
public class AddMatchRequestResponsePacket : BaseMatchmakingPacket, ISerializablePacket
{
    public new const short OpCode = 4;

    // The serialized MatchmakingRequest, as received.
    public byte[] Request = [];

    public AddMatchRequestResponsePacket() : base(OpCode)
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
