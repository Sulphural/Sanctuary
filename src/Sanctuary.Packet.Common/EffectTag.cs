using Sanctuary.Core.IO;

namespace Sanctuary.Packet.Common;

// Wire layout RE'd 2026-07-25 from the client's real ClientPcData deserializer chain
// (FUN_00944fe0 -> FUN_00a19e70 HashListMap<int,ClientEffectTag> insert -> FUN_00a18cc0 ->
// FUN_008e93e0), NOT guessed. Field order/types/sizes are confirmed; semantic names are not -
// see memory project_effecttag_rev_eng.md for the full offset table and how to test them live.
public class EffectTag : ISerializableType
{
    // HashListMap bucket key (client reads 1 byte, hashes % 20 buckets) - likely a tag/buff type id.
    public byte Key;

    public int Unknown1;
    public int Unknown2;
    public int Unknown3;

    public int Unknown4;
    public int Unknown5;

    public float Unknown6;

    public int Unknown7;

    public bool Unknown8;

    // Raw 8 bytes copied as-is (2x int32) - shape matches a guid (e.g. caster/source entity).
    public ulong Unknown9;

    // Client computes now - wireValue via GetTickCount()/__time64() into an absolute time64.
    // Wire sends ONE signed int32 delta each; likely StartTime/AppliedTime and EndTime/ExpireTime.
    public int StartTimeDelta;
    public int EndTimeDelta;

    public int Unknown10;
    public int Unknown11;

    // Raw 8 bytes copied as-is (2x int32) - another guid-shaped field.
    public ulong Unknown12;

    public int Unknown13;
    public int Unknown14;

    public bool Unknown15;
    public bool Unknown16;
    public bool Unknown17;

    public int Unknown18;

    public bool Unknown19;

    public int Unknown20;
    public int Unknown21;

    public void Serialize(PacketWriter writer)
    {
        writer.Write(Key);

        writer.Write(Unknown1);
        writer.Write(Unknown2);
        writer.Write(Unknown3);

        writer.Write(Unknown4);
        writer.Write(Unknown5);

        writer.Write(Unknown6);

        writer.Write(Unknown7);

        writer.Write(Unknown8);

        writer.Write(Unknown9);

        writer.Write(StartTimeDelta);
        writer.Write(EndTimeDelta);

        writer.Write(Unknown10);
        writer.Write(Unknown11);

        writer.Write(Unknown12);

        writer.Write(Unknown13);
        writer.Write(Unknown14);

        writer.Write(Unknown15);
        writer.Write(Unknown16);
        writer.Write(Unknown17);

        writer.Write(Unknown18);

        writer.Write(Unknown19);

        writer.Write(Unknown20);
        writer.Write(Unknown21);
    }
}