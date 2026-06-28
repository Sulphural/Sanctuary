using System.Collections.Generic;

using Sanctuary.Core.IO;
using Sanctuary.Packet.Common;

namespace Sanctuary.Packet;

public class CoinStoreBuyBackResponsePacket : BaseCoinStorePacket, ISerializablePacket
{
    public new const short OpCode = 13;

    public List<CoinStoreTransactionRecord> Transactions = [];

    public CoinStoreBuyBackResponsePacket() : base(OpCode)
    {
    }

    public byte[] Serialize()
    {
        using var writer = new PacketWriter();

        Write(writer);

        writer.Write(Transactions);

        return writer.Buffer;
    }
}
