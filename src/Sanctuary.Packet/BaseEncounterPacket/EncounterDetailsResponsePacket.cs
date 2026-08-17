using System.Collections.Generic;

using Sanctuary.Core.IO;
using Sanctuary.Packet.Common;

namespace Sanctuary.Packet;


// One inline objective inside MiniGameInfo.ObjectiveData[] (client reader ObjectiveData::sub_8FD770,
// 103 B/record). GROUND TRUTH (2026-07-03, 04-01 capture): the real server DEFINES the encounter's goals
// HERE, inline in the launch details packet, then ACTIVATES them by id with op45/sub1 — it never uses
// op45/sub5 (ObjectiveAdd). The client's op45 dispatch requires the goal id to already exist in the
// MiniGameState, so goals that aren't defined inline can never be activated -> no panel.
public sealed class EncounterObjective
{
    public int ObjectiveId;
    public int NameId;          // goal text (server-fed string id; unknown ids -> "<OBJECTIVE n>")
    public int DescriptionId;
    public int Status;          // real inline defs use 0; ObjectiveActivate flips it to 2 (announce)
    public int Count;
    public int Total;           // 0 inline; the follow-up ObjectiveActivate sets the real total
    public int Unknown8;        // real obj0 carried 1 here
    public bool MemberOnly;
    public int Unknown10;

    // Real per-goal XP reward (ground truth: obj 12642's own bundle carried U3=10, NOT the top-level
    // preview bundle - see EncounterDetailsResponsePacket.PreviewXp). 0 = no reward on this row (the old,
    // still-correct behavior for every objective that doesn't set this - writes the proven all-zero bundle).
    public int Xp;
}

// (RewardEntry + the shared bundle serializer live in RewardBundle.cs — used by this packet's preview
// bundle, the loot-wheel packets, and the op50 reward grant banner.)

// INSTANCE WIP (Frostfang Fury): BaseEncounterPacket (op 41) sub-opcode 114 = "EncounterDetailsResponsePacket"
// — the S2C adventure OFFER POPUP (title / difficulty / description / prizes + GO! button).
//
// Wire format reverse-engineered from the client's Unserialize functions (IDA, 2026-06-24), top-down:
//   EncounterDetailsResponsePacket::Unserialize (sub_AA32D0):
//     BaseEncounter header (sub_8D6690 = op/sub + 2 ints)  [handled by BaseEncounterPacket.Write]
//     EncounterDetailsCommon                                (sub_A29120)
//     byte  flag
//     int32 Unknown
//     Set<StoreBundleId> = prizes-at-packet-level           (sub_9B0700; int32 count + ids)
//   EncounterDetailsCommon (sub_A29120):
//     int32 Unknown · int32 Unknown2
//     collection (sub_A27610; int32 count + elems)          [GAP_ member]
//     List<EncounterTeamData> (sub_A24660; int32 count + elems)
//     int32 Unknown3 · int32 TeleportEffectId
//     byte Unknown5 · byte Unknown6 · byte Tutorial
//     int32 Unknown8 · int32 RespawnTime
//     MiniGameInfo                                          (sub_9BDD70)
//     byte UNK0 · byte UNK1
//   MiniGameInfo (sub_9BDD70):
//     int32 NameId(title) · int32 IconId · int32 DescriptionId · int32 Difficulty · int32 ProfileType ·
//     int32 Type · byte MembersOnly ·
//     RewardBundleBase ×3 (reward / member / preview)       (sub_8E7930)
//     ObjectiveData[] (sub_9BC380; int32 count + elems) ·
//     byte ×5 (U8..U12) · string U13 · int32 U14 · byte U15 · int32 PreselectedGameId ·
//     byte ×4 (U16..U19) · int32 U20
//   RewardBundleBase (sub_8E7930):
//     byte Unknown · int32 ×9 (U2..U10) · int32 ×2 pairA · int32 ×2 pairB ·
//     int32 U13 · int32 U14 · int32 entryCount · entryCount×{int32 type + entry body} · int32 U15
//     (empty bundle = 69 fixed bytes, entryCount 0)
//   Reward entry (GROUND-TRUTHED 2026-07-04 against the real 04-01 launch packet idx 28053, which parsed
//   end-to-end with these exact sizes — note the type prefix IS int32, not the byte the first IDA pass said):
//     int32 type (1=ITEM) · byte Hidden · int32 IconId · int32 TintId · int32 NameId · int32 Quantity ·
//     int32 Param1 (=the ITEM DEFINITION id — cross-checked: all 5 real entries' Param1 resolve in
//     ClientItemDefinitions with matching NameId/Icon/Tint) · int32 Param2 · string · int32 U8 ·
//     byte U9 (real entries all 0 = no per-type tail)
//   Real-bundle constants worth mirroring: ints[6] (U8 of U2..U10) = 1.0f in EVERY live bundle (empty or
//   not); empty bundles carry U13=U14=-1, populated ones carry U13=first entry IconId / U14=first NameId.
//   The preview bundle also had ints[0]=10 / ints[3]=15 (best guess: coins/XP — the Lua end-screen wheel
//   reads bundle-DS cols 2/3 as xp/coins) and U15=957 (unknown; not sent).
public class EncounterDetailsResponsePacket : BaseEncounterPacket, ISerializablePacket
{
    public new const short OpCode = 114;

    // --- the visible popup content (MiniGameInfo) ---
    public int NameId;            // title (locale string id)
    public int IconId = -1;       // dungeon emblem icon (-1 = none/default, matches the client ctor default)
    public int DescriptionId;     // description (locale string id)
    public int Difficulty;        // difficulty rating
    public int ProfileType;
    public int MiniGameType;
    public bool MembersOnly;

    // --- a couple of common fields worth exposing ---
    public int TeleportEffectId;
    public int RespawnTime;
    public bool Tutorial;

    // EncounterDetailsCommon "Unknown3" — the ZONE-CONTEXT selector (client apply sub_AA36C0, raw value
    // stored at BaseClient+0x78C): ==6 sets the ARENA flag (+0x958), ==8 hub (m_bIsInHub +0x781),
    // ==9 snowball (+0x782), ==12 (+0x783). THE ARENA FLAG IS THE RED-NAME MECHANISM (RE'd 2026-07-03):
    // while it's set, every AddNpc apply forces the character's disposition to 0 HOSTILE before its own
    // SetProfileId call re-runs the nameplate color resolver -> hostile NPCs get the RED name
    // (Display.NameColorHostileNpc) at spawn. No per-NPC recolor packet exists — this flag, sent BEFORE
    // the NPC adds, is how the live server made encounter mobs red.
    public int ZoneContext;

    // LAUNCH selector (client case 114 @0xaa3dcf, RE'd 2026-07-02): the trailing packet flag byte picks
    // the path — false = OFFER popup (ClientMiniGameManager::sub_9BEB70), true = LAUNCH
    // (sub_9BB2D0: replaces/creates THE MiniGameState from this packet's MiniGameInfo).
    // The MiniGameState is the master gate for the whole minigame UI: every op45 objective packet
    // (goals panel) is dropped while m_MiniGameStates is empty, and IsInMiniGame() stays false.
    // So the encounter entry flow must send this packet AGAIN with Launch=true at GO!.
    public bool Launch;

    // Objectives DEFINED inline (real server flow — see EncounterObjective). Empty = count-0 (offer popup).
    public List<EncounterObjective> Objectives = [];

    // PRIZES (the offer popup's reward list + the victory loot wheel) — serialized into the PREVIEW
    // reward bundle. GROUND TRUTH: the real 04-01 launch packet carried 5 ITEM entries here (the ninja
    // set: Tabi Boots / Power Shard / 1000 Storms sword / Vitality Necklace / Mystery Pack), matching
    // the player's ACTIVE JOB — the job selection is server-side; ProfileType just names the job
    // CATEGORY the set is for (2 = combat jobs, from Profiles.json Type).
    public List<RewardBundleEntryItem> PreviewRewards = [];

    // Coins/XP for the extra loot-wheel slices — IDA-verified DS mapping (bundle U2 = Num Coins,
    // U3 = Experience). Real preview: coins 10, XP 0 (the encounter's XP was granted by the GOAL's
    // own bundle instead — obj 12642's carried U3=10).
    public int PreviewCoins;
    public int PreviewXp;

    // The other two MiniGameInfo bundles (m_RewardBundleBase / m_RewardBundleBase_Member) were ALWAYS
    // sent empty until now — confirmed live (2026-07-26 screenshot) to be the actual "Your Rewards"
    // boxes on the win/score card: a "reward" bundle (Stars = this XP value, rendered as its own box —
    // same DS Xp column the preview bundle uses) and a separate "member" bundle ("Members Only Bonus" —
    // Coins). Distinct from PreviewXp/PreviewCoins above, which only feed the pre-entry offer popup +
    // loot-wheel slice selection - the win screen reads THESE two instead.
    public int RewardXp;
    public int MemberCoins;

    // MiniGameInfo tail int "U20". GROUND TRUTH (04-01 idx 28053): the real server sends the
    // ClientActivityDefinitions ACTIVITY ID here (174 = Frostfang Growler). We sent 0 before 2026-07-04.
    public int ActivityId;

    public EncounterDetailsResponsePacket() : base(OpCode)
    {
    }

    public byte[] Serialize()
    {
        using var writer = new PacketWriter();

        Write(writer); // [op 41][sub 114][int Unknown][int Unknown2]

        // ===== EncounterDetailsCommon (sub_A29120) =====
        writer.Write(0);                 // Unknown
        writer.Write(0);                 // Unknown2
        writer.Write(0);                 // GAP_ collection count = 0 (empty)
        writer.Write(0);                 // EncounterTeamData list count = 0 (empty)
        writer.Write(ZoneContext);       // Unknown3/ZoneContext: 6 = ARENA (red hostile NPCs), 8 = hub
        writer.Write(TeleportEffectId);  // TeleportEffectId
        writer.Write(true);              // Unknown5 (byte) — ctor default 1; passed into the offer display
        writer.Write(false);             // Unknown6 (byte)
        writer.Write(Tutorial);          // Tutorial (byte)
        writer.Write(0);                 // Unknown8
        writer.Write(RespawnTime);       // RespawnTime

        // ----- MiniGameInfo (sub_9BDD70) -----
        writer.Write(NameId);            // title
        writer.Write(IconId);            // icon
        writer.Write(DescriptionId);     // description
        writer.Write(Difficulty);        // difficulty
        writer.Write(ProfileType);       // ProfileType
        writer.Write(MiniGameType);      // Type
        writer.Write(MembersOnly);       // MembersOnly (byte)
        // NOTE: WriteRewardBundle (below) collapses to the all-zero empty shape whenever entries is empty,
        // discarding any coins/xp passed alongside it - fine for the preview bundle (always has real item
        // entries) but wrong here, where these two bundles carry ONLY a coins/xp value and no items. Call
        // RewardBundle.Write directly so RewardXp/MemberCoins actually make it onto the wire.
        new RewardBundleBase { Experience = RewardXp }.Serialize(writer);  // m_RewardBundleBase — win-screen "Stars" box
        new RewardBundleBase { Coins = MemberCoins }.Serialize(writer);   // m_RewardBundleBase_Member — "Members Only Bonus" Coins box
        WriteRewardBundle(writer, PreviewRewards, PreviewCoins, PreviewXp); // m_RewardBundleBase_Preview (the popup prizes + loot wheel)
        writer.Write(Objectives.Count);  // ObjectiveData array — goals defined inline (real server flow)
        foreach (var obj in Objectives)
            WriteObjective(writer, obj);
        writer.Write(true);              // U8  (ctor default 1)
        writer.Write(true);              // U9  (ctor default 1)
        writer.Write(true);              // U10 (ctor default 1)
        writer.Write(true);              // U11 (ctor default 1)
        writer.Write(true);              // U12 (ctor default 1)
        writer.Write((string?)null);     // U13 string (writes int32 0)
        writer.Write(1);                 // U14 (ctor default 1)
        writer.Write(true);              // U15 (ctor default 1)
        writer.Write(0);                 // PreselectedGameId
        writer.Write(false);             // U16
        writer.Write(false);             // U17
        writer.Write(false);             // U18
        writer.Write(false);             // U19
        writer.Write(ActivityId);        // U20 = ClientActivityDefinitions activity id (real: 174)
        // ----- end MiniGameInfo -----

        writer.Write(false);             // EncounterDetailsCommon UNK0 (byte)
        writer.Write(true);              // EncounterDetailsCommon UNK1 (byte) — ★ REQUIRED: client case 114
                                         // gates the whole popup on this (if(!UNK1) -> do nothing). ctor default 1.
        // ===== end EncounterDetailsCommon =====

        writer.Write(Launch);            // packet flag (byte): false = offer popup, true = launch (create MiniGameState)
        writer.Write(0);                 // packet Unknown (int32)
        writer.Write(0);                 // Set<StoreBundleId> count = 0 (no prizes yet)

        return writer.Buffer;
    }

    // One ObjectiveData record (103 B): matches the client reader ObjectiveData::sub_8FD770 and the
    // op45 ObjectiveData layout — kept byte-identical so an inline-defined goal can be activated by id.
    private static void WriteObjective(PacketWriter writer, EncounterObjective obj)
    {
        writer.Write(obj.ObjectiveId);
        writer.Write(obj.NameId);
        writer.Write(obj.DescriptionId);
        writer.Write(false);              // byte Unknown4
        if (obj.Xp > 0)
            new RewardBundleBase { Experience = obj.Xp }.Serialize(writer); // real per-goal XP (no items)
        else
            WriteEmptyRewardBundle(writer);   // RewardBundleBase (69-byte empty)
        writer.Write(obj.Status);
        writer.Write(obj.Count);
        writer.Write(obj.Total);
        writer.Write(obj.Unknown8);
        writer.Write(obj.MemberOnly);     // byte MemberOnly
        writer.Write(obj.Unknown10);
    }

    // RewardBundleBase with no entries. This site sends 0 rather than the -1 "defer to entry[0]"
    // sentinel for the icon/name overrides, which is what it has always sent.
    private static void WriteEmptyRewardBundle(PacketWriter writer)
    {
        new RewardBundleBase { IconId = 0, NameId = 0 }.Serialize(writer);
    }

    // RewardBundleBase with entries. Falls back to the proven empty shape when there are none; otherwise
    // the icon/name overrides mirror entry[0] like the real preview does. Preview only, so no guid tails.
    private static void WriteRewardBundle(PacketWriter writer, IReadOnlyList<RewardBundleEntryItem> entries, int coins, int xp)
    {
        if (entries.Count == 0)
        {
            WriteEmptyRewardBundle(writer);
            return;
        }

        var bundle = new RewardBundleBase
        {
            Coins = coins,
            Experience = xp,
            IconId = entries[0].IconId,
            NameId = entries[0].NameId
        };
        bundle.Entries.AddRange(entries);
        bundle.Serialize(writer);
    }
}
