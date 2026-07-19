using System.Numerics;

using Sanctuary.Core.IO;

namespace Sanctuary.Packet;

// op35/59 PlayerUpdateSeekTarget - installs a native ProxiedCharacterSeekMovementController on an entity so
// the CLIENT moves it toward a target entity (smooth, client-side). RE'd from the reader FUN_008e64f0:
//   [ulong CharacterGuid @+0x10][ulong TargetGuid @+0x18][float @+0x20][float @+0x24][float @+0x28]
//   [float @+0x2c][float @+0x30][Vector4 @+0x40 (008e2410)][Vector4 (008e2410)]
// Used natively to make an entity fly/seek toward another - so a spawned projectile entity + SeekTarget to
// the enemy = a server-driven flying projectile with NATIVE client motion (no per-tick op125, no +0x508).
public class PlayerUpdatePacketSeekTarget : BasePlayerUpdatePacket, ISerializablePacket
{
    public new const short OpCode = 59;

    public ulong CharacterGuid;   // the entity to control (the projectile)
    public ulong TargetGuid;      // the entity to seek toward (the enemy)
    public float Unknown1;
    public float Unknown2;
    public float Speed;           // best-guess speed field (probe live)
    public float Unknown4;
    public float Unknown5;
    public Vector4 Position1;      // start / current position
    public Vector4 Position2;      // target / destination position

    public PlayerUpdatePacketSeekTarget() : base(OpCode)
    {
    }

    public byte[] Serialize()
    {
        using var writer = new PacketWriter();

        base.Write(writer); // [short 35][short 59]

        writer.Write(CharacterGuid);
        writer.Write(TargetGuid);
        writer.Write(Unknown1);
        writer.Write(Unknown2);
        writer.Write(Speed);
        writer.Write(Unknown4);
        writer.Write(Unknown5);
        writer.Write(Position1);
        writer.Write(Position2);

        return writer.Buffer;
    }
}
