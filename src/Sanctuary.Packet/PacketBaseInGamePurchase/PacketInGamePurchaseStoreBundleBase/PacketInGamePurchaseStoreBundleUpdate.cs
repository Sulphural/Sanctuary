using Sanctuary.Core.IO;
using Sanctuary.Packet.Common.GameCommerce;

namespace Sanctuary.Packet;

public class PacketInGamePurchaseStoreBundleUpdate : PacketInGamePurchaseStoreBundleBase, ISerializablePacket
{
    public new const int OpCode = 2;

    public StoreDefinition Store = new();

    public PacketInGamePurchaseStoreBundleUpdate() : base(OpCode)
    {
    }

    public byte[] Serialize()
    {
        using var writer = new PacketWriter();

        Write(writer);

        Store.Serialize(writer);

        return writer.Buffer;
    }
}
