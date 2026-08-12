using System.Collections.Generic;

using Sanctuary.Core.IO;

namespace Sanctuary.Packet;

// op193 - announcements, the "What's New" tiles on the welcome screen (the client's
// BaseClient.Announcements data source, not PacketLoadWelcomeScreen).
//
// Clicking a tile runs its LuaCall: "Minigame" opens that minigame's detail panel, and the other verbs
// welcome.lua accepts are Teleport, Marketplace, Store_SC, TCGLobby, HouseBrowser, MysteryChest,
// Achievements, Communicator, Membership, Subscription, Video, ShowComic, ShowActivityCalander,
// OpenHerosJournal, Claim and ReadUpdateNotes.
public class BaseAnnouncementPacket
{
    public const short OpCode = 193;

    private readonly byte _subOpCode;

    protected BaseAnnouncementPacket(byte subOpCode) => _subOpCode = subOpCode;

    protected PacketWriter CreateWriter()
    {
        var writer = new PacketWriter();

        writer.Write(OpCode);
        writer.Write(_subOpCode);

        return writer;
    }
}

// One row of the What's New panel.
public class AnnouncementInfo
{
    // Row identity and sort order. The panel shows them in the order sent; Priority is the client's own
    // tiebreaker.
    public int Id;
    public int Priority;

    // The picture on the tile - an image id, the same space the rest of the UI draws icons from.
    public int IconId;

    // Headline, body copy and the button caption, all Global.Text string ids.
    public int TitleStringId;
    public int BodyStringId;
    public int ButtonStringId;

    // What clicking the tile does - see the verb list in BaseAnnouncementPacket. Empty = an entry that
    // just reads as news.
    public string LuaCall = "";

    // Arguments for that verb. For "Minigame", Param1 is the minigame id.
    public int Param1;
    public int Param2;
    public int Param3;

    public string StringParam1 = "";
    public string StringParam2 = "";

    public void Serialize(PacketWriter writer)
    {
        writer.Write(Id);
        writer.Write(Priority);
        writer.Write(IconId);
        writer.Write(TitleStringId);
        writer.Write(BodyStringId);
        writer.Write(ButtonStringId);

        // Both strings come BEFORE the params on the wire, whatever the column order says.
        writer.Write(LuaCall);
        writer.Write(StringParam1);

        writer.Write(Param1);
        writer.Write(Param2);
        writer.Write(Param3);

        writer.Write(StringParam2);
    }
}

// S2C sub 2: the rows themselves.
public class AnnouncementDataSendPacket : BaseAnnouncementPacket, ISerializablePacket
{
    public const byte SubOpCode = 2;

    public List<AnnouncementInfo> Announcements = [];

    public AnnouncementDataSendPacket() : base(SubOpCode) { }

    public byte[] Serialize()
    {
        var writer = CreateWriter();

        writer.Write(Announcements.Count);

        foreach (var announcement in Announcements)
            announcement.Serialize(writer);

        return writer.Buffer;
    }
}
