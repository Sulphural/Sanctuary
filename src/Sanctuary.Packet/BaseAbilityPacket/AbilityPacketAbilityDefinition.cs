using Sanctuary.Core.IO;

namespace Sanctuary.Packet;

// op36/13 AbilityDefinition — the reply to the client's op36/12 request. The client inserts it into its
// HashListMap<int, AbilityDefinition> by AbilityId; the AbilitiesScreen reads Name/Desc/Icon from there (miss
// = "undefined"). Wire format reversed from the client field reader FUN_00a32930 (op36 dispatcher FUN_00a35cc0
// -> deserialize FUN_00a34380): a large fixed record, NOT a small stub (a stub mis-parses and nothing inserts).
// Key fields by struct offset: +0x10 Name, +0x14 Desc, +0x18 Icon, +0x1c CastSeconds, +0x38 ManaCost, +0x58
// ManaCostPerSecond, +0x60 AuraDuration, +0x68 MaxAoeTargets; everything else 0, then an empty list + trailing
// bool. Offsets are noted inline so the layout can be re-checked against the decompiler.
public class AbilityPacketAbilityDefinition : BaseAbilityPacket, ISerializablePacket
{
    public new const short OpCode = 13;

    public int AbilityId;          // +0x08 — the map key (the requested ability def id)
    public int NameId;             // +0x10 — Global.Text id shown as the ability name
    public int DescriptionId;      // +0x14 — Global.Text id shown as the description
    public int IconId;             // +0x18 — ability icon image id
    public float CastSeconds;      // +0x1c
    public int ManaCost;           // +0x38
    public int ManaCostPerSecond;  // +0x58
    public int AuraDuration;       // +0x60
    public int MaxAoeTargets;      // +0x68

    // Candidate COOLDOWN/recast fields (currently all 0). The cooldown duration is one of the def's
    // still-unnamed floats; these let a probe sweep them to find which drives the ability-slot cooldown
    // sweep length. All default 0 = no behaviour change.
    public float Probe44;          // +0x44
    public float Probe48;          // +0x48
    public float Probe6c;          // +0x6c
    public float Probe78;          // +0x78
    public float Probe7c;          // +0x7c
    public float Probe8c;          // +0x8c
    public float Probe90;          // +0x90
    public float ProbeA8;          // +0xa8

    public AbilityPacketAbilityDefinition() : base(OpCode)
    {
    }

    public byte[] Serialize()
    {
        using var writer = new PacketWriter();

        Write(writer); // op 36 + sub 13

        writer.Write(AbilityId);         // +0x08
        writer.Write(false);             // +0x0c
        writer.Write(false);             // +0x0d
        writer.Write(NameId);            // +0x10
        writer.Write(DescriptionId);     // +0x14
        writer.Write(IconId);            // +0x18
        writer.Write(CastSeconds);       // +0x1c (float)
        writer.Write(0);                 // +0x20
        writer.Write(0);                 // +0x24
        writer.Write(0);                 // +0x28
        writer.Write(0);                 // +0x2c
        writer.Write(0);                 // +0x30
        writer.Write(0);                 // +0x34
        writer.Write(ManaCost);          // +0x38
        writer.Write(0);                 // +0x3c
        writer.Write(0);                 // +0x40
        writer.Write(Probe44);           // +0x44 (float)
        writer.Write(Probe48);           // +0x48 (float)
        writer.Write(0);                 // +0x4c
        writer.Write(0);                 // +0x50
        writer.Write(ManaCostPerSecond); // +0x58
        writer.Write(false);             // +0x5c
        writer.Write(AuraDuration);      // +0x60
        writer.Write(0);                 // +0x64
        writer.Write(MaxAoeTargets);     // +0x68
        writer.Write(Probe6c);           // +0x6c (float)
        writer.Write(0);                 // +0x70
        writer.Write(0);                 // +0x74
        writer.Write(Probe78);           // +0x78 (float)
        writer.Write(Probe7c);           // +0x7c (float)
        writer.Write(0);                 // +0x80
        writer.Write(0);                 // +0x84
        writer.Write(0);                 // +0x88
        writer.Write(Probe8c);           // +0x8c (float)
        writer.Write(Probe90);           // +0x90 (float)
        writer.Write(false);             // +0x94
        writer.Write(0);                 // +0x98
        writer.Write(0);                 // +0x9c
        writer.Write(false);             // +0xa0
        writer.Write(false);             // +0xa1
        writer.Write(false);             // +0xa2
        writer.Write(0);                 // +0xa4
        writer.Write(ProbeA8);           // +0xa8 (float)
        writer.Write(0);                 // +0xb0 — variable list: count 0 (no entries)
        writer.Write(false);             // +0xad — trailing bool (read after the list)

        return writer.Buffer;
    }
}
