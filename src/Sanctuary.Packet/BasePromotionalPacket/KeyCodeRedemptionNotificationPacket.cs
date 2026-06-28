using Sanctuary.Core.IO;

namespace Sanctuary.Packet;

public class KeyCodeRedemptionNotificationPacket : BasePromotionalPacket, ISerializablePacket
{
    public const short SubOpCode = 1;

    public bool Success { get; set; }

    public KeyCodeRedemptionNotificationPacket() : base(SubOpCode)
    {
    }

    public byte[] Serialize()
    {
        using var writer = new PacketWriter();

        Write(writer);

        writer.Write(Success);

        return writer.Buffer;
    }
}
