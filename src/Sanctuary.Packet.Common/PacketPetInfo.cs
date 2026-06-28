using System;

using Sanctuary.Core.IO;

namespace Sanctuary.Packet.Common;

public class PacketPetInfo : ISerializableType
{
    public int Id;

    // Server side
    public int Definition;

    public int NameId;

    public int ImageSetId;

    public int TintId;
    public string TintAlias = null!;

    public ulong Guid;

    public bool MembersOnly;

    public bool IsNameable; // Server-side only - not serialized
    public bool IsUpgradable; // Sent to client (matching mount structure)
    public bool IsUpgraded; // Sent to client (matching mount structure)

    public void Serialize(PacketWriter writer)
    {
        writer.Write(Id);
        writer.Write(NameId);
        writer.Write(ImageSetId);
        writer.Write(Guid);
        writer.Write(MembersOnly);
        writer.Write(TintId);
        writer.Write(TintAlias);
        writer.Write(IsUpgradable);
        writer.Write(IsUpgraded);
    }
}
