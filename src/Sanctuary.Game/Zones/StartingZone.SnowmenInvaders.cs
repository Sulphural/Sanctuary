using System;
using System.Collections.Generic;
using System.Numerics;

using Microsoft.Extensions.Logging;

using Sanctuary.Game.Entities;
using Sanctuary.Game.Interactions;
using Sanctuary.Packet;
using Sanctuary.Packet.Common.Chat;

namespace Sanctuary.Game.Zones;

// SNOWMEN INVADERS - the recurring Snow Days world event at the Gifting Tree.
//
// Retail shape (fanbyte Snow Days page, verbatim): a game-wide announcement says snowmen invaders are
// attacking the Gifting Tree; players grab snowballs from a nearby pile and knock the snowmen down; each
// defeated snowman occasionally drops 1-2 Snowman Coal; eventually the Abominable Snowman comes out and
// takes a group effort; on his defeat he leaves a treasure chest that hands a Mystery Gift to everyone who
// helped. The battle repeats every 15 minutes.
//
// Every id here is REAL shipped content, not invented - these are the very NPCs that were pulled out of the
// static spawn roster earlier ("we're going to use them later for something else"):
//   Snowman Invader    - NameId 420986, model 1907 snowman_present.adr   (Npcs.json 31443/31445)
//   Abominable Snowman - NameId 421084, model 1944 snowmanboss.adr       (Npcs.json 31336)
//   Snowman Coal       - item 70454 (NameId 421075)
//   Holiday Mystery Gift - item 76631 (NameId 3947)
//   Treasure chest     - model 151 sg_treasure_chest_01.adr
public sealed partial class StartingZone
{
    private const int SnowmanInvaderNameId = 420986;
    // ★ Settable for the health-bar experiment. The bar is decided by the MODEL's RACE_ID (Models.txt) and
    // by nothing the server sends - snowman models are races 0/24/25 which draw a bar, while dungeon enemy
    // models draw none (robgoblin 102/103, ghostdwarf 9, necrowartzombie 11, troll_ice 106, floren 104).
    // `/snowmen invadermodel <id>` swaps the model live so that can be CONFIRMED rather than inferred.
    // Test ids: 4 robgoblin_m_basic (race 102), 10 ghostdwarf_m_miner (race 9), 100 troll_ice (race 106).
    // 1907 snowman_present is the real one.
    public static int SnowmanInvaderModelId { get; set; } = 1907;
    private const int AbominableSnowmanNameId = 421084;
    private const int AbominableSnowmanModelId = 1944;

    private const int SnowmanCoalItemId = 70454;
    private const int HolidayMysteryGiftItemId = 76631;
    private const int TreasureChestModelId = 151;

    // ★ The chest's own retail strings, recovered the same way as the announcements:
    private const int TreasureChestNameId = 421213;   // "Abominable's Treasure"
    private const int TreasureDialogueId = 421217;    // "*You find treasure inside this large golden chest...*"
    private const int TreasureClaimButtonId = 421218; // "I claim my reward!"

    // The chest's glow, in two layers because no single composite is "big golden glow WITH stars":
    //   16534 PFX_sparkles_gold_coin-tree-large_loop - the continuous golden sparkle, attached so it rides
    //         the chest and can be pulled off with it.
    //   15897 PFX_levelup_achievement_gold - the big golden star burst (7 effects, staged over ~1s). It's a
    //         ONE-SHOT, so it's replayed on a timer to keep the chest visibly popping rather than just lit.
    //
    // ★ THE SHINE MUST BE A SILENT COMPOSITE. This used to be 5379
    // PFX_sparkles_gold_cylinder_invulnerable_loop, and that is where the endless "raindrops" noise after
    // claiming the treasure came from. Read its entry in the client's own ActorCompositeEffectDefinitions.xml:
    //
    //   <EffectDefinition name="PFX_sparkles_gold_cylinder_invulnerable_loop" id="5379" effectCount="5">
    //       PARTICLE 5535 | SOUND 8142 | SOUND 8136 | SOUND 8269 | SOUND 8084     (all at time 0.000000)
    //
    // Four SOUND emitters, every one of them started at t=0 with eventSlot 0 (start) and NO eventSlot 1
    // (stop), on a definition with no defaultLifeTime at all - i.e. an open-ended loop that only ever ends
    // when something explicitly kills it. 8269 is the same emitter PFX_water_blue_cog_elemental-barrier_loop
    // uses, which is exactly the wet pattering the noise sounds like. Attached to a prop that stands in the
    // world for the whole reward phase, any client that misses or mishandles the sub42 teardown is left with
    // that stack running for the rest of the session.
    //
    // It is the ONLY sound-bearing open-ended loop anywhere in this event - the star burst below, the boss's
    // snow explosion (15799), the pile sparkle (15932) and the snowball trail (15329) are all either
    // particle-only or genuine one-shots, and a one-shot's audio is proven to clean itself up (it is the same
    // path every player level-up takes). So rather than chase whichever teardown edge leaks, the shine now
    // uses a composite with NO SOUND children at all and the noise cannot happen by construction.
    //
    // The coin-tree loops are the right family for this: gold sparkles authored for a placed WORLD PROP with
    // no skeleton, which is what a treasure chest is. Tunable live with `/snowmen chestfx <id>` -
    // 16535 (_medium) and 16536 (_small) are the same effect in smaller sizes, 0 turns the shine off.
    // Do NOT set it back to a PFX_*_loop that carries SOUND children.
    // How far the chest is lifted off the sampled ground height. The samples we snap to (the measured pile
    // positions, the routing graph's nodes) are all points a CHARACTER stands on, and a character's position
    // sits at its feet - so snapping the chest's own origin to one of them buries the model up to its lid.
    // This is the difference between "where a player's feet are" and "where a prop rests".
    private const float TreasureGroundOffset = 0.45f;

    public static int TreasureShineFxId { get; set; } = 16534; // PFX_sparkles_gold_coin-tree-large_loop - SILENT
    private const int TreasureStarBurstFxId = 15897;
    private const int TreasureShineTagId = 91001;
    private const int TreasureStarBurstIntervalSeconds = 3;

    // The dialog's response button skin - the same green button + leave arrow the quest conversations use.
    private const int DialogLeaveImageId = 4008;
    private const int DialogGreenButtonImageSet = 17;

    // The Holiday Mystery Gift is a WRAPPER: what you actually receive is one item picked at random from the
    // Gifting Tree quest's own pool, so the chest and the quest hand out the same set. Read from the quest
    // rather than copied, so the pool has one home.
    private const int MysteryGiftPoolQuestId = 3090;

    // The Gifting Tree clearing - the same cluster the quest's presents ring (Quests.json 3090).
    private static readonly Vector4 GiftingTreeCenter = new(250f, 27.1f, 406.5f, 1f);

    // ★ REAL localized string, recovered by hashing Global.Text.<id> against en_us_data.dat:
    //   423066 = "Snowman Invaders are stealing presents in Snowhill!"
    // Goes out as HudMessagePacket (op35/64), the client's own centre-screen message - which is why the text
    // has to be a STRING ID and can't be free text.
    private const int SnowmenWaveMessageId = 423066;

    // The boss's entrance line, spoken by HIM (so the client prefixes "Abominable Snowman:") and coloured
    // RED. 421212 = "Buhahaha! Soon all of your gifts will be MINE!" - again retail's own string, and again
    // the reason it has to be a string id rather than typed text.
    // ★ The invaders' own barks, said as they take a gift off the tree. Real retail lines, recovered by
    // reverse-hashing the locale data: they sit in one block at 421097-421101 (421099 is unused), directly
    // alongside the Abominable's 421103/421104 which this event already speaks.
    private static readonly int[] SnowmanInvaderGiftLines =
    [
        421097, // "Arg... Grr..."
        421098, // "Mmm... gifts... *nom nom*"
        421100, // "Take giiifts... mooore gifts..."
        421101, // "Gifts for maaasters..."
    ];

    // Not every one of them pipes up - a dozen snowmen all barking at once would be noise rather than
    // flavour. Roughly one in three.
    public static int SnowmanInvaderBarkPercent { get; set; } = 35;

    private const int AbominableSnowmanTauntId = 421212;
    private const int AbominableSnowmanDefeatId = 421104; // "Nooo! You can't stop me! NOOO!"
    private const int AbominableSnowmanStoleGiftId = 421103; // "Buahahaha! One more gift for ME!"

    // He reached the tree: he gloats, stands over his prize, then vanishes in a burst of snow. Nobody is
    // rewarded - the whole point of the fight was stopping him getting here.
    private const int AbominableEscapeSeconds = 5;
    private const int AbominableEscapeFxId = 15799;   // PFX_snow_explosion_large
    private const int AbominableEscapeFadeMs = 1500;
    // The invaders' own idle barks - consecutive ids, so retail plainly used them as a pair.
    private static readonly int[] SnowmanBarkLineIds = [421168, 421169]; // "Grr!", "Arg!"
    private const int ChatColorRed = 1;   // ChatPacketFromStringId.ColorId: 0 white, 1 red, 2 yellow, 3 green, 4 blue
    private const int ChatColorGreen = 3;
    private const int ChatColorWhite = 0;

    // Where he comes in - measured in game with !pos, well south-west of the tree, so he has a walk-up rather
    // than popping into the middle of the fight. He then heads for the Gifting Tree (see SpawnAbominableSnowman).
    private static readonly Vector4 AbominableSnowmanSpawn = new(143.211f, 24.381f, 332.691f, 1f);

    // How far short of the Gifting Tree he stops. Walking to the tree's own centre parked him INSIDE the
    // trunk; this leaves him standing beside it, on the side he walked in from, so the gloat reads as him
    // looming over the presents rather than clipping through the tree.
    private const float TreeStandoffDistance = 12f;

    // The tree, backed off along the line he approaches from - his actual destination and stopping point.
    private static readonly Vector4 AbominableStandPosition = StandoffFromTree(AbominableSnowmanSpawn);

    private static float HorizontalDistance(Vector4 a, Vector4 b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    private static Vector4 StandoffFromTree(Vector4 approachFrom)
    {
        var dx = approachFrom.X - GiftingTreeCenter.X;
        var dz = approachFrom.Z - GiftingTreeCenter.Z;
        var length = MathF.Sqrt(dx * dx + dz * dz);

        if (length < 0.001f)
            return GiftingTreeCenter;

        return new Vector4(
            GiftingTreeCenter.X + dx / length * TreeStandoffDistance,
            GiftingTreeCenter.Y,
            GiftingTreeCenter.Z + dz / length * TreeStandoffDistance,
            1f);
    }

    // Boss combat stats. The CombatNpc defaults (aggro 15, attack 5, leash 40) are sized for an ordinary
    // roaming mob and leave a boss swinging at air: he is scaled 1.4, so his MODEL edge is well outside a
    // 5-unit centre-to-centre attack range and players standing against him were simply out of reach. The
    // wider leash keeps him from disengaging mid-fight while the group circles him at the tree.
    private const float AbominableAggroRange = 25f;
    private const float AbominableAttackRange = 9f;
    private const float AbominableLeashRange = 70f;

    // March pace. The CombatNpc default (6.0) is a RUN - he arrived almost immediately and there was no time
    // to mount a defence. This is a heavy plod, so the walk to the tree is a real countdown the players are
    // pushing back against. The march uses ReturnSpeed (see CombatNpc.UpdateMarch).
    private const float AbominableMarchSpeed = 1.8f;

    // ── Boss health, scaled to the turnout ───────────────────────────────────────────────────────────
    // A fixed pool makes the boss trivial in a crowd and unbeatable alone, and this event has no control
    // over how many people show up. Health is therefore set when he spawns, from the number of players in
    // the battle area.
    //
    // Sized against what a thrower can actually output: the march is ~130 units at 1.8/sec, so roughly 70
    // seconds, and the snowball is on a 2s cooldown - about 35 throws, or ~12.6k damage, per player.
    //
    // The per-player figure is deliberately close to a FULL player's output (9000 vs ~12.6k), so a bigger
    // turnout genuinely means a bigger boss rather than a faster kill: the group's margin stays roughly flat
    // (~1.8x solo, ~1.4x in a crowd) instead of widening with every extra person. It is kept a little under
    // one player's theoretical maximum because nobody throws on a perfect cadence - there is walking back to
    // a pile, missed throws, and the 65-second armful running out mid-fight.
    private const int AbominableBaseHitpoints = 7000;
    private const int AbominableHitpointsPerExtraPlayer = 9000;

    // Who counts as "here for the battle". The overworld is ONE zone covering the whole world, so counting
    // every player online would scale the boss off people questing on the other side of the map. This is the
    // Snowhill battle area instead - the same radius the event uses to decide whether to start at all.
    private const float SnowhillBattleRadius = 120f;

    private const int SnowmenEventIntervalSeconds = 900;  // "This battle repeats every 15 minutes."
    private const int SnowmenInvaderLevel = 6;
    private const int AbominableSnowmanLevel = 12;

    // Invaders spawn AROUND EACH SNOWBALL PILE, not around the tree - the piles are the ammo, so putting the
    // fight on top of them is what makes throwing snowballs the way you play it.
    // ★ Settable, and lowered from 4 - a raid of snowmen walking to the tree reads much better with a few
    // than with a crowd, and every one of them is now on a scripted route rather than milling about.
    // `/snowmen perpile <n>` retunes it live.
    public static int InvadersPerPile { get; set; } = 3;
    private const float InvaderPileRadius = 6f;

    // How far an invader wanders from its post while idle. Kept well inside the default 40-unit leash so
    // roaming can never trip a leash reset.
    private const float InvaderRoamRadius = 5f;

    // How close a player has to get before an invader breaks off and chases them.
    private const float InvaderAggroRange = 14f;

    // How far out from the tree the invaders gather once they arrive.
    // How far out from the tree's centre an invader stops to take its gift. Small enough that they are
    // clearly AT the tree rather than loitering at the edge of the clearing, big enough that they don't
    // stand inside the trunk. `/snowmen gatherradius <n>` retunes it live.
    public static float TreeGatherRadius { get; set; } = 8f;

    // A snowman shuffle. The CombatNpc defaults (6.0 walk / 2.5 roam) are a jog; these should look like they
    // are lumbering toward the tree, and it keeps them in range of a thrower for longer.
    private const float InvaderMoveSpeed = 3.4f;
    private const float InvaderRoamSpeed = 2.1f;

    // Invaders are SNOWBALL fodder, not a stat check: ONE hit and they're down.
    private const int InvaderSnowballHitsToKill = 1;

    // ★ NOTHING in this event damages players. The invaders chase and taunt, the boss only wants the tree -
    // it is a snowball fight in a town square, not a place to be killed. CombatNpc.Harmless keeps the
    // posturing (pursuit, facing, the swing clip) without any damage ever landing.

    // The wave runs on a clock, not a body count: killed invaders keep coming back for this long, then the
    // boss arrives. That is what makes it a defence rather than a checklist.
    private const int SnowmenWaveSeconds = 180;

    // Downed invaders are replaced IMMEDIATELY - the wave should feel relentless, not like a slow trickle.
    private const int InvaderRespawnSeconds = 0;

    // The chest is a prop, not a container - it stands as the visible "you won" marker while the rewards go
    // out, then despawns. Nothing has to be clicked, matching the screenshot of players simply gathered round.
    private const int TreasureChestLingerSeconds = 30;

    // "Occasionally, you will receive 1-2 Snowman Coal when you defeat one" - roughly one invader in ten,
    // so coal stays worth having. With a one-hit kill and an endless wave, a high rate would flood the
    // player with the Snowman Showdown currency in a single battle.
    private const int CoalDropPercent = 10;

    // ── Nameplate: SETTLED 2026-08-14 ────────────────────────────────────────────────────────────────
    // The invaders show a blue name AND a health bar, and the bar cannot be removed while keeping the name.
    //
    // ★ THE BAR COMES FROM THE MODEL. Models.txt carries a RACE_ID per model and AddNpc has NO race field -
    // the client looks race up itself by ModelId. 1907 snowman_present is race 0 (a creature) and therefore
    // gets a creature plate WITH a bar; the Snowball Pile (1757) is race 99, a PROP race, which is the only
    // reason it shows a bare nameplate. Nothing the server sends can change that.
    //
    // Ruled out by direct test, every one confirmed correctly set via a live field dump: ShowHealthBar,
    // MaxHealth, EnemyStatus, Disposition, ClientDisposition, ActiveProfile, NameColor, NpcRelevance,
    // IsInteractable, Static, MovementType, Speed. Do not spend another pass on these - the only levers are
    // a different MODEL (no prop-race snowman exists) or HideNamePlate, which takes the name with it.
    //
    // The bar is accepted (user decision). The blue name is kept, which DID work: a non-zero NameColor
    // bypasses the client's colour resolver entirely.
    private const int InvaderNameColor = unchecked((int)0xFF6699CC); // the client's own bluish npc default

    // ── The snowball piles, while the battle is on ───────────────────────────────────────────────────
    // Retail re-dresses the EXISTING piles rather than spawning event-only ones: they keep their places and
    // their model, and for the duration they are renamed and wear a badge that points players at the ammo.
    // (Same piles, because that is what retail's own quest text describes - "Grab some Anti-Snowman
    // Snowballs from the piles near the Gifting Tree" - and ours already stand exactly there.)
    //
    // ★ Both ids are retail's own, recovered from the client's locale table by reversing the T4 hash. The
    // reverse-lookup validates itself: the same brute force maps our existing pile name back to 421142,
    // the id already in use here.
    //   421142 -> "Snowball Pile"               (the year-round name)
    //   422872 -> "Anti-Snowman Snowball Pile"  (during the invasion)
    //   421181 -> "Anti-Snowman Snowball"       (the snowball itself, unused so far)
    private const int AntiSnowmanPileNameId = 422872;

    // NotificationImages entry 251 = the context bubble + icon 26947, which is the 64px sibling of the very
    // icon_event_snowball_fights art already on the toolbar button. The table has one other entry built from
    // the same icon (239, at 0.65 scale instead of 0.75); 251 matches the layer/flag shape of the combat
    // badge that is known to render correctly, so it is the one used here.
    private const int SnowballPileBadgeImageId = 251;

    private enum SnowmenPhase { Idle, Invaders, Boss, Rewarding, Escaping }

    private SnowmenPhase _snowmenPhase = SnowmenPhase.Idle;
    private DateTime _snowmenNextStart = DateTime.UtcNow.AddSeconds(SnowmenEventIntervalSeconds);
    private DateTime _snowmenPhaseDeadline;
    private readonly List<Npc> _snowmenInvaders = [];
    private Npc? _abominableSnowman;
    private Npc? _treasureChest;

    // A nameless, invisible actor that exists purely to SPEAK the event's announcements.
    //
    // ChatPacketFromStringId needs a speaker the client can resolve or it silently drops the line - that is
    // why an announcement sent with SpeakerGuid 0 never appeared. But a speaker with a NAME gets its name
    // prefixed ("Snowman Invader: ..."), which is wrong for an announcement. An actor with NameId 0 and no
    // Name resolves fine and has nothing to prefix, so the line comes through as bare white text.
    private Npc? _announcer;

    // Who has already opened the chest this run. It is claimable by EVERYONE who helped - one gift each -
    // so this is a per-player latch, not a first-come-first-served flag.
    private readonly HashSet<ulong> _treasureClaimed = [];

    // Where the boss actually fell - the chest stands there rather than at the tree.
    private Vector4 _abominableDeathPosition;
    private DateTime _nextTreasureStarBurst = DateTime.MinValue;

    // Where the snowball piles actually ended up, filled by TrySpawnSnowballPile as the zone script places
    // them. Read rather than duplicated, so moving a pile in Resources/SnowballPiles.json moves the wave with
    // it instead of leaving the invaders standing somewhere the ammo no longer is.
    internal readonly List<Vector4> SnowballPilePositions = [];

    // The permanent pile entities - hidden while the event runs (see SetSnowballPileEventState).
    internal readonly List<Npc> SnowballPiles = [];

    // The event's own piles, standing in their place for the duration.
    private readonly List<Npc> _eventSnowballPiles = [];

    // Posts that are currently empty and when they may be refilled - one entry per invader killed while the
    // wave is still running. The post is remembered rather than the npc, so a replacement comes back where
    // its predecessor stood (next to a pile) instead of drifting.
    private readonly List<(Vector4 Post, DateTime RespawnAt)> _snowmenRespawnQueue = [];

    // Where each invader originally spawned (its post by a snowball pile). Needed because an invader's
    // SpawnPosition is REPURPOSED when it reaches the tree - UpdateMarch makes the destination its new home,
    // which is what anchors its roaming there. Without remembering the original post, replacements would
    // spawn at the tree instead of back at the piles where the wave starts.
    private readonly Dictionary<ulong, Vector4> _snowmenOrigins = [];

    // Everyone who landed a hit this run - the reward list. Retail gives the gift to "everyone who helped",
    // which is participation, not the killing blow.
    private readonly HashSet<ulong> _snowmenParticipants = [];

    protected override void UpdateEverySecondZone()
    {
        var now = DateTime.UtcNow;

        // Not snowmen business, but this is the zone's single per-second hook - see UpdateBrucePerformance
        // for why it can't live on the npc itself.
        UpdateBrucePerformance();

        switch (_snowmenPhase)
        {
            case SnowmenPhase.Idle:
                // Don't start a battle nobody can see - it would burn its 15-minute slot to an empty clearing
                // and hand out nothing. The timer simply rolls forward until someone is around.
                if (now >= _snowmenNextStart && HasPlayersNearGiftingTree())
                    StartSnowmenInvaders();
                break;

            case SnowmenPhase.Invaders:
                _snowmenInvaders.RemoveAll(npc => !npc.IsAlive);

                // Killing them all does NOT end the wave - they keep coming until the clock runs out.
                if (now >= _snowmenPhaseDeadline)
                {
                    SpawnAbominableSnowman();
                    break;
                }

                UpdateInvaderRaids(now);
                RefillSnowmenPosts(now);
                break;

            case SnowmenPhase.Boss:
                // The wave carries on underneath the boss fight.
                _snowmenInvaders.RemoveAll(npc => !npc.IsAlive);
                UpdateInvaderRaids(now);
                RefillSnowmenPosts(now);

                if (_abominableSnowman is null || !_abominableSnowman.IsAlive)
                {
                    FinishSnowmenInvaders();
                    break;
                }

                // He got to the Gifting Tree - the players failed to stop him.
                // ★ HORIZONTAL distance, matching CombatNpc.DistanceTo (which is XZ-only) - the march's own
                // arrival test. Vector4.Distance is 3D, and the stand point carries the tree's height while
                // he walks on ground several units below it, so the vertical gap alone could keep this above
                // the threshold: he would stop marching, never register as arrived, drop through to Idle and
                // wander into the trunk chasing someone.
                if (_abominableSnowman is CombatNpc marching && marching.MarchTarget is null &&
                    HorizontalDistance(marching.Position, AbominableStandPosition) < 6f)
                    BeginAbominableEscape(marching);
                break;

            case SnowmenPhase.Escaping:
                if (now >= _snowmenPhaseDeadline)
                    FinishAbominableEscape();
                break;

            case SnowmenPhase.Rewarding:
                PulseTreasureStars(now);

                if (now >= _snowmenPhaseDeadline)
                    ClearSnowmenEvent();
                break;
        }
    }

    // True while there is an actual FIGHT on - the invader wave or the boss. The reward phase does not
    // count: the boss is down, the chest is up, and the snowball fight goes back to being a snowball fight.
    //
    // Used to suspend player-vs-player snowballs for the duration (see SnowballTool.FindTarget), so a wave
    // is spent throwing at snowmen instead of at each other.
    public bool SnowmenBattleActive => _snowmenPhase is SnowmenPhase.Invaders or SnowmenPhase.Boss;

    // Dev entry point (/snowmen): tear down whatever is running and start a fresh battle now, so the event
    // can be tested without sitting out a 15-minute interval.
    public void ForceStartSnowmenInvaders()
    {
        ClearSnowmenEvent();
        StartSnowmenInvaders();
    }

    // Dev entry point (/snowmen boss): skip straight to the Abominable Snowman.
    public void ForceSnowmenBoss()
    {
        if (_snowmenPhase == SnowmenPhase.Idle)
            StartSnowmenInvaders();

        SpawnAbominableSnowman();
    }

    // Replays the golden star burst over the chest. The burst composite is a one-shot, so without this the
    // chest settles to a plain glow after the first second.
    private void PulseTreasureStars(DateTime now)
    {
        if (_treasureChest is not { } chest || now < _nextTreasureStarBurst)
            return;

        _nextTreasureStarBurst = now.AddSeconds(TreasureStarBurstIntervalSeconds);

        foreach (var viewer in chest.VisiblePlayers.Values)
        {
            if (_treasureClaimed.Contains(viewer.Guid))
                continue; // they've taken theirs and the chest is gone from their screen

            viewer.SendTunneled(new PlayerUpdatePacketPlayCompositeEffect
            {
                Guid = chest.Guid,
                CompositeEffectId = TreasureStarBurstFxId,
                Position = chest.Position,
            });
        }
    }

    // Re-spawns the current invader wave so changed nameplate settings take effect (they ride the AddNpc).
    public void RespawnSnowmenInvaders()
    {
        if (_snowmenPhase != SnowmenPhase.Invaders)
        {
            ForceStartSnowmenInvaders();
            return;
        }

        var deadline = _snowmenPhaseDeadline;
        DespawnSnowmenInvaders();
        _snowmenRespawnQueue.Clear();
        _invaderRaids.Clear();

        foreach (var pile in SnowballPilePositions)
            for (var i = 0; i < InvadersPerPile; i++)
                if (SpawnSnowmanEnemy(SnowmanInvaderModelId, SnowmanInvaderNameId, "Snowman Invader",
                        SnowmenInvaderLevel, InvaderPost(pile, i)) is { } invader)
                {
                    _snowmenInvaders.Add(invader);
                    BeginInvaderRaid(invader);
                }

        _snowmenPhaseDeadline = deadline;
    }

    // Ground height near a point, taken from the nearest MEASURED position we hold for this area - the
    // snowball piles (each recorded in game with !pos) and the Gifting Tree itself.
    //
    // ★ Deliberately NOT the ".map" navigation graph, which was tried first and is far too coarse here: it
    // carries ~2000 nodes for the whole world, so the nearest one to the Gifting Tree is over 20 units away
    // at height 24.2 where the clearing is really about 27 - snapping to it buried the chest instead of
    // landing it. The pile positions are only metres away and are true standing ground.
    // ★ A SMOOTH ground surface for CONTROLLER actors, as opposed to GroundHeightNear's nearest-sample
    // snap. Nearest-sample is right for placing a one-off prop (a chest lands on real measured ground), but
    // an npc WALKING across the clearing crosses the boundary between samples and its height jumps - which
    // reads as sinking into the snow and popping back out.
    //
    // Inverse-distance weighting over the same samples gives a continuous surface: right on top of a sample
    // it returns that sample exactly, and between two it eases across.
    private float SmoothGroundHeightNear(Vector4 position)
    {
        var samples = new List<Vector4>(SnowballPilePositions) { GiftingTreeCenter };

        var weightedSum = 0f;
        var weightTotal = 0f;

        foreach (var sample in samples)
        {
            var dx = sample.X - position.X;
            var dz = sample.Z - position.Z;
            var distanceSquared = dx * dx + dz * dz;

            if (distanceSquared < 0.01f)
                return sample.Y;   // standing on it

            var weight = 1f / distanceSquared;
            weightedSum += sample.Y * weight;
            weightTotal += weight;
        }

        return weightTotal > 0f ? weightedSum / weightTotal : GroundHeightNear(position);
    }

    private float GroundHeightNear(Vector4 position)
    {
        var best = GiftingTreeCenter.Y;
        var bestDistanceSquared = float.MaxValue;

        void Consider(Vector4 sample)
        {
            var dx = sample.X - position.X;
            var dz = sample.Z - position.Z;
            var distanceSquared = dx * dx + dz * dz;

            if (distanceSquared >= bestDistanceSquared)
                return;

            bestDistanceSquared = distanceSquared;
            best = sample.Y;
        }

        Consider(GiftingTreeCenter);
        foreach (var pile in SnowballPilePositions)
            Consider(pile);

        // ★ ...but those samples only exist AT the tree. The boss is meant to die out on his march, which
        // can be 130 units away, and there the nearest measured sample is the tree's own height - so the
        // chest was planted at clearing height out in a field that isn't at clearing height.
        //
        // The routing graph covers the whole world and its nodes are real walkable ground, so the node
        // nearest the kill site is the right height there. It is offered as one more CANDIDATE rather than
        // replacing the measured samples: the graph is coarse (~2000 nodes world-wide, the nearest to the
        // Gifting Tree is 20+ units away and 3 units too low), so up at the tree the piles still win on
        // distance, which is the accuracy the earlier note was protecting.
        if (TryFindPath(position, GiftingTreeCenter) is { Count: > 0 } route)
        {
            foreach (var node in route)
            {
                // Skip the route's start anchor - that is the query point itself, carrying the very height
                // we are trying to replace.
                if (HorizontalDistance(node, position) < 0.01f)
                    continue;

                Consider(node);
                break; // the route is ordered from here outward, so the first real node is the nearest one
            }
        }

        return best;
    }

    private bool HasPlayersNearGiftingTree() => CountPlayersInBattleArea() > 0;

    // Live players in the Snowhill battle area - the boss's health scales off this.
    private int CountPlayersInBattleArea()
    {
        var count = 0;

        foreach (var player in Players)
        {
            if (player.IsDead)
                continue;

            var dx = player.Position.X - GiftingTreeCenter.X;
            var dz = player.Position.Z - GiftingTreeCenter.Z;

            if (dx * dx + dz * dz <= SnowhillBattleRadius * SnowhillBattleRadius)
                count++;
        }

        return count;
    }

    private void StartSnowmenInvaders()
    {
        _snowmenParticipants.Clear();

        // Despawn, don't just forget - dropping live invaders off the list orphans them the same way a
        // dropped boss is orphaned. Normally a no-op, since a start is preceded by a teardown.
        DespawnSnowmenInvaders();
        DespawnAbominableSnowman();
        _snowmenRespawnQueue.Clear();
        _invaderRaids.Clear();
        _snowmenPhase = SnowmenPhase.Invaders;
        _snowmenPhaseDeadline = DateTime.UtcNow.AddSeconds(SnowmenWaveSeconds);

        // The piles become "Anti-Snowman Snowball Pile" and put their badge up for the duration.
        SetSnowballPileEventState(true);

        // Four invaders ringing EACH snowball pile - the ammo and the fight in the same place.
        foreach (var pile in SnowballPilePositions)
        {
            for (var i = 0; i < InvadersPerPile; i++)
            {
                var post = InvaderPost(pile, i);

                if (SpawnSnowmanEnemy(SnowmanInvaderModelId, SnowmanInvaderNameId, "Snowman Invader",
                        SnowmenInvaderLevel, post) is { } invader)
                {
                    _snowmenInvaders.Add(invader);
                    BeginInvaderRaid(invader);
                }
            }
        }

        // ★ Announced AFTER the spawn, and keyed to a REAL actor. HudMessagePacket's Guid1 is the SOURCE
        // actor, and the one place this packet is known to work (the combat tutorial) passes an npc guid
        // there. It was being sent with the player as their own source, which rendered nothing - that is why
        // the opening announcement never appeared.
        AnnounceToZone(SnowmenWaveMessageId, EnsureAnnouncer());

        _logger.LogInformation("Snowmen Invaders: wave started with {count} invaders across {piles} piles.",
            _snowmenInvaders.Count, SnowballPilePositions.Count);
    }

    private void SpawnAbominableSnowman()
    {
        _snowmenPhase = SnowmenPhase.Boss;

        // Clear out any boss still standing before making another. "/snowmen boss" can be run twice, and a
        // forced restart can cut across a live fight - and a boss dropped without being despawned is
        // ORPHANED: still alive, still marching, but no longer the one _abominableSnowman points at. His
        // death then matches nothing in TryHandleSnowmenKill, falls through to the generic world-enemy
        // branch, and RESPAWNS him at his home position - which for the boss is the Gifting Tree - as an
        // ordinary hostile with none of the event's rules. Kill that one and it happens again, forever.
        DespawnAbominableSnowman();

        // The invaders STAY - the boss arriving is an escalation, not a scene change. They are cleared with
        // everything else once he is beaten (FinishSnowmenInvaders).

        // Spawns out at his own arrival spot, but his HOME is the Gifting Tree - CombatNpc's "walk home" AI
        // (MoveTowards(SpawnPosition)) then marches him at the tree over the real pathfinding graph, which is
        // the walk-up without inventing a second movement system. Anyone he meets on the way aggros him
        // normally, so the march can be interrupted exactly like retail's.
        _abominableSnowman = SpawnSnowmanEnemy(AbominableSnowmanModelId, AbominableSnowmanNameId,
            "Abominable Snowman", AbominableSnowmanLevel, AbominableSnowmanSpawn, scale: 1.4f,
            homePosition: GiftingTreeCenter);

        if (_abominableSnowman is null)
        {
            // Nothing to fight - don't strand the event in a phase that can never complete.
            FinishSnowmenInvaders();
            return;
        }

        if (_abominableSnowman is CombatNpc boss)
        {
            var defenders = CountPlayersInBattleArea();
            var hitpoints = AbominableBaseHitpoints + Math.Max(0, defenders - 1) * AbominableHitpointsPerExtraPlayer;

            boss.MaxHitpoints = hitpoints;
            boss.CurrentHitpoints = hitpoints;
            boss.MaxHealth = hitpoints;
            boss.Health = hitpoints;

            _logger.LogInformation("Snowmen Invaders: Abominable Snowman scaled to {hp} hp for {count} defender(s).",
                hitpoints, defenders);

            // ★ THE MARCH. A real scripted walk (CombatNpc.MarchTarget), NOT "send him home to the tree" -
            // that earlier trick pointed his LEASH ANCHOR 130 units away, so the moment he aggroed he
            // measured himself out of leash and turned round, endlessly. MarchTarget keeps the anchor with
            // him on the way in, so he aggros, fights and pursues normally the whole march, and the tree only
            // becomes his home once he arrives.
            //
            // AlwaysRoute puts every step through the zone's A* graph - the same routing "Take Me There"
            // uses - instead of the cheap "is the straight line clear?" test, which over a cross-map walk
            // just answers yes and sends him ploughing through the terrain.
            boss.MarchTarget = AbominableStandPosition;
            boss.AlwaysRoute = true;

            // ★ RELENTLESS. The fight IS the march: he never stops walking at the tree and never chases, he
            // just swings at whoever steps into reach. Players win by killing him before he arrives, which is
            // what the retail video shows - a crowd backing up in front of him pelting him the whole way.
            // Without this he abandoned the march on first contact and stood around trading hits instead.
            boss.MarchRelentless = true;
            boss.ReturnSpeed = AbominableMarchSpeed;


            boss.AggroRange = AbominableAggroRange;
            boss.AttackRange = AbominableAttackRange;
            boss.LeashRange = AbominableLeashRange;
        }

        // His entrance line: spoken BY him so the client renders "Abominable Snowman: ...", in red, on
        // everyone's screen. Not chat-logged - it's an announcement, not conversation.
        AnnounceSpeech(_abominableSnowman.Guid, AbominableSnowmanTauntId, ChatColorRed);

        _logger.LogInformation("Snowmen Invaders: Abominable Snowman spawned at ({x}, {y}, {z}), marching on the tree.",
            AbominableSnowmanSpawn.X, AbominableSnowmanSpawn.Y, AbominableSnowmanSpawn.Z);
    }

    // He made it. Plant him over the tree, let him gloat, and hold him there for a beat before he vanishes.
    private void BeginAbominableEscape(CombatNpc boss)
    {
        _snowmenPhase = SnowmenPhase.Escaping;
        _snowmenPhaseDeadline = DateTime.UtcNow.AddSeconds(AbominableEscapeSeconds);

        // Stop him dead: no march left to walk, nothing to chase, nowhere to wander. Without all three he
        // would carry on milling about the tree while gloating about having won.
        boss.MarchTarget = null;
        boss.MarchRelentless = false;
        boss.AggroRange = 0f;
        boss.RoamRadius = 0f;
        boss.AggroTarget = null;
        boss.SpawnPosition = boss.Position;
        boss.State = CombatState.Idle;
        boss.BroadcastStop();

        // He has won - snowballs bounce off him now. Killing him during the five seconds he stands gloating
        // would otherwise run the "you beat him" ending (chest, gifts) on top of the failure already in
        // progress.
        boss.Invulnerable = true;

        AnnounceSpeech(boss.Guid, AbominableSnowmanStoleGiftId, ChatColorRed);

        _logger.LogInformation("Snowmen Invaders: the Abominable Snowman reached the tree - event failed, no rewards.");
    }

    // The gloat is over - he disappears in a burst of snow, taking the invasion with him. No chest, no gifts.
    private void FinishAbominableEscape()
    {
        if (_abominableSnowman is { } boss)
        {
            foreach (var viewer in boss.VisiblePlayers.Values)
                viewer.SendTunneled(new PlayerUpdatePacketRemovePlayerGracefully
                {
                    Guid = boss.Guid,
                    Animate = true,                          // play his own despawn clip rather than blinking out
                    Delay = 0,
                    EffectDelay = 0,
                    CompositeEffectId = AbominableEscapeFxId, // a burst of snow as he goes
                    Duration = AbominableEscapeFadeMs,
                });

            TryRemoveNpc(boss.Guid);
            boss.Dispose();
            _abominableSnowman = null;
        }

        _snowmenParticipants.Clear();  // nobody is rewarded for a failed defence
        ClearSnowmenEvent();
    }

    private void FinishSnowmenInvaders()
    {
        _snowmenPhase = SnowmenPhase.Rewarding;
        _snowmenPhaseDeadline = DateTime.UtcNow.AddSeconds(TreasureChestLingerSeconds);

        // Boss down - now the whole invasion clears out.
        DespawnSnowmenInvaders();
        _snowmenRespawnQueue.Clear();
        _invaderRaids.Clear();

        // His parting line, in green - spoken by him so the client prefixes "Abominable Snowman:". Sent
        // before the guid is dropped, since the speaker is what gives the line its name.
        if (_abominableSnowman is { } defeated)
        {
            AnnounceSpeech(defeated.Guid, AbominableSnowmanDefeatId, ChatColorGreen);
            _abominableDeathPosition = defeated.Position;
        }

        _abominableSnowman = null;
        _treasureClaimed.Clear();

        // The chest is the visual payoff from the wiki screenshot - it stands where he fell while the gifts
        // go out, then despawns with the rest of the event.
        if (TryCreateNpc(out var chest))
        {
            chest.IsEventSpawn = true;
            chest.ModelId = TreasureChestModelId;
            chest.NameId = TreasureChestNameId; // "Abominable's Treasure"
            chest.Static = true;
            chest.Visible = true;
            chest.CursorId = 17;               // hand cursor - it's clickable
            chest.InteractAction = OnTreasureChestInteract;
            chest.Scale = _resourceManager.Models.TryGetValue(TreasureChestModelId, out var model) && model.Scale != 0f
                ? model.Scale
                : 1f;
            // ★ Snapped to the GROUND. The boss is a physics actor scaled 1.4, so the Y he happens to be at
            // when he dies is not ground level and the chest was left hanging in the air. The zone's native
            // ".map" navigation graph is real shipped walkable-ground data (the same source the snowball pile
            // positions were taken from), so the nearest node's height is a true floor height here.
            var chestPosition = _abominableDeathPosition != default ? _abominableDeathPosition : GiftingTreeCenter;
            chestPosition = new Vector4(
                chestPosition.X,
                GroundHeightNear(chestPosition) + TreasureGroundOffset,
                chestPosition.Z,
                1f);
            chest.UpdatePosition(chestPosition, SpawnRotation);
            GetTileFromPosition(chestPosition).Entities.TryAdd(chest.Guid, chest);
            PushNpcToNearbyPlayers(chest);
            _treasureChest = chest;

            // The golden shine, attached so it rides the chest and can be pulled off with it.
            if (TreasureShineFxId > 0)
                foreach (var viewer in chest.VisiblePlayers.Values)
                    viewer.SendTunneled(new PlayerUpdatePacketAddEffectTagCompositeEffect
                    {
                        Guid = chest.Guid,
                        TagId = TreasureShineTagId,
                        CompositeEffectId = TreasureShineFxId,
                        SourceGuid = chest.Guid,
                    });
        }

        _logger.LogInformation("Snowmen Invaders: boss down, treasure chest up for claiming.");
    }

    // Clicking the chest: the floating "*You find treasure inside this large golden chest...*" box with its
    // single claim button. CameraFocusParam 0 and no NpcGuid deliberately - that pair is what makes the
    // client frame and lock onto a speaker, and this is a prop, not a conversation. Everyone gets their own
    // claim; a player who already took theirs just isn't offered it again.
    private void OnTreasureChestInteract(Player player)
    {
        if (_treasureChest is null || _treasureClaimed.Contains(player.Guid))
            return;

        var dialog = new CommandPacketShowDialog
        {
            DialogueTextId = TreasureDialogueId,
            NpcGuid = 0,
            CameraFocusParam = 0f,
        };

        dialog.Responses.Add(new CommandPacketShowDialog.Response
        {
            Id = 1,
            LabelTextId = TreasureClaimButtonId,
            Param1 = DialogLeaveImageId,       // the leave arrow - this click ends the exchange
            Param2 = DialogGreenButtonImageSet,
        });

        // The button belongs to the chest, not to a quest conversation - see Player.PendingDialogAction.
        player.PendingDialogAction = () => ClaimTreasure(player);

        player.SendTunneled(dialog);
    }

    // "I claim my reward!" - hand over one Holiday Mystery Gift, which pays out a random item from the
    // Gifting Tree's pool, then take the chest off THIS player's screen. It stays up for everyone else.
    private bool ClaimTreasure(Player player)
    {
        if (!_treasureClaimed.Add(player.Guid))
            return false;

        var itemId = RollMysteryGiftItem();
        _questManager.GrantItem(player, itemId);
        player.SendTunneled(new RewardNonBundledItemPacket { ItemDefinitionId = itemId, Quantity = 1 });

        // Gracefully for the claimer only - the chest is shared, so it can't just be despawned.
        if (_treasureChest is { } chest)
        {
            player.SendTunneled(new PlayerUpdatePacketRemoveEffectTagCompositeEffect
            {
                Guid = chest.Guid,
                TagId = TreasureShineTagId,
            });

            // ★ CompositeEffectId 0, NOT the shine. Passing the shine here re-played the golden loop as the
            // despawn effect - world-anchored this time, and a world-anchored composite never auto-cleans, so
            // the glow stayed behind forever exactly where the chest had been. That is the "effect doesn't
            // disappear after claiming" bug: the remove packet was putting it back.
            player.SendTunneled(new PlayerUpdatePacketRemovePlayerGracefully
            {
                Guid = chest.Guid,
                Animate = true,
                Delay = 0,
                EffectDelay = 0,
                CompositeEffectId = 0,
                Duration = 1000,
            });
        }

        _logger.LogInformation("Snowmen Invaders: {who} claimed the treasure (item {item}).", player.Name, itemId);
        return true;
    }

    // One item at random out of the Gifting Tree quest's mystery-gift pool - the same set that quest pays
    // out, read from the quest so there is only one copy of the list. Falls back to the wrapped gift itself
    // if the quest isn't loaded.
    private int RollMysteryGiftItem()
    {
        if (_resourceManager.Quests.TryGet(MysteryGiftPoolQuestId, out var quest) && quest.RandomRewardItems.Count > 0)
            return quest.RandomRewardItems[Random.Shared.Next(quest.RandomRewardItems.Count)];

        return HolidayMysteryGiftItemId;
    }

    // Swaps the snowball piles between their year-round selves and their invasion selves ("Anti-Snowman
    // Snowball Pile", wearing the snowball badge).
    //
    // ★ A SWAP, not a rename. The name travels in the AddNpc and a client keeps the name it was first given,
    // so the only way to relabel a pile in place is to drop the actor and re-add it - and this codebase
    // already learned that remove+re-add of the SAME guid races and can leave the actor gone for good (see
    // QuestManager.HideQuestCollectibles' note). That is exactly what happened: the piles vanished for the
    // whole invasion. So the event stands up its OWN piles and hides the permanent ones. No guid is ever
    // removed and re-added, so there is no race to lose.
    //
    // Which also settles the "same piles or different ones?" question in the direction the client data
    // hints at: there are two names in the locale table because there are two sets of piles.
    private void SetSnowballPileEventState(bool active)
    {
        if (active)
        {
            foreach (var pile in SnowballPiles)
            {
                // Visible=false keeps the tile-reveal sweep from handing the permanent pile to anyone who
                // walks up (or logs in) mid-battle - the client-side removal below only reaches the players
                // who can already see it.
                pile.Visible = false;

                foreach (var viewer in new List<Player>(pile.VisiblePlayers.Values))
                    viewer.SendTunneled(new PlayerUpdatePacketRemovePlayer { Guid = pile.Guid });

                if (CreateSnowballPile(pile.Position, pile.Rotation, AntiSnowmanPileNameId, SnowballPileBadgeImageId)
                    is not { } eventPile)
                    continue;

                eventPile.IsEventSpawn = true;
                _eventSnowballPiles.Add(eventPile);
                PushNpcToNearbyPlayers(eventPile);
            }

            return;
        }

        foreach (var eventPile in _eventSnowballPiles)
        {
            foreach (var viewer in new List<Player>(eventPile.VisiblePlayers.Values))
            {
                viewer.SendTunneled(new PlayerUpdatePacketRemoveNotifications { Guids = { eventPile.Guid } });

                // Its own sparkle tag, not the chest's - an attached composite outlives the actor it rode on.
                viewer.SendTunneled(new PlayerUpdatePacketRemoveEffectTagCompositeEffect
                {
                    Guid = eventPile.Guid,
                    TagId = SnowballTool.PileSparkleTagId,
                });
            }

            DespawnSnowmanNpc(eventPile);
        }

        _eventSnowballPiles.Clear();

        // The ordinary piles come back. Forced, because the permanent pile was only ever removed on the
        // CLIENT - server-side those players are still listed as seeing it, so the usual "skip anyone who
        // already has it" push would skip every one of them and leave them staring at empty ground.
        foreach (var pile in SnowballPiles)
        {
            pile.Visible = true;
            PushNpcToNearbyPlayers(pile, force: true);
        }
    }

    // Removes the boss from the world if one is standing. Every path that stops pointing at him has to go
    // through here - see the orphan note in SpawnAbominableSnowman.
    private void DespawnAbominableSnowman()
    {
        if (_abominableSnowman is not { } boss)
            return;

        _abominableSnowman = null;

        if (boss.IsAlive)
            DespawnSnowmanNpc(boss);
    }

    private void ClearSnowmenEvent()
    {
        DespawnSnowmenInvaders();

        // The boss too. This is the teardown a forced restart runs, so leaving him out of it was how a live
        // boss got orphaned into the world-enemy respawn loop.
        DespawnAbominableSnowman();

        if (_treasureChest is { } chest)
        {
            DespawnSnowmanNpc(chest);
            _treasureChest = null;
        }

        if (_announcer is { } announcer)
        {
            DespawnSnowmanNpc(announcer);
            _announcer = null;
        }

        // The piles go back to being ordinary "Snowball Pile"s and drop their badge. Done here rather than
        // at the boss's death so it covers every way the event can end - won, failed, or force-restarted.
        SetSnowballPileEventState(false);

        _snowmenRespawnQueue.Clear();
        _invaderRaids.Clear();
        _snowmenOrigins.Clear();
        _snowmenParticipants.Clear();
        _snowmenPhase = SnowmenPhase.Idle;
        _snowmenNextStart = DateTime.UtcNow.AddSeconds(SnowmenEventIntervalSeconds);
    }

    private void DespawnSnowmenInvaders()
    {
        // Snapshot for the same reason as UpdateInvaderRaids: despawning can route back into the kill
        // path, which removes from this list.
        foreach (var invader in _snowmenInvaders.ToArray())
            DespawnSnowmanNpc(invader);

        _snowmenInvaders.Clear();
    }

    private void DespawnSnowmanNpc(Npc npc)
    {
        foreach (var player in npc.VisiblePlayers.Values)
        {
            // Pull any attached effect off FIRST - an attached composite outlives the actor it rode on.
            player.SendTunneled(new PlayerUpdatePacketRemoveEffectTagCompositeEffect
            {
                Guid = npc.Guid,
                TagId = TreasureShineTagId,
            });

            player.SendTunneled(new PlayerUpdatePacketRemovePlayerGracefully { Guid = npc.Guid, Animate = false });
        }

        TryRemoveNpc(npc.Guid);
        npc.Dispose();
    }

    // A single event enemy. Same shape as RespawnWorldEnemy's combat NPCs (hostile, damageable, health bar,
    // physics movement) but kept on our own list so the event owns their lifecycle - in particular they must
    // NOT go through the world-enemy respawn-at-post path when killed.
    private Npc? SpawnSnowmanEnemy(int modelId, int nameId, string name, int level, Vector4 position, float scale = 1f,
        Vector4? homePosition = null)
    {
        if (!TryCreateCombatNpc(out var enemy))
            return null;

        enemy.IsEventSpawn = true;   // the event owns its lifecycle - never world-enemy respawn fodder
        enemy.ModelId = modelId;
        enemy.NameId = nameId;
        enemy.Name = name;
        enemy.Static = false;
        enemy.Scale = scale;
        enemy.Visible = true;
        enemy.EnemyStatus = true;
        enemy.ActiveProfile = 1;
        enemy.CursorId = 11;              // crossed-swords attack cursor
        enemy.IsInteractable = false;     // a combat target, not a talkable NPC
        enemy.ShowHealthBar = true;
        enemy.MovementType = InvaderMovementType;   // see InvaderMovementType - CONTROLLER for the invaders

        // ★★ WITHOUT THIS THE CLIENT DISCARDS EVERY POSITION UPDATE WE SEND. OnPlayerUpdatePosition ignores
        // any actor whose rider is not the invalid-guid sentinel (see Npc.RiderGuid), and these snowmen were
        // left at the default 0 - so the single-destination glide, the facing and the ground clamp were all
        // being thrown away by the client, which is exactly why changing them appeared to do nothing.
        //
        // Every other zone already does this: EncounterArenaZone sets it on all four of its npc spawns,
        // CombatEncounterZone on its pickups, and ProjectileNpc's own comment says "else op125 is ignored".
        enemy.RiderGuid = ulong.MaxValue;

        enemy.InitializeFromLevel(level, EnemyTiers.FromName(name));

        // ★ SNOWBALLS ONLY. The whole event is a snowball fight, so weapons and abilities do nothing to
        // either the invaders or the boss - a passing archer can't shortcut the wave, and a combat job can't
        // accidentally delete the boss before anyone has thrown anything.
        enemy.SnowballOnly = true;

        if (modelId != AbominableSnowmanModelId)
        {
            // Amble around their post instead of standing to attention. The boss doesn't roam - he has
            // somewhere to be.
            enemy.RoamRadius = InvaderRoamRadius;
            enemy.RoamSpeed = InvaderRoamSpeed;
            enemy.CombatSpeed = InvaderMoveSpeed;
            enemy.ReturnSpeed = InvaderMoveSpeed;

            // ★★ ZERO AGGRO, SET AT SPAWN. The invaders never chase: retail has them walk to the tree,
            // grab a present and run off, while players knock them down on the way.
            //
            // MarchRelentless alone is NOT enough. It only suppresses targeting while a MarchTarget is set
            // (CombatNpc: `if (MarchRelentless && MarchTarget is {} ...) AggroTarget = null`), and the march
            // target goes null the moment they ARRIVE - so during the grab at the tree, and again after the
            // getaway, they were free to lock onto whoever was closest and chase. Setting the range to 0
            // means FindClosestPlayer can never return anyone, at any stage.
            //
            // Set here rather than only in BeginInvaderRaid because that runs on the NEXT tick, which left a
            // window where a freshly spawned invader could grab a target and keep it.
            enemy.AggroRange = 0f;
            enemy.MarchRelentless = true;
            enemy.AggroTarget = null;

            // No health bar. ShowHealthBar stops every packet that CARRIES health (the stat push, the HP
            // broadcast, the snowball's hit feedback) - but the bar drawn on the plate itself is not one of
            // those. Traced 2026-08-13: the AddNpc bool labelled "Health bar" (Unknown41) is never read by
            // the client's AddNpc apply or the ProxiedCharacter ctor at all, so that label is a guess and it
            // controls nothing. What DOES mark an actor as an enemy - red name AND the enemy plate that
            // carries the bar - is EnemyStatus, which is ground-truthed from a real 04-01 capture.
            //
            // Kept because they are correct in their own right (these are not combat targets), even though
            // none of them removes the bar - see the nameplate note at the top of this file.
            // Health data IS transmitted, so the bar (which can't be removed - it comes from the model's
            // race, see the nameplate note above) at least behaves properly and drains as they take snowballs
            // rather than sitting there full.
            enemy.ShowHealthBar = true;
            enemy.EnemyStatus = false;
            enemy.ActiveProfile = 0;

            // The plate stays - the name is wanted. NameColor is what actually delivers the blue: a non-zero
            // value bypasses the client's colour resolver entirely.
            enemy.HideNamePlate = false;
            enemy.IsInteractable = false;

            // ★ NO CURSOR. CursorId 11 is the crossed-swords ATTACK cursor, set for every combat npc at the
            // top of this method - but these carry no hitpoints now, so there is nothing to attack and the
            // cursor was the only thing still saying "click me". Snowballs pick their target by RANGE, never
            // by client targeting, so nothing needs a cursor here.
            enemy.CursorId = 0;

            // Neutral, matching the Snowball Pile. Snowball targeting no longer keys on IsHostile (see
            // SnowballTool.IsThrowable), so this cannot make them unhittable, and their AI ignores it.
            enemy.Disposition = 1;
            enemy.NameColor = InvaderNameColor;
            enemy.ClientDisposition = 1;

            // ★ REAL hitpoints, so the (model-driven, unavoidable) health bar actually means something and
            // drains as they take snowballs. Sized directly off the snowball's damage x the hits it should
            // take, so "one snowball away from death" stays true even if the throw is retuned - and so the
            // last hit lands exactly on 0 rather than leaving a sliver.
            var invaderHitpoints = SnowballTool.NpcDamage * InvaderSnowballHitsToKill;
            enemy.MaxHealth = invaderHitpoints;
            enemy.MaxHitpoints = invaderHitpoints;
            enemy.CurrentHitpoints = invaderHitpoints;
            enemy.Harmless = true;

            // Their own retail bark. Deliberately sparse: sixteen snowmen on the default 25s greeter cooldown
            // is a constant wall of "Grr!", so the window is stretched and only a minority of eligible barks
            // actually fire.
            // Frequent enough to read as a taunting crowd, rare enough per-snowman that sixteen of them
            // aren't a wall of noise. Each has its OWN cooldown, so the wave chatters steadily while any one
            // of them stays quiet for a while.
            enemy.AmbientLineIds = SnowmanBarkLineIds;
            enemy.AmbientGreetCooldownMs = 20_000;
            enemy.AmbientGreetChancePercent = 60;
        }
        else
        {
            enemy.Harmless = true;
        }
        enemy.Speed = enemy.CombatSpeed;
        enemy.MaxHealth = enemy.MaxHitpoints;
        enemy.Health = enemy.CurrentHitpoints;

        // SpawnPosition is the npc's HOME, not necessarily where it appears - CombatNpc walks back to it
        // whenever it has no aggro target, which is what gives the boss his march on the tree.
        var home = homePosition ?? position;
        var facing = FacingDirection(position, home);
        enemy.SpawnPosition = home;
        enemy.SpawnRotation = facing;
        enemy.LastSentPosition = position;
        enemy.UpdatePosition(position, facing);

        // Invaders converge on the Gifting Tree and then roam around it. Each gets its OWN point on a ring
        // so they spread out instead of stacking on the trunk; on arrival MarchTarget clears and that point
        // becomes their roam anchor (see CombatNpc.UpdateMarch).
        if (modelId != AbominableSnowmanModelId)
        {
            var angle = MathF.Tau * Random.Shared.NextSingle();
            var radius = TreeGatherRadius * (0.5f + Random.Shared.NextSingle() * 0.5f);
            enemy.MarchTarget = new Vector4(
                GiftingTreeCenter.X + MathF.Cos(angle) * radius,
                GiftingTreeCenter.Y,
                GiftingTreeCenter.Z + MathF.Sin(angle) * radius,
                1f);
        }

        if (modelId != AbominableSnowmanModelId)
            _snowmenOrigins[enemy.Guid] = position;

        GetTileFromPosition(position).Entities.TryAdd(enemy.Guid, enemy);
        PushNpcToNearbyPlayers(enemy);

        // ★ NAME COLOUR + NO BAR, in one lever: DISPOSITION.
        //
        // The client's nameplate colour resolver (ProxiedCharacter::sub_966460) picks the colour from
        // disposition whenever NameColor is 0: hostile (0) paints RED and draws the enemy plate that carries
        // the health bar; anything else gets the bluish default 0xFF6699CC and the ordinary name-only plate.
        // CombatNpc's ctor defaults to hostile, which is where the red name and the bar were both coming from.
        //
        // It MUST be sent as op35/28 - the AddNpc Disposition field is discarded client-side (the apply takes
        // it from a global arena flag), which is why setting it on the spawn changed nothing. Sent after the
        // spawn push so the actor exists, and it works because ActiveProfile is non-zero: that is what makes
        // the resolver re-run after the AddNpc apply instead of keeping the ctor's colour.
        return enemy;
    }

    // Mid-session spawns aren't picked up by the load-time visibility sweep, so push them to everyone who can
    // already see the tile - otherwise they exist server-side and render for nobody (the same trap
    // RespawnWorldEnemy documents).
    private void PushNpcToNearbyPlayers(Npc npc, bool force = false)
    {
        var tile = GetTileFromPosition(npc.Position);

        foreach (var player in Players)
        {
            // force: the npc was taken off these clients' screens directly, so server-side visibility still
            // says they can see it and the usual skip would send them nothing.
            if (!force && npc.VisiblePlayers.ContainsKey(player.Guid))
                continue;

            var playerTile = GetTileFromPosition(player.Position);
            if (playerTile == tile || playerTile.VisibleTiles.Contains(tile))
            {
                player.OnAddVisibleNpcs([npc]);
                npc.OnAddVisiblePlayers(player);
            }
        }
    }

    // True when this kill belongs to the event, in which case OnNpcKilled must NOT run the world-enemy
    // respawn-at-post path - these are event spawns and the event owns when they come back.
    private bool TryHandleSnowmenKill(Player killer, Npc npc)
    {
        // Keyed on the npc's OWN flag - NOT on the event's phase, and NOT on whether it is still in the
        // event's lists. Both of those were tested here before, and both could answer "not mine" about an
        // npc this event had plainly created: a spawn the event lost track of, or any event spawn killed
        // after the battle had already ended. Either answer sent it down the world-enemy branch to be
        // respawned at its home post forever. An event spawn is the event's to clean up in every case, even
        // one it is no longer tracking - the worst outcome there is that it simply stays dead.
        if (!npc.IsEventSpawn)
            return false;

        var wasInWave = _snowmenInvaders.Remove(npc);
        var isInvader = npc.NameId == SnowmanInvaderNameId;

        // ★ The dead boss is deliberately NOT dropped here. FinishSnowmenInvaders still needs him: his guid
        // is what gives the "Nooo! You can't stop me!" line its speaker, and his position is where the
        // treasure chest stands. The Boss-phase tick spots the death via !IsAlive and clears him there.

        // ★ Read the post BEFORE forgetting it. SnowmanPostOf falls back to "wherever it died" when the
        // origin is gone, so clearing the entry first silently turned every replacement into a spawn at the
        // kill site - out where the invader had wandered or chased to, instead of back at its pile.
        var post = SnowmanPostOf(npc);

        // Bookkeeping for a spawn that outlived its wave is dropped, but nothing else happens for it - no
        // credit, no coal, no replacement.
        _snowmenOrigins.Remove(npc.Guid);

        if (_snowmenPhase == SnowmenPhase.Idle)
            return true;

        _snowmenParticipants.Add(killer.Guid);

        if (isInvader)
        {
            // Killed invaders keep coming back until the wave clock runs out - hold its post and refill it
            // after a short grace period (see RefillSnowmenPosts). Only for one of the CURRENT wave, so a
            // straggler from an earlier battle can't grow this one.
            if (wasInWave && _snowmenPhase is SnowmenPhase.Invaders or SnowmenPhase.Boss)
                _snowmenRespawnQueue.Add((post, DateTime.UtcNow.AddSeconds(InvaderRespawnSeconds)));

            // "Occasionally, you will receive 1-2 Snowman Coal when you defeat one" - the currency for the
            // repeatable Snowman Showdown quest.
            if (Random.Shared.Next(100) < CoalDropPercent)
            {
                var quantity = Random.Shared.Next(1, 3);
                _questManager.GrantItem(killer, SnowmanCoalItemId, quantity);
                killer.SendTunneled(new RewardNonBundledItemPacket { ItemDefinitionId = SnowmanCoalItemId, Quantity = quantity });
            }
        }

        return true;
    }

    // Credits a player for helping even when they aren't the one who lands the killing blow - snowballs stun
    // rather than kill, so a thrower would otherwise never make the reward list.
    // Credits a player for helping. Damage and death are handled by the NORMAL path now, so that the health
    // bar drains as they are hit - this only records who took part.
    public void OnSnowmenDamaged(Player attacker, Npc npc)
    {
        if (_snowmenPhase == SnowmenPhase.Idle)
            return;

        if (_snowmenInvaders.Contains(npc) || (_abominableSnowman is not null && ReferenceEquals(npc, _abominableSnowman)))
            _snowmenParticipants.Add(attacker.Guid);
    }

    // Where a downed invader should be replaced: its assigned post if it has one (CombatNpc keeps the spot
    // it was spawned at), else wherever it fell.
    private Vector4 SnowmanPostOf(Npc npc) =>
        _snowmenOrigins.TryGetValue(npc.Guid, out var origin) ? origin : npc.Position;

    // The client's "rotation" is a facing DIRECTION packed as (dirX, 0, dirZ, 0), not a quaternion - so the
    // boss faces the tree he is walking toward instead of the zone's default heading.
    private static Quaternion FacingDirection(Vector4 from, Vector4 to)
    {
        var dx = to.X - from.X;
        var dz = to.Z - from.Z;
        var len = MathF.Sqrt(dx * dx + dz * dz);
        return len < 0.0001f ? new Quaternion(1f, 0f, 0f, 0f) : new Quaternion(dx / len, 0f, dz / len, 0f);
    }

    // NPC speech shown to the whole zone: spoken BY the npc (the client prefixes its name) and optionally
    // coloured. IsChatLogged stays false - retail keeps event barks out of the chat log.
    // ★ BUBBLE ONLY - no chat-log line. AnnounceSpeech is for the event's ANNOUNCEMENTS (the referee's
    // victory call, the Abominable's taunts), which are deliberately coloured and belong in the chat log.
    // An invader muttering as it grabs a gift is ambient flavour: it should appear over its head and
    // nowhere else, or a wave of snowmen fills the chat window.
    //
    // The difference is HasColor. A coloured line is treated as an announcement and logged; a plain one is
    // just speech, and with IsChatLogged=false it renders as the overhead bubble alone.
    private void AnnounceSpeech(ulong speakerGuid, int stringId, int colorId)
    {
        foreach (var player in Players)
        {
            player.SendTunneled(new ChatPacketFromStringId
            {
                SpeakerGuid = speakerGuid,
                StringId = stringId,
                IsChatLogged = false,
                HasColor = true,
                ColorId = colorId,
            });
        }
    }

    // A post for invader #index around a snowball pile - evenly spaced so they surround it.
    private static Vector4 InvaderPost(Vector4 pile, int index)
    {
        var angle = MathF.Tau * index / InvadersPerPile;
        return new Vector4(
            pile.X + MathF.Cos(angle) * InvaderPileRadius,
            pile.Y,
            pile.Z + MathF.Sin(angle) * InvaderPileRadius,
            1f);
    }

    // Bring back invaders whose grace period has elapsed. Only refills posts vacated by a KILL - the wave
    // keeps its shape instead of growing, and the pressure stays constant until the boss arrives.
    // ── The invader raid ────────────────────────────────────────────────────────────────────────────
    // Retail behaviour (user, from the live game): the invaders DO NOT fight the players at all. Each one
    // walks to the Gifting Tree, plays a grab at it, then runs off with a present and vanishes when it gets
    // clear of the clearing. Players knock them down with snowballs on the way - the snowmen never chase.
    private enum InvaderStage
    {
        Approaching,   // walking to the tree
        Stealing,      // at the tree, playing the grab
        Fleeing,       // running off with a present
    }

    private sealed class InvaderRaid
    {
        public InvaderStage Stage;
        public DateTime StageAt;
        public Vector4 GatherAt;   // where at the tree this one stops
        public Vector4 FleeTo;     // where it runs to before vanishing
    }

    private readonly Dictionary<ulong, InvaderRaid> _invaderRaids = [];

    // ★★ 2001 = `amb_oneshot_01`, which on THIS model is literally `snowman_amb_pick_up_present.gr2`.
    //
    // Settled by extracting snowman_present.adr from AssetsW_002.pack and reading its animation slot list.
    // The model has exactly SEVEN clips, and it was authored for precisely this event:
    //     loc_stand           -> snowman_loc_stand_with_present.gr2
    //     loc_run             -> snowman_loc_run_without_present.gr2
    //     com_death_01        -> snowman_present_com_death_01.gr2
    //     com_death_static_01 -> snowman_present_com_death_static_01.gr2
    //     com_spawn           -> snowman_com_spawn_present.gr2
    //     amb_oneshot_01      -> snowman_amb_pick_up_present.gr2     <- the grab
    //     amb_loop_01         -> snowman_loc_run_with_present.gr2    <- the getaway, present in hand
    //
    // That is why every earlier id did nothing: emo_*/scr_* are player-rig clips, and 1099 com_swing (the
    // id CombatNpc.ExplicitAttackAnimByModel records for 1907) is not in this model's list at all - that
    // mapping never rendered.
    public static int InvaderStealAnimationId { get; set; } = 2001;

    // ★ The getaway clip, and the answer to "they aren't holding a gift": the present is part of
    // `snowman_loc_run_with_present.gr2`, so carrying it IS the animation. amb_loop_01 loops, which is what
    // a run cycle needs.
    // ★ 2100 = amb_loop_01, which on THIS model is `snowman_loc_run_with_present.gr2` - a RUN CYCLE that
    // already includes the present. It is exactly "walking animation + holding the gift" in one clip; the
    // model was authored that way.
    //
    // It only ever failed because the client kept overriding it with loc_run (run WITHOUT present) while
    // the npc was moving. With the speed stream suppressed the client never enters that locomotion state,
    // so this clip survives - which is the combination that showed the present staying in hand.
    //
    // loc_stand (1) also carries a present, but it is a STANDING pose - holding that while sliding home is
    // why they looked like they were gliding motionless.
    public static int InvaderFleeAnimationId { get; set; } = 2100;

    // ★ The movement state broadcast while carrying a present. CombatNpc sends 2 ("run") on every position
    // update and the client answers with its locomotion clip - which on this model is loc_run =
    // snowman_loc_run_WITHOUT_present, stripping the gift. 0 asks it not to force one. Which value actually
    // preserves the carry clip is not established, so `/snowmen movestate <n>` switches it live.
    // 1 = WALK. Deliberate: the model has no loc_walk, so the client must fall back - and loc_stand, the
    // likeliest fallback, is the with-present pose. 2 = run resolves to the without-present clip.
    public static byte InvaderFleeMovingState { get; set; } = 1;

    // ★ MovementType while carrying a present. 2 = PHYSICS (grounded) is what they normally use, and a
    // physics actor animates its OWN locomotion client-side - which is what replaces the carry clip with
    // loc_run (run WITHOUT present) the moment they set off. 1 = CONTROLLER is server-interpolated and does
    // not drive a locomotion clip, so the held animation should survive the run.
    //
    // Caveat from the spawn code: CONTROLLER "leaves them flying" - it does not stick actors to the ground.
    // Over the short, flat run back to the piles that may be fine; if they hover, put this back to 2.
    // `/snowmen fleemovetype <n>` switches it live.
    // ★★ MOVEMENT TYPE AT **SPAWN** - the only place it counts, because it is an AddNpc field and changing
    // it on a live npc never reaches the client (which is why the earlier "switch to CONTROLLER for the
    // getaway" attempt measured nothing).
    //
    // 1 = CONTROLLER: server-interpolated, like a projectile - it moves smoothly AND does not drive its own
    // locomotion clip. That is the combination we need, because a PHYSICS actor's self-animated `loc_run`
    // (= run WITHOUT present) is what keeps overwriting the carry clip.
    //
    // With CONTROLLER the animation is entirely ours to choose, so each stage holds its own clip:
    //     approach -> 3    loc_run        = snowman_loc_run_without_present
    //     grab     -> 2001 amb_oneshot_01 = snowman_amb_pick_up_present
    //     getaway  -> 2100 amb_loop_01    = snowman_loc_run_WITH_present
    //
    // Known risk: CONTROLLER is not ground-clamped ("leaves them flying"). The server drives Y from its own
    // pathing so it should sit right on this flat ground; `/snowmen movetype 2` puts them back to PHYSICS.
    // ★ 2 = PHYSICS. Settled after a long run of attempts at the alternative.
    //
    // CONTROLLER (1) is the only mode where the present stays in hand while moving - a controller actor is
    // server-interpolated and does not animate its own locomotion, so the server owns the clip. It was
    // live-confirmed working for the GIFT. But it never looked right: the snowmen floated or sank, and
    // their facing was wrong regardless of what was sent. Fixed along the way and still not enough:
    //   * RiderGuid was unset, so the client was DISCARDING every position update (see Npc.RiderGuid) -
    //     a real bug, now fixed, and the reason several earlier attempts appeared to do nothing;
    //   * ground height was snapping between measured samples (now smoothed, SmoothGroundHeightNear);
    //   * rotation was sent as a half-angle quaternion where the zones use a direction vector;
    //   * movement was streamed per-tick instead of ProjectileNpc's single-destination glide.
    // Even with all four corrected the result was still floating and mis-facing, so PHYSICS wins on looks.
    //
    // The cost is the gift: a physics actor animates its own `loc_run` (= run WITHOUT present) over
    // anything we send, so it is not visible on the way back. Everything for the controller route is still
    // here and one command away - `/snowmen movetype 1` - if it is ever worth revisiting.
    public static int InvaderMovementType { get; set; } = 2;

    // The clip held while walking IN, now that we own the animation instead of the client.
    public static int InvaderApproachAnimationId { get; set; } = 3;

    // Facing correction for CONTROLLER-driven invaders, in DEGREES. A controller actor is oriented only by
    // the rotation the server sends, and the client applies it differently from a physics actor - so the
    // heading that looked right under physics can be off by a fixed amount here. `/snowmen facing <deg>`.
    public static float InvaderHeadingOffsetDegrees { get; set; }

    // ★ Pin the streamed speed to 0 during the getaway. A physics actor animates its own run cycle off the
    // speed the server streams, which is what keeps replacing the carry clip with loc_run (run WITHOUT the
    // present). With no speed, the client has no locomotion to play and the held ambient loop survives.
    // They still travel - position updates continue - they just glide instead of animating a run, which is
    // fine because the CLIP is itself a run cycle. `/snowmen fleeslide on|off`.
    // ★ ON. This is the one thing that demonstrably keeps the present in hand: with no streamed speed the
    // client never enters a locomotion state, so it holds the idle (with-present) pose instead of swapping
    // to loc_run. The cost is that it also stops interpolating, so the pace below has to stay low enough
    // that each position update is a small step rather than a visible jump.
    public static bool InvaderFleeSuppressSpeed { get; set; }

    public static int InvaderStealMs { get; set; } = 2_500;

    // They amble in and RUN out - the getaway should read as fleeing, not more lumbering.
    // ★ Deliberately slow while the speed stream is suppressed. With no ExpectedSpeed the client cannot
    // interpolate between position updates - it snaps to each one - so at ~10 ticks/sec a fast getaway
    // arrives as visible ~0.65-unit jumps. Halving the pace halves the jump, which reads as a glide rather
    // than teleporting; the clip itself is a run cycle, so it still looks like running.
    // `/snowmen fleespeed <n>` retunes it.
    // ★ Slowed to a walk. Partly because it suits a walk state rather than a sprint, and partly because a
    // fallback-to-idle pose looks far more natural at walking pace than at a run. `/snowmen fleespeed <n>`.
    public static float InvaderFleeSpeed { get; set; } = 3.2f;

    private const int InvaderVanishFxId = 15799;   // PFX_snow_explosion_large, same as the boss's exit
    private const int InvaderVanishFadeMs = 1_200;

    // How close counts as arrived. Matches the boss's own march test - HORIZONTAL only, because the tree
    // point carries the tree's height while they walk on the ground below it.
    private const float InvaderArriveDistance = 6f;

    // ★ Where an invader runs off to: its spawn post, pushed FURTHER out along the line away from the tree.
    // Stopping at the post looked like it was just going home; carrying on past it reads as making off with
    // the loot and leaving the clearing. `/snowmen fleedistance <n>` retunes how far past.
    public static float InvaderFleeExtraDistance { get; set; } = 30f;

    private Vector4 FleePointFrom(Vector4 post)
    {
        var dx = post.X - GiftingTreeCenter.X;
        var dz = post.Z - GiftingTreeCenter.Z;
        var length = MathF.Sqrt(dx * dx + dz * dz);

        if (length < 0.001f)
            return post;

        // Straight out from the tree, through the post, and onward.
        var distance = length + InvaderFleeExtraDistance;

        return new Vector4(
            GiftingTreeCenter.X + dx / length * distance,
            post.Y,
            GiftingTreeCenter.Z + dz / length * distance,
            1f);
    }

    // Where at the tree this invader stops - on its own side, so a wave fans around the trunk instead of
    // stacking on one spot.
    private Vector4 GatherPointNearTree(Vector4 approachFrom)
    {
        var dx = approachFrom.X - GiftingTreeCenter.X;
        var dz = approachFrom.Z - GiftingTreeCenter.Z;
        var length = MathF.Sqrt(dx * dx + dz * dz);

        if (length < 0.001f)
            return GiftingTreeCenter;

        return new Vector4(
            GiftingTreeCenter.X + dx / length * TreeGatherRadius,
            GiftingTreeCenter.Y,
            GiftingTreeCenter.Z + dz / length * TreeGatherRadius,
            1f);
    }

    // Send a freshly spawned invader at the tree.
    private void BeginInvaderRaid(Npc invader)
    {
        var raid = new InvaderRaid
        {
            Stage = InvaderStage.Approaching,
            StageAt = DateTime.UtcNow,
            GatherAt = GatherPointNearTree(invader.Position),
            FleeTo = FleePointFrom(invader.Position),
        };

        _invaderRaids[invader.Guid] = raid;

        if (invader is not CombatNpc walker)
            return;

        // ★ NEVER CHASE. AggroRange 0 plus MarchRelentless means players are simply scenery to them - they
        // walk their route and take snowballs on the way, which is the whole event.
        walker.AggroRange = 0f;
        walker.MarchRelentless = true;
        walker.AlwaysRoute = true;
        walker.MarchTarget = raid.GatherAt;
        walker.CombatSpeed = InvaderMoveSpeed;
        walker.ReturnSpeed = InvaderMoveSpeed;
        walker.RoamRadius = 0f;   // no milling about - they have somewhere to be
        // ★ Hold the RUN clip on the way in too. Left to itself the client blends its own locomotion off
        // the npc's pace, which at the invaders' amble reads as a trudge rather than a charge on the tree.
        // Same sticky mechanism the carry clip uses - re-sent right after each position broadcast.
        // 3 = loc_run = snowman_loc_run_without_present, correct for the approach: no gift yet.
        walker.StickyAnimationId = InvaderApproachAnimationId;
        walker.BaseAnimationId = InvaderApproachAnimationId;
        SendInvaderAnimation(walker, InvaderApproachAnimationId);
        // Both of these exist for CONTROLLER actors only: physics npcs are ground-clamped and faced by the
        // client already, and forcing our own values on them just fights it.
        if (InvaderMovementType != 2)
        {
            walker.HeadingOffset = InvaderHeadingOffsetDegrees * MathF.PI / 180f;
            walker.GroundHeight = SmoothGroundHeightNear;

            // A controller actor is faced only by what we send, and the direction form is what this zone
            // uses everywhere it sets a heading by hand.
            walker.DirectionStyleRotation = true;
        }


    }

    private void UpdateInvaderRaids(DateTime now)
    {
        // Snapshot: a raid that finishes its getaway vanishes the invader from inside this loop
        // (GlideInvaderHome -> VanishInvader -> _snowmenInvaders.Remove), and the delayed getaway does the
        // same from a timer thread. Enumerating the live list threw "Collection was modified" every tick,
        // which killed the zone's per-second update.
        foreach (var invader in _snowmenInvaders.ToArray())
        {
            if (!invader.IsAlive)
                continue;

            if (!_invaderRaids.TryGetValue(invader.Guid, out var raid))
            {
                BeginInvaderRaid(invader);
                continue;
            }

            if (invader is not CombatNpc walker)
                continue;

            switch (raid.Stage)
            {
                case InvaderStage.Approaching:
                    if (walker.MarchTarget is null &&
                        HorizontalDistance(walker.Position, raid.GatherAt) < InvaderArriveDistance)
                    {
                        raid.Stage = InvaderStage.Stealing;
                        raid.StageAt = now;
                        walker.BroadcastStop();   // stop first - a moving npc's locomotion clip wins
                        PlayInvaderSteal(walker);
                    }
                    break;

                case InvaderStage.Stealing:
                    if (now >= raid.StageAt.AddMilliseconds(InvaderStealMs))
                    {
                        raid.Stage = InvaderStage.Fleeing;
                        raid.StageAt = now;

                        _logger.LogInformation("Snowmen: invader {guid} got a present - running for it.", walker.Guid);

                        // ★★ THE WAY BACK IS EXACTLY THE WAY IN. Same march, same physics movement, same
                        // speed handling - the approach walk already looks right, so the getaway just
                        // reuses it instead of inventing a second movement path.
                        //
                        // What that costs: the client animates a physics mover's own locomotion, and this
                        // model's is `loc_run` = snowman_loc_run_WITHOUT_present, so the gift is not visible
                        // while they run. Everything tried to keep it visible made the MOVEMENT worse:
                        //   - suppressing the streamed speed keeps the present, but the client then cannot
                        //     interpolate and snaps to each position update (the "teleporting" look);
                        //   - driving the glide at 30/sec did not fix that;
                        //   - MovementType cannot be changed after AddNpc, so CONTROLLER was never actually
                        //     tested on a live npc;
                        //   - amb_loop_01 (snowman_loc_run_with_present) never demonstrably played - every
                        //     "the present is there" sighting was the npc STOPPED, which is when the client
                        //     plays loc_stand, and loc_stand on this model already holds a present.
                        // Smooth walking was judged the more important half.
                        walker.MovingAnimationState = 2;
                        walker.SuppressExpectedSpeed = false;

                        // ★ THE GETAWAY MOVES THE SAME WAY THE APPROACH DOES - by MARCHING. Under PHYSICS
                        // that is the only thing that produces an actual walk: the client animates and
                        // interpolates it step by step, exactly as it does on the way in.
                        //
                        // It must NOT use the single-destination glide. That is the CONTROLLER technique
                        // (ProjectileNpc's: one position update to the end point, client interpolates the
                        // whole way) and on a physics actor it just relocates them - which is the "they
                        // teleport to spawn without moving" symptom.
                        if (InvaderMovementType == 2)
                        {
                            // ★ WALK STATE, NOT RUN. The client picks its locomotion clip from this byte,
                            // and this model has NO `loc_walk` - snowman_present.adr declares only
                            // loc_stand, loc_run, the two deaths, com_spawn and the two ambient slots. Ask
                            // for a walk and the client has to fall back, and the likeliest fallback is
                            // loc_stand = snowman_loc_stand_WITH_present.
                            //
                            // Run state (2) resolves to loc_run = run WITHOUT present, which is what has
                            // been stripping the gift all along. `/snowmen movestate <n>` to compare 0/1/2.
                            walker.MovingAnimationState = InvaderFleeMovingState;
                            walker.BaseAnimationId = InvaderFleeAnimationId;

                            // ★ Held THROUGH the movement: re-sent right after every position broadcast,
                            // which is the only ordering that beats the client's own locomotion resolve.
                            // `/snowmen testanim 2100` proved the clip itself renders fine - it was only
                            // ever being overwritten.
                            walker.StickyAnimationId = InvaderFleeAnimationId;
                            SendInvaderAnimation(walker, InvaderFleeAnimationId);

                            // ★ Backstop. The sticky re-send rides the MOVEMENT broadcast, which only fires
                            // once the npc has actually travelled far enough - so one that sets off slowly,
                            // or is briefly blocked, can go several ticks with nothing restating the clip
                            // and shows the client's own run instead. That is the "some of them walk back
                            // without a present" case. A few early re-sends cover the gap.
                            ReassertInvaderAnimation(walker, InvaderFleeAnimationId);

                            walker.MarchTarget = raid.FleeTo;
                            walker.AlwaysRoute = true;
                            walker.CombatSpeed = InvaderFleeSpeed;
                            walker.ReturnSpeed = InvaderFleeSpeed;
                        }
                        else
                        {
                            walker.MarchTarget = null;
                            walker.MovingAnimationState = InvaderFleeMovingState;

                            walker.BaseAnimationId = InvaderFleeAnimationId;
                            SendInvaderAnimation(walker, InvaderFleeAnimationId);

                            GlideInvaderHome(walker, raid.FleeTo);
                        }
                    }

                    break;

                case InvaderStage.Fleeing:
                    // Only the MARCHING case is polled here; the controller glide vanishes them itself.
                    if (InvaderMovementType == 2 &&
                        walker.MarchTarget is null &&
                        HorizontalDistance(walker.Position, raid.FleeTo) < InvaderArriveDistance)
                    {
                        VanishInvader(walker);
                    }

                    break;
            }
        }
    }

    // An invader left the field (escaped rather than knocked down) - hold its post so the wave refills it,
    // the same as a kill does.
    private void QueueInvaderRespawn(Vector4 post)
    {
        if (_snowmenPhase is SnowmenPhase.Invaders or SnowmenPhase.Boss)
            _snowmenRespawnQueue.Add((post, DateTime.UtcNow.AddSeconds(InvaderRespawnSeconds)));
    }

    // Walk an invader home at a high update rate so the client, which cannot interpolate while the speed
    // stream is suppressed, still sees smooth motion. Ends by vanishing it.
    private void GlideInvaderHome(CombatNpc walker, Vector4 destination)
    {
        // Land them on the smoothed clearing surface rather than wherever the path height happened to be -
        // a controller actor sits exactly where it is told, so this is what stops it sinking or floating.
        var target = new Vector4(destination.X, SmoothGroundHeightNear(destination), destination.Z, 1f);

        var dx = target.X - walker.Position.X;
        var dz = target.Z - walker.Position.Z;
        var distance = MathF.Sqrt(dx * dx + dz * dz);

        if (distance < 0.01f)
        {
            VanishInvader(walker);
            return;
        }

        // Facing is set ONCE, toward where they are going - the same normalised direction form
        // ProjectileNpc uses. Nothing needs to update it mid-glide because the path is a straight line.
        var rotation = new Quaternion(dx / distance, 0f, dz / distance, 0f);

        walker.UpdatePosition(target, rotation);

        foreach (var viewer in walker.VisiblePlayers.Values)
        {
            viewer.SendTunneled(new PlayerUpdatePacketExpectedSpeed
            {
                Guid = walker.Guid,
                ExpectedSpeed = InvaderFleeSpeed,
            });

            viewer.SendTunneled(new PlayerUpdatePacketUpdatePosition
            {
                Guid = walker.Guid,
                Position = target,
                Rotation = rotation,
                State = 1,   // moving - what ProjectileNpc sends
                Unknown = 0,
            });
        }

        // Vanish when the client will have finished gliding.
        var travelMs = (int)(distance / MathF.Max(InvaderFleeSpeed, 0.1f) * 1000f);

        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                await System.Threading.Tasks.Task.Delay(travelMs);
                VanishInvader(walker);
            }
            catch { }
        });
    }

    // Restate a held clip a few times over the first couple of seconds, independently of the movement
    // broadcast - see the note at the call site.
    private void ReassertInvaderAnimation(Npc invader, int animationId)
    {
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                foreach (var delay in InvaderReassertMs)
                {
                    await System.Threading.Tasks.Task.Delay(delay);

                    // Stop if it has moved on to something else (knocked down, vanished, new clip).
                    if (!invader.IsAlive || invader.BaseAnimationId != animationId)
                        return;

                    SendInvaderAnimation(invader, animationId);
                }
            }
            catch { }
        });
    }

    // Cumulative: ~0.2s, ~0.6s, ~1.2s, ~2s after the clip is first set.
    private static readonly int[] InvaderReassertMs = [200, 400, 600, 800];

    // Dev: play each id in a range on every live invader, one every few seconds, so a whole animation
    // family can be watched in one pass instead of typed out one at a time. The snowman's clip set is not
    // listed anywhere readable, so finding a usable grab is necessarily trial and error.
    public void SweepInvaderAnimations(int from, int to, int stepMs = 2500)
    {
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                for (var id = from; id <= to; id++)
                {
                    if (_snowmenInvaders.Count == 0)
                        return;

                    PlayInvaderAnimationNow(id);
                    _logger.LogInformation("Snowmen: animation sweep playing {id}.", id);

                    await System.Threading.Tasks.Task.Delay(stepMs);
                }
            }
            catch { }
        });
    }

    // Dev: play a clip on every live invader immediately, for finding one the snowman rig actually has.
    public int PlayInvaderAnimationNow(int animationId)
    {
        var played = 0;

        foreach (var invader in _snowmenInvaders)
        {
            if (!invader.IsAlive)
                continue;

            InvaderStealAnimationId = animationId;
            PlayInvaderSteal(invader);
            played++;
        }

        return played;
    }

    // The grab at the tree.
    //
    // ★ HELD, NOT FIRED ONCE. A single SetAnimation on a MOVING npc is pointless: these are physics movers
    // and their movement broadcasts reassert the locomotion clip, so a one-shot is overwritten within a
    // tick. (Bruce, the one npc animation in this codebase that demonstrably works, is Static = true - he
    // never moves, so nothing competes with his BaseAnimationId.)
    //
    // So the clip is re-sent on a short interval for the whole grab, and BaseAnimationId is set too so a
    // player who walks up mid-grab is handed the pose with the AddNpc rather than seeing a frozen snowman.
    private void PlayInvaderSteal(Npc invader)
    {
        // The rig-independent half - see InvaderStealFxId.
        if (InvaderStealFxId > 0)
        {
            var burst = new PlayerUpdatePacketPlayCompositeEffect
            {
                Guid = invader.Guid,
                CompositeEffectId = InvaderStealFxId,
                Position = invader.Position,
            };

            foreach (var viewer in invader.VisiblePlayers.Values)
                viewer.SendTunneled(burst);
        }

        _logger.LogInformation("Snowmen: invader {guid} grabbing at the tree (anim {anim}).",
            invader.Guid, InvaderStealAnimationId);

        // A bark as it takes the gift - spoken BY the snowman so it renders as its own line, and NOT
        // chat-logged (these are ambient, not conversation - same treatment the boss's taunts get).
        if (SnowmanInvaderGiftLines.Length > 0 && Random.Shared.Next(100) < SnowmanInvaderBarkPercent)
        {
            var line = SnowmanInvaderGiftLines[Random.Shared.Next(SnowmanInvaderGiftLines.Length)];
            // ★ Npc.SayStringId - the codebase's OWN bubble path, and the one that actually works. It
            // sets OwnerGuid (required) and sends NO colour; a hand-rolled ChatPacketFromStringId that
            // omitted OwnerGuid rendered nothing without a colour and printed to the announcements with
            // one. It also only sends to VisiblePlayers, which a bubble needs.
            invader.SayStringId(line);
        }

        // ★★ PlayType 1 IS A BASE ANIMATION, AND A BASE ANIMATION LOOPS UNTIL REPLACED. That is the whole
        // reason the grab "played twice": one send is enough to make it repeat for as long as it is left in
        // place. CombatNpc.PlaySwingAnimation documents the same thing and works around it exactly this way.
        //
        // So: send it once, then put the actor back to idle after one clip length. That is what fakes a
        // single play - the server log proves we only ever SENT it once per invader.
        // ★ The grab OWNS the clip while it plays: the sticky run is cleared (nothing must restate it over
        // the top) and BaseAnimationId records what is up, which is what the idle reset below checks before
        // firing. Without that the reset bailed out every time and the one-shot looped - the grab playing
        // twice.
        if (invader is CombatNpc grabber)
            grabber.StickyAnimationId = 0;

        invader.BaseAnimationId = InvaderStealAnimationId;
        SendInvaderAnimation(invader, InvaderStealAnimationId);

        var guid = invader.Guid;
        var animationIdAtGrab = InvaderStealAnimationId;
        var watchers = new List<Player>(invader.VisiblePlayers.Values);

        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                await System.Threading.Tasks.Task.Delay(InvaderGrabClipMs);

                // ★ Don't clobber a getaway that has already begun. This is a fire-and-forget timer, so if
                // the grab window is ever shortened (or the tick runs late) it can land AFTER the carry clip
                // has been set - which would drop the present. Only reset if the grab is still what's up.
                if (invader.BaseAnimationId != animationIdAtGrab)
                    return;

                var idle = new PlayerUpdatePacketSetAnimation
                {
                    Guid = guid,
                    AnimationId = InvaderIdleAnimationId,
                    PlayType = 1,
                };

                foreach (var watcher in watchers)
                    watcher.SendTunneled(idle);
            }
            catch { }
        });
    }

    // ★ HELD, NOT FIRED ONCE. These are physics movers, and their movement broadcasts reassert the
    // locomotion clip - a single SetAnimation is overwritten within a tick. (Bruce, the one npc animation in
    // this codebase that demonstrably works, is Static = true, so nothing competes with his pose.)
    //
    // BaseAnimationId is set as well so a player who walks up mid-clip is handed the pose with the AddNpc
    // instead of seeing a snowman standing idle.
    private static void SendInvaderAnimation(Npc invader, int animationId)
    {
        if (animationId <= 0)
            return;

        var frame = new PlayerUpdatePacketSetAnimation
        {
            Guid = invader.Guid,
            AnimationId = animationId,
            PlayType = 1,
        };

        foreach (var viewer in invader.VisiblePlayers.Values)
            viewer.SendTunneled(frame);
    }

    // ★ ONE PLAY OF THE GRAB CLIP - and this is the CLIP's length, NOT how long they linger at the tree.
    // A base animation loops until replaced, so this is the deadline for putting them back to idle; set it
    // longer than the clip and it plays again. It was derived from InvaderStealMs (2500 - 400 = 2100ms),
    // which is roughly twice the clip, which is exactly why the grab kept playing twice.
    // `/snowmen grabclip <ms>` tunes it against the real clip length.
    public static int InvaderGrabClipMs { get; set; } = 1_000;

    private const int InvaderIdleAnimationId = 1;   // loc_stand = snowman_loc_stand_with_present

    // ★ THE GRAB'S ACTUAL VISUAL, because the ANIMATION may not exist on this rig at all.
    //
    // They now walk to the tree and stop dead, but no clip renders - not 1099 com_swing (the id
    // CombatNpc.ExplicitAttackAnimByModel records for 1907, which was added when these snowmen were seen
    // hitting while FROZEN and was never confirmed to actually render afterwards), not the emote or soccer
    // families (wrong rig entirely - those are player clips). The likeliest answer is that
    // snowman_present.adr carries nothing beyond idle/walk/run.
    //
    // A composite effect does not depend on the rig, so this is what actually sells the theft: a burst at
    // the snowman while it stands at the tree. Tunable with `/snowmen stealfx <id>`; 0 disables it.
    public static int InvaderStealFxId { get; set; }              // 0 = none. The animation is the whole show.

    // It got away with a present. Same graceful exit the boss uses rather than blinking out.
    private void VanishInvader(Npc invader)
    {
        foreach (var viewer in invader.VisiblePlayers.Values)
            viewer.SendTunneled(new PlayerUpdatePacketRemovePlayerGracefully
            {
                Guid = invader.Guid,

                // ★ FALSE. Animate = true plays the actor's own removal clip, which for this model is
                // com_death_01 - so an invader that got clean away appeared to DIE at the edge of the
                // clearing. They escaped; they should simply fade out.
                Animate = false,
                Delay = 0,
                EffectDelay = 0,
                CompositeEffectId = InvaderVanishFxId,
                Duration = InvaderVanishFadeMs,
            });

        var post = _invaderRaids.TryGetValue(invader.Guid, out var raid) ? raid.FleeTo : invader.Position;

        _invaderRaids.Remove(invader.Guid);
        _snowmenInvaders.Remove(invader);

        // Escaping counts as a post falling empty, so the wave keeps its pressure up.
        QueueInvaderRespawn(post);

        TryRemoveNpc(invader.Guid);
        invader.Dispose();
    }

    private void RefillSnowmenPosts(DateTime now)
    {
        for (var i = _snowmenRespawnQueue.Count - 1; i >= 0; i--)
        {
            if (now < _snowmenRespawnQueue[i].RespawnAt)
                continue;

            var post = _snowmenRespawnQueue[i].Post;
            _snowmenRespawnQueue.RemoveAt(i);

            if (SpawnSnowmanEnemy(SnowmanInvaderModelId, SnowmanInvaderNameId, "Snowman Invader",
                    SnowmenInvaderLevel, post) is { } invader)
            {
                _snowmenInvaders.Add(invader);
                BeginInvaderRaid(invader);   // head for the tree immediately - no idle tick first
            }
        }
    }

    // Ensures the nameless announcer exists. Invisible and non-interactive - it is a voice, not a prop.
    private ulong EnsureAnnouncer()
    {
        if (_announcer is { } existing)
            return existing.Guid;

        if (!TryCreateNpc(out var announcer))
            return 0;

        announcer.IsEventSpawn = true;
        announcer.ModelId = 1056;      // invisible_cube_with_skeleton
        announcer.NameId = 0;          // ★ nameless - this is what removes the "Name:" prefix
        announcer.Name = null;
        announcer.HideNamePlate = true;
        announcer.Static = true;
        announcer.Visible = true;      // must be KNOWN to the client to be a valid speaker
        announcer.IsInteractable = false;
        announcer.InteractRange = 0;
        announcer.ShowHealthBar = false;
        announcer.EnemyStatus = false;

        announcer.UpdatePosition(GiftingTreeCenter, SpawnRotation);
        GetTileFromPosition(GiftingTreeCenter).Entities.TryAdd(announcer.Guid, announcer);
        PushNpcToNearbyPlayers(announcer);

        _announcer = announcer;
        return announcer.Guid;
    }

    // The client's own centre-screen message (op35/64 HudMessage). It carries a STRING ID, not text, so every
    // announcement has to be a real localized id - which is why the wording here is retail's own.
    private void AnnounceToZone(int stringId, ulong sourceGuid)
    {
        foreach (var player in Players)
        {
            // op35/64 HudMessage - the client's centre-screen message. Kept because it is the RIGHT packet
            // for this and costs nothing, but it has not been seen to render in the overworld (it is only
            // proven inside the combat tutorial), so it is not relied on alone.
            player.SendTunneled(new HudMessagePacket
            {
                Guid1 = sourceGuid,
                Guid2 = player.Guid,
                StringId = stringId,
            });

            // The dependable path: the same packet the boss's taunts use, which definitely renders out here.
            //
            // ★ It NEEDS a real speaker. The boss's lines work because they carry his guid; this one was sent
            // with SpeakerGuid 0 and the client simply dropped it - a speaker it can't resolve is a line it
            // won't draw. So the wave announcement is spoken by one of the invaders that just spawned (the
            // caller passes its guid), which is why the announcement is raised AFTER the spawn.
            // ★ IsEmote strips the "Name: " framing. A normal line is drawn as "<speaker>: <text>", and the
            // client emits that separator even when the speaker has no name - which is the bare ":" left over
            // once the announcer was made nameless. An EMOTE line is formatted as an action instead, so no
            // speaker/colon decoration is drawn at all.
            player.SendTunneled(new ChatPacketFromStringId
            {
                SpeakerGuid = sourceGuid,   // still needs a resolvable speaker or the line is dropped
                OwnerGuid = sourceGuid,
                StringId = stringId,
                IsEmote = true,
                IsChatLogged = false,
                HasColor = true,
                ColorId = 0, // white
            });
        }
    }
}
