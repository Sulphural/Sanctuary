using Sanctuary.Core.IO;

namespace Sanctuary.Packet;

// BaseChatPacket (op15) sub-opcode 3 = "DebugChat" — a clean server->client message that prints a
// single line to the chat window with NO speaker name / channel prefix (unlike PacketChat). The text
// supports the client's inline markup, e.g. <font color='#ffffff' size='14'>Line 1<br>Line 2</font>.
public class ChatPacketDebugChat : BaseChatPacket, ISerializablePacket
{
    public new const short OpCode = 3;

    // The message to display. May contain <font>/<br> markup.
    public string? Message;

    // When true the client prints Message to the chat window.
    public bool PrintToChat;

    public ChatPacketDebugChat() : base(OpCode)
    {
    }

    public byte[] Serialize()
    {
        using var writer = new PacketWriter();

        base.Write(writer);

        writer.Write(Message);
        writer.Write(PrintToChat);

        return writer.Buffer;
    }
}
