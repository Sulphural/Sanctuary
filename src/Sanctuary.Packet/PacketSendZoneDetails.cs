using Sanctuary.Core.IO;

namespace Sanctuary.Packet;

public class PacketSendZoneDetails : ISerializablePacket
{
    public const short OpCode = 43;

    public string Name = null!;

    // 0 - TileStatic
    // 1 - TileSeamless
    // 2 - RuntimeSeamless
    // 3 - Mesh

    private int Type = 2; // Should always be 2.

    public bool Tutorial;
    public bool Unknown2;

    public string? Sky;

    public bool IsInArena;

    public int Id;

    public int GeometryId;

    public bool IsInStartingSocialZone;

    // ★ The client exposes this to its scripts as the Lua global `IsInSnowballFight()` - that C function
    // (FUN_00c170d0) just pushes a byte read from the live zone object at +0x782, and this is the bool that
    // lands there: it is the LAST of the two consecutive bools here, matching `IsInHub` reading +0x781 for
    // the one before it (IsInStartingSocialZone).
    //
    // It is what gates the snowball-fight first-time-event tutorial: triggering FtesSnowball while the
    // client does not believe it is in a snowball fight silently does nothing, no matter how the trigger is
    // sent. Set it on the Snowball Battles arena zone.
    public bool IsInSnowballFight;

    public byte[] Serialize()
    {
        using var writer = new PacketWriter();

        writer.Write(OpCode);

        writer.Write(Name);
        writer.Write(Type);

        writer.Write(Tutorial);
        writer.Write(Unknown2);

        writer.Write(Sky);

        writer.Write(IsInArena);

        writer.Write(Id);

        writer.Write(GeometryId);
        writer.Write(IsInStartingSocialZone);
        writer.Write(IsInSnowballFight);

        return writer.Buffer;
    }
}