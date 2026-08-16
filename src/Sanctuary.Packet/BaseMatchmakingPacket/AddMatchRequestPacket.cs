using System;

using Sanctuary.Core.IO;

namespace Sanctuary.Packet;

// Client -> server: "put me in this queue" - what the Matchmaking panel's "Join!" button sends.
//
// Opcode 141 sub 3 (client ctor 0x00c087cc pushes 3). The 69-byte body below was read off the wire
// 2026-08-15, joining Snowball Fighting as character 241:
//
//   00000000 00000000   +0x00  (8) always zero so far - group guid, most likely
//   00000000            +0x08  (4) zero
//   33000000            +0x0C  (4) 0x33 = 51 = THE QUEUE ID          <- the field that matters
//   F1000000 00000000   +0x10  (8) 241 = the requesting character     <- and this one
//   00000000 00000000   +0x18  (8) zero
//   71010000            +0x20  (4) 369 - the queue row's own Param1, echoed straight back
//   00000000            +0x24  (4) zero
//   A4DB806A            +0x28  (4) 1786829732 - a Unix timestamp (Aug 2026), i.e. "asked at"
//   00000000            +0x2C  (4) zero
//   01000000            +0x30  (4) 1
//   ...                 +0x34  (17) zero, ending on an odd byte (so a trailing bool)
//
// Only QueueId and PlayerGuid are named: everything else was zero or an echo in the one capture, and
// naming a field off a single sample is how the "EncounterDescriptionId" mistake happened in this same
// packet family. Parsing is deliberately lenient about the tail for the same reason.
public class AddMatchRequestPacket : BaseMatchmakingPacket, IDeserializable<AddMatchRequestPacket>
{
    public new const short OpCode = 3;

    public int QueueId;
    public ulong PlayerGuid;

    // The whole body verbatim, i.e. the serialized MatchmakingRequest. Kept because the reply (141/4)
    // carries the SAME payload type - both packets construct a MatchmakingRequest at +0x10 and hold no
    // other member (their ctors call the same one, 0x0106F3F0) - so the confirmation can be produced by
    // echoing this back rather than by guessing a ~69-byte layout with a string in the middle of it.
    public byte[] RawRequest = [];

    public AddMatchRequestPacket() : base(OpCode)
    {
    }

    public static bool TryDeserialize(ReadOnlySpan<byte> data, out AddMatchRequestPacket value)
    {
        value = new AddMatchRequestPacket();

        var reader = new PacketReader(data);

        if (!value.TryRead(ref reader))
            return false;

        value.RawRequest = reader.RemainingSpan.ToArray();

        reader.Read(12); // +0x00 group guid + the zero int at +0x08

        if (!reader.TryRead(out value.QueueId))
            return false;

        if (!reader.TryRead(out value.PlayerGuid))
            return false;

        return true; // tail deliberately not validated - see the note above
    }
}
