using System.Collections.Generic;

using Sanctuary.Core.IO;
using Sanctuary.Packet.Common;

namespace Sanctuary.Packet;

// ★ BUG FIX: this used to derive from BaseNameChangePacket, so it went out as opcode 192 (name change)
// instead of 141 (matchmaking) - the client routed it to the wrong handler and the matchmaking screen's
// queue list could never populate. The client's own class is
// `ListQueuesResponsePacket@EncounterMatchmaking`, ctor 0x00a9f207, whose base call pushes sub-opcode 2.
public class ListQueuesResponsePacket : BaseMatchmakingPacket, ISerializablePacket
{
    public new const short OpCode = 2;

    public ulong Guid;

    public List<MatchmakingQueueDefinition> Queues = [];

    public ListQueuesResponsePacket() : base(OpCode)
    {
    }

    public byte[] Serialize()
    {
        using var writer = new PacketWriter();

        Write(writer);

        writer.Write(Guid);

        writer.Write(Queues);

        return writer.Buffer;
    }
}