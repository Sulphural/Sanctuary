using System.Numerics;

using Sanctuary.Core.IO;

namespace Sanctuary.Packet;

public class HousingPacketPlaceFixture : BaseHousingPacket, ISerializablePacket
{
    public new const short OpCode = 2;

    public ulong FixtureGuid;
    public int ItemDefinitionId;
    public Vector4 Position;
    public Quaternion Rotation;
    public float Scale;

    public HousingPacketPlaceFixture() : base(OpCode)
    {
    }

    public byte[] Serialize()
    {
        using var writer = new PacketWriter();

        Write(writer);

        writer.Write(FixtureGuid);
        writer.Write(ItemDefinitionId);
        writer.Write(Position);
        writer.Write(Rotation);
        writer.Write(Scale);

        return writer.Buffer;
    }
}
