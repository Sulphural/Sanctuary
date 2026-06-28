using Sanctuary.Core.IO;

namespace Sanctuary.Packet;

/// <summary>
/// Updates the client with new profile XP (OpCode 38, SubOpCode 14).
/// </summary>
public class ClientUpdatePacketUpdateProfileExperience : BaseClientUpdatePacket, ISerializablePacket
{
    public new const short OpCode = 14;

    /// <summary>Profile/Job ID that gained XP.</summary>
    public int ProfileId;

    /// <summary>XP gained this update.</summary>
    public int XpGained;

    /// <summary>Total XP progress within current level (0-100 percent).</summary>
    public int TotalXpInLevel;

    /// <summary>Current level of the profile.</summary>
    public int CurrentLevel;

    public ClientUpdatePacketUpdateProfileExperience() : base(OpCode)
    {
    }

    public byte[] Serialize()
    {
        using var writer = new PacketWriter();

        Write(writer);

        writer.Write(ProfileId);
        writer.Write(XpGained);
        writer.Write(TotalXpInLevel);
        writer.Write(CurrentLevel);

        return writer.Buffer;
    }
}
