using Sanctuary.Core.IO;

namespace Sanctuary.Packet;

// Server -> client: put this player in front of the Matchmaking screen with one queue already picked.
// This is how an NPC match-maker (Calvin Coldcastle for Snowball Fighting) opens the panel on his own
// game instead of dumping the player into the generic list.
//
// Opcode 141 sub 12. RE'd 2026-08-15 from the client's own RTTI: the type descriptor
// `.?AVSelectQueueForUserPacket@EncounterMatchmaking@@` -> COL -> vtable 0x018185a4 -> its single .text
// reference 0x00a99047, the constructor. That ctor pushes 0x0c to the BaseMatchmakingPacket base before
// storing the vtable, which is the sub-opcode - calibrated against the two sub-opcodes already known to
// be right here (ListQueuesRequest's ctor pushes 1, ListQueuesResponse's pushes 2).
//
// The ctor then zero-initialises exactly ONE field, at +0x0c (`89 46 0c`), immediately after the packet
// header - so the body is a single int32. Queue id is the only thing it could sensibly be, and 51 is
// Snowball Fighting in the queue table this server sends (see ListQueuesRequestPacketHandler).
//
// ★ NOT LIVE-VERIFIED. The sub-opcode and the one-int body are read off the binary rather than seen on
// the wire, and the direction is inferred from the name. If the Matchmaking panel doesn't open, this
// packet is the first thing to doubt - the neighbouring sub-opcodes are 3/4 AddMatchRequest(+Response),
// 5 ClearMatchRequest, 6 CancelMatchRequest, 9/10 MatchInvitationRequest(+Response),
// 13/14 QueueStatsRequest(+Response), 15 MatchmakingServerStatus.
public class SelectQueueForUserPacket : BaseMatchmakingPacket, ISerializablePacket
{
    public new const short OpCode = 12;

    // MatchmakingQueueDefinition.Id - which row of the queue list to open on.
    public int QueueId;

    public SelectQueueForUserPacket() : base(OpCode)
    {
    }

    public byte[] Serialize()
    {
        using var writer = new PacketWriter();

        Write(writer);

        writer.Write(QueueId);

        return writer.Buffer;
    }
}
