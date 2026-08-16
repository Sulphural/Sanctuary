using System.Collections.Generic;

using Sanctuary.Core.IO;

namespace Sanctuary.Packet;

// op107/5 - the SERVER SCRIPT TRIGGER for first-time events. The client's FTE Lua names three trigger
// kinds (ClientScriptTrigger, ServerScriptTrigger, KillTrigger); this is how a server drives the middle one.
//
// Wire layout, read straight off the client's reader (FUN_00c88940), in order:
//   [header]            int16 opcode + int8 sub (BaseFirstTimeEventPacket)
//   [int32][bytes]      Script      -> field +0x14, a length-prefixed string
//   [list]              Params      -> field +0x24
//   [int32]             Unknown     -> field +0x0c
//   [int8]              Flag        -> field +0x10 (stored via SETNE, i.e. a bool)
// The wrapper (FUN_00c88ee0) additionally requires the body to be consumed EXACTLY - trailing bytes fail
// the parse just as truncation does.
//
// ★ Script IS A DOTTED PATH, NOT SOURCE. The handler (FUN_00c893f0) feeds the string to a tokenizer with
// the separator 0x2e ('.'), i.e. it splits "Table.Function" and walks it. That is why sending Lua source
// through this family (or through ExecuteScriptPacket, which has the same string+int-list shape) does
// nothing: the client never compiles anything - hooking luaL_loadbuffer live showed zero calls.
public class FirstTimeEventScriptPacket : BaseFirstTimeEventPacket, ISerializablePacket
{
    public new const byte OpCode = 5;

    public string Script = string.Empty;

    // Element type not established - only an EMPTY list is known-safe to send (count 0), which is what the
    // FTE trigger needs anyway since the event is named by Script/Unknown rather than by an argument.
    public List<int> Params = new();

    // ★ THE EVENT ID (FirstTimeEvents.txt row id, e.g. 75 = FtesSnowball) - live-confirmed: sending 0 here
    // with Clear set made the client print its own debug line "First time event 0 cleared".
    public int EventId;

    // ★ CLEAR, not trigger. True wipes the event's state (the "First time event %d cleared" path); leave it
    // FALSE to actually fire the event. The client's matching debug strings sit together in .rdata:
    // "First time event %d cleared" / "First time events cleared" / "First time events disabled" /
    // "First time events enabled" / "Invalid FTE name: \"%s\"" / "FTE \"%s\" has not been triggered".
    public bool Clear;

    public FirstTimeEventScriptPacket() : base(OpCode)
    {
    }

    public byte[] Serialize()
    {
        using var writer = new PacketWriter();

        Write(writer);

        writer.Write(Script);
        writer.Write(Params);
        writer.Write(EventId);
        writer.Write(Clear);

        return writer.Buffer;
    }
}
