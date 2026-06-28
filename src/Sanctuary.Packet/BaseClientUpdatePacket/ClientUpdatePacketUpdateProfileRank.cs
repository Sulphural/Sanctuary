using Sanctuary.Core.IO;

namespace Sanctuary.Packet;

/// <summary>
/// Notifies the client of a profile rank/level change (OpCode 38, SubOpCode 18).
/// Triggers the level-up display in the client.
/// </summary>
public class ClientUpdatePacketUpdateProfileRank : BaseClientUpdatePacket, ISerializablePacket
{
    public new const short OpCode = 18;

    /// <summary>Profile/Job ID that leveled up.</summary>
    public int ProfileId;

    /// <summary>New rank/level of the profile.</summary>
    public int NewRank;

    /// <summary>Profile icon to display in UI.</summary>
    public int ProfileIconId;

    /// <summary>Profile name string ID to display in level-up notification.</summary>
    public int ProfileNameId;

    public ClientUpdatePacketUpdateProfileRank() : base(OpCode)
    {
    }

    public byte[] Serialize()
    {
        using var writer = new PacketWriter();

        Write(writer);

        writer.Write(ProfileId);
        writer.Write(NewRank);
        writer.Write(ProfileIconId);
        writer.Write(ProfileNameId);

        return writer.Buffer;
    }
}
