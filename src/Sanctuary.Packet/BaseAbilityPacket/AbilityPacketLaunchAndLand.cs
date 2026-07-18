using System;
using System.Numerics;
using System.Text;

using Sanctuary.Core.IO;

namespace Sanctuary.Packet;

// Server -> client: launch a projectile and resolve its landing. The biggest ability packet by far.
//
// RECOVERED THE HARD WAY - see the notes, because this one defeated the approach that worked for the
// smaller packets:
//   * The dispatcher case (FUN_00a35cc0, case 4) calls NO deserializer. It makes a virtual call
//     (*(*this + 0xc))(), resolved at runtime by hooking the dispatcher and reading ECX->vtable:
//     vtable[0xc] = 0xa33760 (the projectile processor), which calls the real reader FUN_00a31f30.
//   * a31f30's top level is straight-line and readable (the field list below), but it ends with
//     FUN_008e8910 - a large NESTED struct whose decompilation branches, so static field extraction
//     is not trustworthy there (a linear scan reads if/else alternatives as sequential fields).
//   * The total size was therefore established EMPIRICALLY: send an oversized zero body and read the
//     reader's cursor state. Result: consumed=244, err=0 => 4 header + 240 body, and a zero-filled
//     body PARSES.
//
// So: the fields below are named because they were read directly; the nested struct is left OPAQUE
// rather than invented. Serialize() pads to the measured body size, so the packet stays the exact
// length the client expects even if a named field's width is later corrected.
//
// Field MEANINGS are unknown - the reader gives types and order only.
public class AbilityPacketLaunchAndLand : BaseAbilityPacket, ISerializablePacket
{
    public new const short OpCode = 4;

    // Measured with the raw probe + a31f30 cursor hook: total 244 bytes, minus the 4-byte header.
    public const int BodyLength = 240;

    public ulong Guid;              // +0x10
    public string Name = string.Empty; // +0x18 - length-prefixed string (a2fab0), NOT a Vector4
    public int Unknown1;            // +0x28
    public int Unknown2;            // +0x2c
    public int Unknown3;            // +0x30
    public int Unknown4;            // +0x38   NOTE: read order is 0x30 -> 0x38 -> 0x34.
    public int Unknown5;            // +0x34   Offsets are non-monotonic; ORDER is what matters.
    public bool Flag1;              // +0x3c
    public bool Flag2;              // +0x3d
    public int Unknown6;            // +0x40
    public int Unknown7;            // +0x44
    public int Unknown8;            // +0x48
    public Vector4 Position;        // +0x50 - 4 floats (8e2410)
    public float Unknown9;          // +0x60 - float (NaN-checked), not an int
    public int Unknown10;           // +0x68
    public ulong Guid2;             // via 8dadd0
    public int Unknown11;           // +0x74
    public ulong Guid3;             // +0x78
    public bool Flag3;              // +0x80

    // The FUN_008e8910 nested struct (~153 bytes). Deliberately opaque: its decompilation branches, and
    // a zero-filled body is known to parse. Fill in only once its fields are established by measurement.
    public byte[] Nested = [];

    public AbilityPacketLaunchAndLand() : base(OpCode)
    {
    }

    public byte[] Serialize()
    {
        using var writer = new PacketWriter();

        base.Write(writer);   // [op 36][sub 4]
        var headerLength = writer.Buffer.Length;

        writer.Write(Guid);
        writer.Write(Name);
        writer.Write(Unknown1);
        writer.Write(Unknown2);
        writer.Write(Unknown3);
        writer.Write(Unknown4);
        writer.Write(Unknown5);
        writer.Write(Flag1);
        writer.Write(Flag2);
        writer.Write(Unknown6);
        writer.Write(Unknown7);
        writer.Write(Unknown8);
        writer.Write(Position);
        writer.Write(Unknown9);
        writer.Write(Unknown10);
        writer.Write(Guid2);
        writer.Write(Unknown11);
        writer.Write(Guid3);
        writer.Write(Flag3);

        foreach (var b in Nested)
            writer.Write(b);

        // Pad so the body is exactly what the client consumes.
        //
        // BodyLength was measured with an EMPTY string (4 bytes of length prefix, no chars). A real name
        // adds its UTF8 bytes on top, so the target MUST grow by that much. Padding to a fixed 240 with a
        // non-empty name silently truncates the tail, the client reads past the end into garbage, and it
        // CRASHES - that is exactly what happened when this was first tested with real asset names.
        var target = BodyLength + Encoding.UTF8.GetByteCount(Name ?? string.Empty);

        var written = writer.Buffer.Length - headerLength;
        for (var i = written; i < target; i++)
            writer.Write((byte)0);

        return writer.Buffer;
    }
}
