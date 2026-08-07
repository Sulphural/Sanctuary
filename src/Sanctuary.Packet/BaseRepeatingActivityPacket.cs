using Sanctuary.Core.IO;

namespace Sanctuary.Packet;

// op143 - REPEATING ACTIVITIES: the client's "you may do X again N more times, next one at T" system.
//
// ★ THIS IS WHAT GATES THE DAILY WHEEL. The minigames Browser's detail pane enables the wheel's Play
// button only when `Ui.GetRepeatingActivityCount("wheel") > 0` (decompiled ScriptsBase.bin - the same
// function also calls `setDisableText_lua(GetString("NoSpinMessage"))` for the greyed-out case, which is
// exactly the "Play button stays permanently greyed out" that was worked around with a world kiosk back in
// July). The Activity Portal's wheel tile is fed the same way:
// `setWheelData_lua(Ui.GetRepeatingActivityCount("wheel"), Ui.GetRepeatingActivityNextTime("wheel"))`.
// Pressing that Play/tile fires the Lua event "startWheelMinigame" -> `MiniGameFlashC:StartWheel()`, which
// is what actually loads game_wheel.swf. So: send the player a repeating activity called "wheel" with a
// count, and the client's own UI opens the wheel.
//
// Wire format reversed 2026-08-06 from the client's readers (dispatch @0x00ADBF20 jump-table -> per-sub
// ctor + read + apply):
//   * RepeatingActivityStatePacket (sub 2, read @0x00ADAED0) = FIVE int32s. The first is the activity id
//     the client matches against its stored list (ClientRepeatingActivity node +8); the other four are
//     handed to the entry's state setter (count / consecutive / next-time / ...).
//   * RepeatingActivityAddPacket (sub 1, read @0x00ADAFA0) DERIVES from the state packet: it calls the
//     same five-int reader first, then reads two strings and one trailing int32. Its apply either updates
//     the matching entry or creates a new one carrying those strings - the first is the NAME the Lua looks
//     up ("wheel").
// The exact meaning of the four state ints isn't pinned down yet; "/wheel add" and "/wheel state" let all
// of them be set from chat so they can be identified live.
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
