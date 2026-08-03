using System;

using Sanctuary.Core.IO;

namespace Sanctuary.Packet.Common;

public class PacketPetInfo : ISerializableType
{
    public int Id;

    // Server side
    public int Definition;

    public int NameId;

    public int ImageSetId; // Serialized at offset +0xFC (experimental, see Serialize())

    public int TintId;
    public string TintAlias = null!; // Server-side only - not serialized, client has no field for it

    public string TextureAlias = string.Empty;

    public ulong Guid;

    public bool MembersOnly;

    public bool IsNameable; // Server-side only - not serialized
    public string Name = string.Empty;
    public bool IsUpgradable; // Server-side only - not serialized, client struct has no matching field
    public bool IsUpgraded; // Server-side only - not serialized, client struct has no matching field

    // Matches the client's ClientPetData::sub_912CF0 deserializer field-for-field. Re-verified
    // 2026-07-25 against a Ghidra decompilation + raw disassembly of the live client build (MD5
    // E2B27C502DDB1B47A0D0DF951CE6CCA7, matching C:\...\Open Source Free Realms\Client\FreeRealms.exe
    // exactly) - the earlier field order below was wrong: m_strName/m_nNameId were being written
    // right after m_nUnknown3, but the deserializer doesn't read them until AFTER the three
    // HashList-style int lists. sub_8FCDB0/sub_8DB130/sub_8FC8C0 (the three "unknown" list calls)
    // are genuine int32 count+item lists, not strings - the two real strings are read via a shared
    // helper (sub_894B10, a SoeUtil::String<char>-style setter with a 4-byte length prefix) called
    // with an implicit thiscall `this` that Ghidra's decompiler doesn't surface in its pseudo-C
    // (only visible in the raw disassembly: `LEA ECX,[this+0xc]` / `LEA ECX,[this+0x108]`
    // immediately before each call). Sending Name/NameId early shifts every field after it, which
    // trips the deserializer's sticky "ran out of bytes" flag on the first bogus list count it
    // hits - from that point on EVERY remaining field (including the real Name field later in the
    // stream) silently defaults to empty/zero. That's what left the pets panel's "has pets" filter
    // (PetListDataSource::sub_CCC200, checks the Name IString's length at entry+0x14) permanently
    // false for every pet regardless of what name was sent.
    //
    // The client's PetInfoList reader also consumes one leading int32 (used as a hash key) before
    // constructing each ClientPetData entry, so that value is written here too, ahead of the
    // entry's own fields.
    public void Serialize(PacketWriter writer)
    {
        writer.Write(Id); // hash key (outer PetInfoList reader)

        writer.Write(Id); // ClientPetData::m_nId, offset +0x0
        writer.Write(false); // m_bUnknown2, offset +0x4
        writer.Write(0); // m_nUnknown3, offset +0x8
        // Live-tested 2026-08-02: 1.0f rendered as a near-empty bar in the My Pets panel - the
        // client's stat bars expect a 0-100 range, not a 0-1 fraction.
        writer.Write(100.0f); // Hunger, offset +0x20
        writer.Write(100.0f); // Hygiene, offset +0x24
        writer.Write(100.0f); // Play, offset +0x28
        writer.Write(100.0f); // Mood, offset +0x2C
        writer.Write(false); // m_bUnknown8, offset +0x30

        writer.Write(0); // m_PetTricks HashList count (sub_8FCDB0, offset +0x34) - none known yet
        writer.Write(0); // nested list count (sub_8DB130, offset +0xBC) - none known yet
        writer.Write(0); // nested list count (sub_8FC8C0, offset +0xE4) - none known yet

        writer.Write(Name); // m_strName, offset +0xC (4-byte length prefix, via sub_894B10)
        writer.Write(NameId); // m_nNameId, offset +0x1C - immediately follows the Name IString struct

        writer.Write(TintId); // unknown int, offset +0x100
        writer.Write(TextureAlias); // m_strTextureAlias, offset +0x108 (via sub_894B10)
        // EXPERIMENT 2026-08-02: the pet portrait icon showed as a blank placeholder box.
        // PacketMountInfo (a working, comparable system) explicitly sends ImageSetId over the
        // wire - PacketPetInfo never did (the old assumption was "client derives icon from
        // NameId", which the blank icon disproves). +0xFC is the first still-unlabeled int field
        // in wire order, positionally clustered with the other cosmetic fields (TintId/TextureAlias)
        // - trying it here first.
        writer.Write(ImageSetId); // was: unknown int, offset +0xFC
        writer.Write(false); // unknown bool, offset +0x104
        writer.Write(0); // unknown int, offset +0x118
        writer.Write(false); // unknown bool, offset +0x11C

        for (var i = 0; i < 4; i++)
            writer.Write(0); // fixed int[4], offset +0x88

        for (var i = 0; i < 8; i++)
            writer.Write(0); // fixed int[8], offset +0x98
    }
}
