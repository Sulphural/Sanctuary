using Sanctuary.Core.IO;

namespace Sanctuary.Packet;

public class RewardNonBundledItemPacket : ISerializablePacket
{
    public const short OpCode = 50;
    public const byte SubOpCode = 2;

    public int ItemDefinitionId;
    public int Unknown20;
    public int Unknown28;
    public int Unknown2c;
    public int Quantity;
    public int Unknown34;

    public byte[] Serialize()
    {
        using var writer = new PacketWriter();

        writer.Write(OpCode);
        writer.Write(SubOpCode);

        writer.Write(ItemDefinitionId);
        writer.Write(Unknown20);
        writer.Write(Unknown28);
        writer.Write(Unknown2c);
        writer.Write(Quantity);
        writer.Write(Unknown34);

        return writer.Buffer;
    }
}
