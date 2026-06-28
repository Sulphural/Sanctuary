using Sanctuary.Core.IO;

namespace Sanctuary.Packet;

public class BasePromotionalPacket
{
    public const short OpCode = 114;

    private short SubOpCode;

    public BasePromotionalPacket(short subOpCode)
    {
        SubOpCode = subOpCode;
    }

    public void Write(PacketWriter writer)
    {
        writer.Write(OpCode);
        writer.Write(SubOpCode);
    }

    public bool TryRead(ref PacketReader reader)
    {
        if (!reader.TryRead(out short opCode) && opCode != OpCode)
            return false;

        return reader.TryRead(out SubOpCode);
    }
}
