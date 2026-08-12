using Sanctuary.Core.IO;

namespace Sanctuary.Packet;

// op143 - repeating activities, the client's "you may do X again N more times" system.
//
// This is what gates the daily wheel: the minigames Browser only enables its Play button when
// Ui.GetRepeatingActivityCount("wheel") > 0, so send an activity named "wheel" with a count.
//   sub 1 Add   - the five state ints, then two strings (the first is the name) and a trailing int
//   sub 2 State - five ints: the activity id the client matches on, then count/consecutive/next-time
public class BaseRepeatingActivityPacket
{
    public const short OpCode = 143;

    private readonly byte _subOpCode;

    // The id the client keys its stored entry by (RepeatingActivityStatePacket's first int).
    public int ActivityId;

    // The four state values, in wire order. Best guess: remaining count, consecutive count, and a
    // next-available timestamp.
    public int Count;
    public int Consecutive;
    public int NextTime;
    public int Unknown;

    protected BaseRepeatingActivityPacket(byte subOpCode)
    {
        _subOpCode = subOpCode;
    }

    protected void Write(PacketWriter writer)
    {
        writer.Write(OpCode);
        writer.Write(_subOpCode);

        writer.Write(ActivityId);
        writer.Write(Count);
        writer.Write(Consecutive);
        writer.Write(NextTime);
        writer.Write(Unknown);
    }
}

// sub 2 - update an activity the client already knows about.
public class RepeatingActivityStatePacket : BaseRepeatingActivityPacket, ISerializablePacket
{
    public new const byte OpCode = 2;

    public RepeatingActivityStatePacket() : base(OpCode)
    {
    }

    public byte[] Serialize()
    {
        using var writer = new PacketWriter();

        Write(writer);

        return writer.Buffer;
    }
}

// sub 1 - register (or update) an activity. Name is what Lua looks up: "wheel" for the daily wheel.
public class RepeatingActivityAddPacket : BaseRepeatingActivityPacket, ISerializablePacket
{
    public new const byte OpCode = 1;

    public string Name = "wheel";
    public string Name2 = "";
    public int Unknown2;

    public RepeatingActivityAddPacket() : base(OpCode)
    {
    }

    public byte[] Serialize()
    {
        using var writer = new PacketWriter();

        Write(writer);

        writer.Write(Name);
        writer.Write(Name2);
        writer.Write(Unknown2);

        return writer.Buffer;
    }
}
