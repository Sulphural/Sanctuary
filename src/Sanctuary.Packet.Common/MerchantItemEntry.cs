using Sanctuary.Core.IO;

namespace Sanctuary.Packet.Common;

public class MerchantItemEntry : ISerializableType
{
    public int ItemId;
    public int Qty = -1;  // -1 = unlimited stock
    public int Cost;      // coin price

    public void Serialize(PacketWriter writer)
    {
        writer.Write(ItemId);
        writer.Write(Qty);
        writer.Write(Cost);
    }
}
