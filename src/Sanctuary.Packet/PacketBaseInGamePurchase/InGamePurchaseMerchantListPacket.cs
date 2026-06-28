using System.Collections.Generic;
using Sanctuary.Core.IO;

namespace Sanctuary.Packet;

public class InGamePurchaseMerchantListPacket : PacketBaseInGamePurchase, ISerializablePacket
{
    public new const short OpCode = 42;

    public ulong MerchantGuid;
    public List<int> BundleIds = [];

    public InGamePurchaseMerchantListPacket() : base(OpCode)
    {
    }

    public byte[] Serialize()
    {
        using var writer = new PacketWriter();

        Write(writer); // [66(2)][42(2)]

        writer.Write(MerchantGuid); // [guid(8)]
        writer.Write(BundleIds);    // [count(4)][bundleId(4) × count]

        return writer.Buffer;
    }
}
