using Sanctuary.Core.IO;

namespace Sanctuary.Packet;

// op107. Header recovered from the client's own reader (FUN_00c88870): [int16 opcode][int8 subOpCode] -
// note the sub-opcode is a BYTE here, not the int16 most other families use.
//
// The client only ACCEPTS two of the five sub-opcodes inbound (dispatcher FUN_00c89780):
//   2 = FirstTimeEventStatePacket
//   5 = FirstTimeEventScriptPacket
// The other three (1 TriggerRequest, 3 ClearRequest, 4 EnablePacket) are client->server only.
public abstract class BaseFirstTimeEventPacket
{
    public const short OpCode = 107;

    public byte SubOpCode;

    protected BaseFirstTimeEventPacket(byte subOpCode) => SubOpCode = subOpCode;

    protected void Write(PacketWriter writer)
    {
        writer.Write(OpCode);
        writer.Write(SubOpCode);
    }
}
