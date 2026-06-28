using System;

using Sanctuary.Core.IO;

namespace Sanctuary.Packet;

public class CoinStoreClearTransactionHistoryPacket : BaseCoinStorePacket, IDeserializable<CoinStoreClearTransactionHistoryPacket>
{
    public new const short OpCode = 11;

    public CoinStoreClearTransactionHistoryPacket() : base(OpCode)
    {
    }

    public static bool TryDeserialize(ReadOnlySpan<byte> data, out CoinStoreClearTransactionHistoryPacket value)
    {
        value = new CoinStoreClearTransactionHistoryPacket();

        var reader = new PacketReader(data);

        if (!value.TryRead(ref reader))
            return false;

        return true;
    }
}
