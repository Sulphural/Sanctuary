using System.Collections.Generic;

using Sanctuary.Core.IO;
using Sanctuary.Packet.Common;

namespace Sanctuary.Packet;


// LOOT WHEEL (Frostfang Fury victory screen) — the real end-of-encounter flow, fully ground-truthed
// 2026-07-04/05 from the 2014-04-01 capture (idx 37834/37838/38115) + the client binary:
//
//   1) S2C op39/sub45 MiniGameLootWheelSetItemToLandOn — base header + ONE RewardBundle. The client
//      apply (ClientMiniGameManager sub_9B6DA0, dispatched from the op39 switch @0x9BFEA0 case 45)
//      takes the bundle's FIRST ENTRY and matches its **NameId** against the MiniGameState's stored
//      PREVIEW bundle rows — the matching row index becomes the wheel's landing slice
//      (Lua "ScoreScreen:StopLootWheelAt(index)"). With NO entry: bundle Coins>0 -> the coins slice
//      (index = row count); else XP>0 -> the XP slice. The spin itself is pure theater — the outcome
//      is whatever this packet says.
//   2) The player clicks the green spin button ("OnLootWheelSpinRequest" -> ScoreScreen:SpinLootWheel),
//      the SWF animates to the stored index, then fires "OnLootWheelRotationStopped" ->
//   3) C2S op39/sub46 MiniGameLootWheelOnRotationStopped (base header only — 3 ints, all -1) ->
//      the server GRANTS the prize (see BaseMiniGamePacketHandler).
//
// The real landed prize on 04-01 was the Battle Item Mystery Pack (10482), which the server instantly
// opened into 3x Flabbergast Sphere (3015) — battle items.
public class MiniGameLootWheelSetItemToLandOnPacket : BaseMiniGamePacket, ISerializablePacket
{
    public new const byte OpCode = 45; // sub-opcode (client ctor @0x9B9E20: BaseMiniGamePacket(45,-1,-1,-1))

    // The landed prize (single entry; only NameId matters for slice selection — the rest is
    // display data). Leave EMPTY and set Coins to land on the coins slice.
    public List<RewardBundleEntryItem> Entries = [];

    public int Coins;

    // Live wheel bundles carry 957 in the trailing bundle int (same value as the details preview).
    public int Unknown15 = 957;

    public MiniGameLootWheelSetItemToLandOnPacket() : base(OpCode, -1, -1, -1)
    {
    }

    public byte[] Serialize()
    {
        using var writer = new PacketWriter();

        Write(writer); // [op39][sub45][StateId][GroupId][GameId] — all -1 = "current state"

        var bundle = new RewardBundleBase
        {
            Coins = Coins,
            IconId = Entries.Count > 0 ? Entries[0].IconId : -1,
            NameId = Entries.Count > 0 ? Entries[0].NameId : -1,
            Trailing = Unknown15
        };
        bundle.Entries.AddRange(Entries);
        bundle.Serialize(writer);

        return writer.Buffer;
    }
}

// One row of the end-of-game score card.
//
// ★ THE COLUMN MAPPING, ground-truthed 2026-08-16 against the client's own scoreScreen.gfx. Its sample
// data sources spell the row out in full - `Name^Total Score::Icon ID^-1::Score Type^4::Score Count^-1::
// Score Max^-1::Score Points^0` - which is six columns lining up 1:1, in order, with the six wire fields.
// So two of the field names below were guesses and are wrong; they are kept only because renaming them
// would churn every caller:
//
//     Name   -> Name          the label
//     NameId -> "Icon ID"     NOT a text id. -1 on every live row, which is what "no icon" looks like.
//     Order  -> "Score Type"  a FORMAT selector, not a sort key (see below)
//     Value  -> Score Count
//     Max    -> Score Max
//     Points -> Score Points
//
// Score Type values seen in the SWF's own sample rows: 2 renders as a TIME (mm:ss), 3 as "N of M", 4 is the
// total line at the foot of the card, 101 a plain counter. The live capture's 0 (enemies defeated) renders
// as a bare count.
//
// ★ `Name` IS A CLIENT-SIDE KEY, NOT TEXT. It does not resolve through the T4 locale - "scoreEnemiesDefeated"
// and its siblings hash to no CID in en_us_data.dat under any namespace - so only names the client already
// knows will render. The four the real 2014-04-01 server was recorded sending are the safe set:
// scoreEnemiesDefeated, scorePlayerKnockouts, scoreTimeBonus, scoreTotalScore. Inventing one is not an
// option; pick whichever of the four is closest to what the game being scored actually did.
public sealed class MiniGameScoreRow
{
    public string Name = "";    // client string key, e.g. "scoreEnemiesDefeated" (live rows use these)
    public int NameId = -1;     // really "Icon ID" - -1 on every live row
    public int Order;           // really "Score Type" - live: 0 enemies, 2 time bonus, 3 knockouts, 4 total
    public int Value = -1;      // "Score Count" - e.g. enemies defeated; -1 = none (total row)
    public int Max = -1;        // "Score Max" - e.g. knockouts 5 of Max 5; -1 = no max
    public int Points;          // "Score Points" - score contribution shown right-aligned
}

// S2C op39/sub47 — the victory screen's SCORE ROWS ("MiniGame:EndScore"). Reader = client sub_9B82D0 ->
// sub_9B2B30 per row: [i32 len][ascii name][i32 NameId][i32 Order][i32 Value][i32 Max][i32 Points].
// Live packet (37838): scoreEnemiesDefeated 37 -> 11100 (300/kill), scorePlayerKnockouts 5/5 -> 25000
// (5000 per remaining), scoreTimeBonus 67 -> 67000, scoreTotalScore -> 108100. The client appends its
// own scoreObjectives/scoreBonusObjectives rows from the MiniGameState.
public class MiniGameGameEndScorePacket : BaseMiniGamePacket, ISerializablePacket
{
    public new const byte OpCode = 47;

    public List<MiniGameScoreRow> Rows = [];

    public MiniGameGameEndScorePacket() : base(OpCode, -1, -1, -1)
    {
    }

    public byte[] Serialize()
    {
        using var writer = new PacketWriter();

        Write(writer);

        writer.Write(Rows.Count);
        foreach (var row in Rows)
        {
            writer.Write(row.Name);
            writer.Write(row.NameId);
            writer.Write(row.Order);
            writer.Write(row.Value);
            writer.Write(row.Max);
            writer.Write(row.Points);
        }

        return writer.Buffer;
    }
}
