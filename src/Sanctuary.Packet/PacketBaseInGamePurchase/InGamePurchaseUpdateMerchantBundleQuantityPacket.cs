using System.Collections.Generic;
using Sanctuary.Core.IO;

namespace Sanctuary.Packet;

public class InGamePurchaseUpdateMerchantBundleQuantityPacket : PacketBaseInGamePurchase, ISerializablePacket
{
    public new const short OpCode = 45;

    public List<(int BundleId, int Quantity)> Entries = [];

    public InGamePurchaseUpdateMerchantBundleQuantityPacket() : base(OpCode)
    {
    }

    public byte[] Serialize()
    {
        using var writer = new PacketWriter();

        Write(writer);

        writer.Write(Entries.Count);
        foreach (var (bundleId, quantity) in Entries)
        {
            writer.Write(bundleId);
            writer.Write(quantity);
        }

        return writer.Buffer;
    }
}
