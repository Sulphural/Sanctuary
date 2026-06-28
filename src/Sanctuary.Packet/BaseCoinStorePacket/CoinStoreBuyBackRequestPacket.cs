using System;

using Sanctuary.Core.IO;

namespace Sanctuary.Packet;

public class CoinStoreBuyBackRequestPacket : BaseCoinStorePacket, IDeserializable<CoinStoreBuyBackRequestPacket>
{
    public new const short OpCode = 12;

    public int TransactionId;
    public int Quantity;

    public CoinStoreBuyBackRequestPacket() : base(OpCode)
    {
    }

    public static bool TryDeserialize(ReadOnlySpan<byte> data, out CoinStoreBuyBackRequestPacket value)
    {
        value = new CoinStoreBuyBackRequestPacket();

        var reader = new PacketReader(data);

        if (!value.TryRead(ref reader))
            return false;

        if (!reader.TryRead(out value.TransactionId))
            return false;

        if (!reader.TryRead(out value.Quantity))
            return false;

        return true;
    }
}
