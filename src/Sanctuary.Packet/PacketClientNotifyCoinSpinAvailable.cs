using Sanctuary.Core.IO;

namespace Sanctuary.Packet;

// op171 - coin spin available. Despite the name this does NOT open the wheel: the client builds this
// packet itself (its only construction site is client-side), and sending it has no effect even with the
// op143 grant in place and spins available. Kept because the layout is right and we may want to receive
// it. The wheel is opened by StartingZone.LaunchSpinForTheWinGame.
public class PacketClientNotifyCoinSpinAvailable : ISerializablePacket
{
    public const short OpCode = 171;

    public int Unknown1 = 1;
    public int Unknown2;

    public byte[] Serialize()
    {
        using var writer = new PacketWriter();

        writer.Write(OpCode);

        writer.Write(Unknown1);
        writer.Write(Unknown2);

        return writer.Buffer;
    }
}
