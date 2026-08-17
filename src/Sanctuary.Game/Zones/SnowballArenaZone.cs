using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;

using Microsoft.Extensions.DependencyInjection;

using Sanctuary.Game.Combat;
using Sanctuary.Game.Entities;
using Sanctuary.Game.Interactions;
using Sanctuary.Game.Resources.Definitions.Zones;
using Sanctuary.Packet;

namespace Sanctuary.Game.Zones;

// SNOWBALL BATTLES ("Snowball Fighting") - retail's team-PvP snowball arena, rebuilt server-side.
//
// This is NOT the Snowhill overworld snowball event (that one is StartingZone.SnowmenInvaders + the
// year-round piles). It is retail's own separate minigame: two teams, Blue and Red, pelt each other, and
// the first team to enough hits wins. Everything named below came out of the shipped client data rather
// than being invented - see the string ids on each constant.
//
//   world  = sh_snowball_battle - a REAL world in the packs, and a fully dressed one.
//
// ★★ THE FIELD IS AUTHORED IN THE WORLD, AND THE Areas.xml "Bed" SPHERE IS NOT IT. That sphere (centre
// 103/21/393, radius 45, carrying ambient sound 16656 + music 5035) is the AUDIO area, and on other worlds
// the Bed has doubled as the arena - here it does not. Building on it spawned players outside the fence.
//
// The real layout, read out of the world's own .gcnk placements (867 of them):
//   * SIX `evnt_winter_holiday_snowfort_*.adr` snow forts, in two groups of three facing each other
//     across Z - `_02` x3 on the SOUTH side (z 382.9-387.7), `_01` x3 on the NORTH (z 405.1-411.0).
//     Two sets of three forts on opposite sides IS the two-team layout; they are the team bases.
//   * an `sg_ice_fence_*` ring enclosing the whole thing, x 81.5..130.2 by z 367.0..427.1 - so the field
//     is about 49 wide by 60 deep, and it runs along Z, NOT along X.
//   * every fort sits at y 21.81..22.03, which is where GroundY comes from - measured geometry, not a
//     guess off an area definition.
//
// ★ THE EARLIER READING OF THIS WORLD AS AN EMPTY FIELD WAS WRONG, and worth knowing why: a .gcnk holds
// its placements in an INNER zlib stream that starts after the "GCNK" magic + 12-byte header, so scanning
// the OUTER raw-DEFLATE result for model names finds nothing and looks like an empty world. Use
// GcnkParser (Pathfinding/GcnkParser.cs) - it already does this correctly - rather than eyeballing bytes.
//
// FIRST SLICE - deliberately the playable core, not the whole retail feature:
//   in : team assignment + a team spawn, the goals pane, Basic piles, friendly fire off, hit scoring,
//        first-to-N win with retail's own victory text, then everyone teleports home.
//   out: the five other snowball types and their per-team pile flags, Calvin Coldcastle and the Quick Play
//        / Invite Friends lobby, the 12 Days quest hook, and the FTE. All of those layer onto this loop.
public sealed class SnowballArenaZone : BaseZone
{
    private sealed class SnowballArenaDefinition : BaseZoneDefinition
    {
    }

    public enum SnowballTeam
    {
        Blue,
        Red,
    }

    // ── Identity ───────────────────────────────────────────────────────────────────────────────────────
    // Activity 71 "Snowball Fight" (ClientActivityDefinitions), name 9245 / description 9248 "Meet on the
    // frozen lake in Snowhill and throw some snowballs at your friends!".
    public const int EncounterId = 71;
    public const int EncounterInstanceId = 1;

    // The row this game occupies in the Matchmaking panel's queue list - see the queue table in
    // ListQueuesRequestPacketHandler, where 51 is Snowball Fighting (NameId 419545).
    public const int MatchmakingQueueId = 51;

    // What Calvin Coldcastle sends as SelectQueueForUserPacket's queue id.
    //
    // ★ 0, NOT 51, AND THAT IS DELIBERATE. Sending 51 works - the Matchmaking panel opens - but it opens
    // ALREADY ON the Snowball Fighting detail pane, skipping the step retail actually showed: the queue
    // LIST ("1 Waiting  Pirate's Plunder…"), where the player picks Snowball Fighting themselves and hits
    // Next. 0 is the "nothing pre-selected" value, which should land on the list.
    // ★ NOT LIVE-PROVEN - the packet's consumer is reached through a dispatch table rather than its vtable
    // (which holds only destructors), so what the client does with the id has not been read out of the
    // binary. If 0 opens nothing at all, this is the knob: `/snowball queue <id>` retunes and re-sends it
    // live, no rebuild.
    public static int MatchmakingOpenQueueId { get; set; } = 0;
    // The queue's own name and blurb, so the start panel and the minigame HUD say the same thing the
    // Matchmaking row did: 419545 "Snowball Fighting", 419546 "Pick up snowballs and throw them at the
    // other team to win!". (9245/9248 are the older "Snowball Fight" community-event pair.)
    public const int ArenaNameId = 419545;
    public const int ArenaDescriptionId = 419546;

    // ★ IconId is an image-SET id, not a raw image id - the two spaces overlap and neither errors, which
    // has already drawn the wrong picture twice in this project. The purpose-drawn panel graphic for this
    // game DOES exist (image 28360, icon_ui_combatstart_panelgraphic_snowballfight) but has NO image-set
    // wrapper in this build - ImageSetMappings.txt maps nothing to it, and only the foresttrollfort /
    // grexanscamp / highroad panels got sets - so it cannot be addressed through this field at all.
    // 5596 "event_snowball_fights" is the real snowball event icon set and is reachable.
    public const int ArenaIconId = 5596;

    // Client enum MINI_GAME_TYPE_COMBAT = 4 - the minigame status handler only shows the objective pane for
    // a combat-type minigame (the same gate every dungeon here goes through).
    // ★ 1, chosen by testing in-game - it is the only value tried that does NOT draw the knockout counter,
    // which retail's Snowball Fighting doesn't have.
    //
    // This was 4 (COMBAT) because 4 gates the Goals pane, but 4 also drags in the combat minigame HUD and
    // its knockout counter. Type 1 is what the client's own data uses for quest-style minigames - resolving
    // MiniGameData.txt's NAME_IDs through the locale file shows type 1 holding "Lost Little Sheep",
    // "Snowmen on the Loose!", "Foot Race: Beat Finn's Record" and ~150 others.
    //
    // For reference, the same method maps the rest: 3 = trading card game, 6 = Chess, 7 = Checkers,
    // 8 = mining/harvesting/forging, 9 = cooking/smelting, 10 = kart racing, 11 = demo derby, 12 = soccer,
    // 21 = fishing, 22 = the prize wheel, 1009 = spot-the-difference. Snowball Fighting itself has NO row
    // in that table - it is an encounter, not a listed minigame - so no value there is "the right" one and
    // this is a behavioural pick, not a recovered constant.
    //
    // `/snowball type <n>` changes it live (re-sends the match state) if it needs revisiting.
    public static int MiniGameType { get; set; } = 1;

    // EncounterDetailsCommon "Unknown3", the zone-context selector: the client has a value dedicated to
    // THIS minigame - 9 sets its snowball flag at BaseClient+0x782 (see EncounterDetailsResponsePacket).
    // What the flag actually gates is not yet known; it is sent because retail's own value for a snowball
    // arena is not a guess we have to make.
    // ★★ 0, NOT 9 - AND 9 WAS THE WHOLE UI BUG.
    //
    // The client derives three booleans directly from this field (op41/114 handler at 0x00aa3ca0):
    //     IsInHub           = (ZoneContext == 8)     -> byte [zone+0x781]
    //     IsInSnowballFight = (ZoneContext == 9)     -> byte [zone+0x782]
    //     (unnamed)         = (ZoneContext == 12)    -> byte [zone+0x783]
    // Setting 9 therefore TOLD the client it was in a snowball fight - and the client's own script picks
    // GameDock MINIGAME_STATE over NORMAL_STATE from a predicate set that includes IsInSnowballFight(),
    // which is what greyed the Atlas/Welcome buttons and re-populated the Goals pane on every UI rebuild
    // (opening the mounts menu was enough to trigger one).
    //
    // Worse, nothing ever recomputes those flags on the way out: they are only assigned while handling an
    // encounter-details packet, and no other zone sends one - so once set, the flag survived the trip home
    // and every teardown packet we could think of. Live reads kept showing [zone+0x782] = 1 in the
    // overworld no matter what the server sent, because the server was never the thing setting it.
    //
    // Every other zone in this codebase leaves ZoneContext at 0, which is why none of them have this bug.
    // 0 also makes it self-correcting: entering the arena now assigns 0 to all three flags.
    private const int SnowballZoneContext = 0;

    // ── Layout ────────────────────────────────────────────────────────────────────────────────────────
    // Middle of the pitch, i.e. halfway between the two team spawns below.
    private static readonly Vector4 ArenaCenter = new(105.8f, 22f, 399f, 1f);

    // Every snow fort in the world sits at y 21.81..22.03, and both measured spawns came back at 21.9-22.0.
    private const float GroundY = 22f;

    // ★ THE TWO TEAM SPAWNS, MEASURED IN GAME WITH !pos (2026-08-15) - real standing ground, so they are
    // used verbatim with no height fudge, the same rule Resources/SnowballPiles.json follows. These
    // supersede an earlier pair derived from the snow-fort centroids, which sat too far into the middle.
    //   Blue  X=103.04 Y=21.99 Z=377.15  heading 9 degrees
    //   Red   X=108.50 Y=21.90 Z=420.81  heading -172 degrees
    // The headings are measured too: each side starts looking down the pitch at the other.
    private static readonly Vector4 BlueSpawn = new(103.04f, 21.99f, 377.15f, 1f);
    private static readonly Vector4 RedSpawn = new(108.50f, 21.90f, 420.81f, 1f);
    private const float BlueHeading = 9f * MathF.PI / 180f;
    private const float RedHeading = -172f * MathF.PI / 180f;

    // ★ TWO PILES ON EACH SIDE, ONE PER SPECIAL - retail's own layout ("two located on each side of the
    // playing grounds", fanbyte). Each team gets a POWER pile and a FREEZING pile just in front of its own
    // camp, so a player picks which special to carry without crossing the pitch. Flanked left and right of
    // each camp's centre line, comfortably inside the fence (x 81.5..130.2, z 367..427).
    private const float PileFlankX = 12f;      // how far left/right of the camp centre
    private const float BluePileZ = 388f;      // just in front of the south forts (z 382.9-387.7)
    private const float RedPileZ = 404f;       // just in front of the north forts (z 405.1-411.0)

    // ★ EACH PILE WEARS ITS OWN SPECIAL'S BADGE, not the generic snowball one - NotificationImages 240 is
    // the bubble + ice cube (Freezing) and 241 the bubble + rock throw (Power), the same art as their
    // toolbar icons. 251/239 are the generic snowball-fight badge that Calvin and the Snowhill piles use,
    // which said nothing about which pile you were walking up to.

    // ── Match rules ────────────────────────────────────────────────────────────────────────────────────
    // Hits to win. Retail's own number is not recorded anywhere in the client data (the goal strings only
    // say "enough snowball hits"), so this is a tuning value - `/snowball arena target <n>` changes it.
    public static int HitsToWin { get; set; } = ScoreTarget;

    // Retail's own target, read off a screenshot of the Goals pane: "Blue Team's score - 0/80".
    public const int ScoreTarget = 80;

    // How long the victory text is left up before everyone is sent home.
    private const int VictoryLingerMs = 8000;

    // ── Real client text ───────────────────────────────────────────────────────────────────────────────
    // Goals. 423134 is the opener; the win goal is per-team because retail wrote one for each side.
    // ★ THE GOALS PANE SHOWS BOTH TEAMS' SCORES, to everyone - it is a scoreboard, not a personal goal
    // list, so these two rows are identical for every player regardless of side. Retail's own strings, and
    // its own "Bonus:" styling (the green rows in the screenshot).
    //
    // ★★ THESE ROWS ARE op47 UiObjectiveAdd, NOT the op45 objective path. op45 creates the objectives
    // inside the MiniGameState (without which nothing else is accepted), but what actually DRAWS a row in
    // the Goals panel is UiObjectiveAdd - and it has to arrive BEFORE PlayerEnter or ObjectiveListPopulate
    // hides it. Same lesson FrostfangArenaZone's entry sequence encodes.
    private const int BlueScoreGoalId = 420509;      // "Blue Team's score"
    private const int RedScoreGoalId = 420511;       // "Red Team's score"

    // UiObjectiveAdd's "Category Prefix" enum (0 none, 1 Primary, 2 Secondary, 3 Job, 4 Bonus, 5 Elite).
    private const int BonusCategoryPrefixId = 4;

    // ── The overhead team markers ──────────────────────────────────────────────────────────────────────
    // `PFX_symbol-flag_red_head_loop` / `PFX_symbol-flag_blue_head_loop` out of
    // ActorCompositeEffectDefinitions.xml: a matched red/blue pair, `_head` (overhead), `_loop` (persistent),
    // on ADJACENT ids - which is what a two-team marker looks like, and there is no other red/blue overhead
    // pair in the whole effect table. Each wraps a single PARTICLE effect, hence the coloured glow.
    //
    // ★ Not to be confused with `PFX_arrow-combat_*` (16387-16393), the other overhead-arrow family: those
    // exist only in RED, so they cannot mark two sides.
    private const int RedTeamMarkerFxId = 16257;
    private const int BlueTeamMarkerFxId = 16258;

    // Attached via an effect tag (op35/41) rather than played as a one-shot, so it rides the player and can
    // be pulled off again by tag on the way out - the same pattern the snowball piles' sparkle uses.
    private const int TeamMarkerTagId = 91020;

    // Team announcements, shown on entry so a player knows which side they are on.
    private const int JoinBlueMessageId = 420191;    // "Join the Blue Team and become a snowball fighting legend!"
    private const int JoinRedMessageId = 420194;     // "Join the Red Team and become a snowball fighting legend!"

    // Victory lines - retail wrote one per winning side, plus a neutral one.
    private const int BlueVictoryMessageId = 420543; // "The Blue Team was the first to score enough..."
    private const int RedVictoryMessageId = 420549;  // "The Red Team was the first to score enough..."

    // ── The referees, and their call at the end ────────────────────────────────────────────────────────
    // The match is called by a referee, in green chat, prefixed with their name. Both lines are real:
    //   420807 "Blue Team wins! Blue Team wins! Can you believe in miracles!"
    //   420808 "Red Team wins! Red Team wins! Can you believe in miracles!"
    // ★ They sit ABOVE the id range a plain 1-700k brute force covers, which is why the earlier sweeps
    // for "miracles" missed them - found by hashing the CID out of en_us_data.dat instead.
    private const int BlueRefereeWinsMessageId = 420807;
    private const int RedRefereeWinsMessageId = 420808;

    // ChatPacketFromStringId's colour enum: 0 white, 1 red, 2 yellow, 3 GREEN, 4 blue.
    private const int ChatColorGreen = 3;

    // The speakers. Named with the real role strings, because that name is what the client puts in front
    // of the line - a call with no speaker guid would just be a floating sentence.
    // (420183 "Jori Icehands" and 420189 "Saiya Hailstorm" are the same two referees out in Snowhill,
    // where they hand out team membership; in here the role name is what shows.)
    private const int BlueRefereeNameId = 420188;  // "Blue Team Referee"
    private const int RedRefereeNameId = 420190;   // "Red Team Referee"

    // Behind each camp, facing down the pitch - out of the way of the fight, in view of it.
    private static readonly Vector4 BlueRefereePosition = new(103f, GroundY, 373f, 1f);
    private static readonly Vector4 RedRefereePosition = new(108.5f, GroundY, 425f, 1f);

    private ulong _blueRefereeGuid;
    private ulong _redRefereeGuid;

    // ── The victory show ──────────────────────────────────────────────────────────────────────────────
    // ★ A LOOPING FOUNTAIN, ATTACHED TO AN ANCHOR AT EACH FORT - not a one-shot played at a world
    // position. Two reasons the first attempt showed nothing and wouldn't have looped anyway:
    //   * the EFX_ family (EFX_fireworks_blue_spray etc.) is environment-authored, not the PFX one-shots
    //     PlayCompositeEffect is used with everywhere else in this codebase;
    //   * PlayCompositeEffect fires once. A firework SHOW has to persist, and the way persistence works
    //     here is an effect TAG on an entity (the snowball piles' sparkle does exactly this).
    // So each of the winner's three forts gets an invisible anchor npc wearing the loop.
    // ★★ REPEATED ONE-SHOTS, NOT AN ATTACHED LOOP. Two attempts failed before this: EFX_ world sprays via
    // PlayCompositeEffect (EFX_ is environment-authored and showed nothing), then a PFX _loop attached to
    // an invisible anchor by effect tag. The attach path is real - it is how the pile sparkle works - but
    // it depends on the composite being authored to anchor on a prop with no usable socket (the pile uses
    // the `_world` variant of its sparkle for exactly this reason), and the firework loops have no `_world`
    // sibling.
    //
    // So the show is built the way every other effect in this file is proven to render: a PFX ONE-SHOT
    // through PlayCompositeEffect at a world position, re-fired on a timer. That reads as a continuous
    // display and uses only the path already known to work here (it is the same call the snowball splat
    // and the throw sound use).
    //
    // Tunable live: `/snowball winfx <id>` and `/snowball winfxrate <ms>`.
    public static int FireworkFxId { get; set; } = 5354;      // PFX_fireworks_multi_celebration-medium
    public static int FireworkIntervalMs { get; set; } = 1200;
    private const int FireworkShowMs = 30_000;
    private const int CelebrationFireworkFxId = 5349;         // PFX_fireworks_multi_celebration-large

    private int _fireworkShowRun;

    // Who won, so a player leaving by the door can be told whether they won or lost.
    private SnowballTeam? _winner;

    // What a winner spins for on the way out. Coins rather than an item: the arena has no job-specific
    // prize table of its own the way the combat dungeons do (they roll from the player's active-job set),
    // and inventing one would be making up rewards retail didn't record.
    private const int WinnerPrizeCoins = 250;

    // The six real snow forts, straight out of the world's .gcnk placements.
    private static readonly Vector4[] BlueFortPositions =
    [
        new(95.39f, 22.02f, 384.66f, 1f),
        new(104.03f, 21.99f, 387.72f, 1f),
        new(111.38f, 22.00f, 382.86f, 1f),
    ];

    private static readonly Vector4[] RedFortPositions =
    [
        new(98.96f, 21.82f, 411.01f, 1f),
        new(105.95f, 21.81f, 405.13f, 1f),
        new(114.87f, 22.03f, 407.79f, 1f),
    ];

    // ── The exit door (846 sg_exit_door_01) ───────────────────────────────────────────────────────────
    // Same prop and fields the combat encounters use - see FrostfangArenaZone, where every value was read
    // off a live AddNpc. It appears in the middle of the pitch when the match is decided.
    private const int DoorModelId = 846;
    private const int DoorNameId = 4826;
    private const float DoorScale = 1.2f;
    private const int DoorInteractRange = 125;
    private const int DoorCursorId = 17;

    private Npc? _exitDoor;

    private readonly IZoneManager _zoneManager;
    private readonly Microsoft.EntityFrameworkCore.IDbContextFactory<Sanctuary.Database.DatabaseContext> _dbContextFactory;

    // Team roster and score. Concurrent because throws are processed off the zone tick.
    private readonly ConcurrentDictionary<ulong, SnowballTeam> _teams = new();
    private readonly ConcurrentDictionary<SnowballTeam, int> _scores = new();

    // Per-player landed snowballs. The team score is what wins the match, but the end-of-match card is a
    // PERSONAL scoreboard - it tells one player what THEY did - so the team total can't fill it in.
    private readonly ConcurrentDictionary<ulong, int> _hits = new();

    private readonly object _matchLock = new();
    private bool _matchOver;

    // Bumped by every ResetMatch, and captured by anything that runs on a delay after the match is decided
    // (the result card). A new match must never be interrupted by the last one's leftovers.
    private int _matchRun;

    public SnowballArenaZone(IServiceProvider serviceProvider)
        : base(CreateDefinition(), serviceProvider)
    {
        _zoneManager = serviceProvider.GetRequiredService<IZoneManager>();
        _dbContextFactory = serviceProvider.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<Sanctuary.Database.DatabaseContext>>();
    }

    private static BaseZoneDefinition CreateDefinition() => new SnowballArenaDefinition
    {
        Id = EncounterId, // traceability; the runtime zone Id is assigned by the manager
        Name = "sh_snowball_battle",
        TileSize = 64,
        // Wide bounds - the world's own tiles run out past ±512 and the playable field is a long way from
        // the origin, so there is nothing to gain from fitting the grid tightly.
        StartLongitude = -32,
        EndLongitude = 32,
        StartLatitude = -32,
        EndLatitude = 32,
        Sky = null, // the world's own winter ambience
        // Only a fallback - every real entry goes to a team spawn via PrepareEntry.
        SpawnPosition = ArenaCenter,
        SpawnRotation = Quaternion.Identity,
    };

    public int BlueScore => _scores.TryGetValue(SnowballTeam.Blue, out var s) ? s : 0;
    public int RedScore => _scores.TryGetValue(SnowballTeam.Red, out var s) ? s : 0;

    #region Zone lifecycle

    public override void OnStart()
    {
        base.OnStart();
        SpawnPiles();
        SpawnReferees();
    }

    // One referee per team, standing behind their camp. They exist to be the SPEAKER of the victory call -
    // the client prefixes a chat line with the speaker's name, so without a real NPC behind the guid the
    // "Blue Team Referee:" part simply wouldn't appear.
    private void SpawnReferees()
    {
        _blueRefereeGuid = SpawnReferee(BlueRefereeNameId, BlueRefereePosition, BlueHeading);
        _redRefereeGuid = SpawnReferee(RedRefereeNameId, RedRefereePosition, RedHeading);
    }

    private ulong SpawnReferee(int nameId, Vector4 position, float heading)
    {
        if (!TryCreateNpc(out var referee))
            return 0;

        // 837 human_m_snowhill - the Snowhill townsperson, the same stand-in Calvin uses. No referee
        // model ships in this client.
        referee.ModelId = 837;
        referee.NameId = nameId;
        referee.Static = true;
        referee.Visible = true;
        referee.Scale = _resourceManager.Models.TryGetValue(837, out var model) && model.Scale != 0f
            ? model.Scale
            : 1f;

        referee.UpdatePosition(position, new Quaternion(MathF.Sin(heading), 0f, MathF.Cos(heading), 0f));
        GetTileFromPosition(position).Entities.TryAdd(referee.Guid, referee);

        return referee.Guid;
    }

    // The way out, in the middle of the pitch, once the match is decided. Clicking it sends the player
    // home - the same contract the combat encounters' door has (no auto-kick; you leave when you're ready).
    private void SpawnExitDoor()
    {
        if (_exitDoor is not null)
            return; // already up - a second win can't happen, but a re-entered arena shouldn't stack them

        if (!TryCreateNpc(out var door))
            return;

        door.ModelId = DoorModelId;
        door.NameId = DoorNameId;
        door.Static = true;
        door.Visible = true;
        door.Scale = DoorScale;
        door.IsInteractable = true;
        door.InteractRange = DoorInteractRange;
        door.CursorId = DoorCursorId;
        door.ShowHealthBar = false;
        door.MaxHealth = 0;
        door.InteractAction = SendHome;

        var position = new Vector4(ArenaCenter.X, GroundY, ArenaCenter.Z, 1f);

        door.UpdatePosition(position, Quaternion.Identity);
        GetTileFromPosition(position).Entities.TryAdd(door.Guid, door);

        // Mid-match spawn: the load-time visibility sweep has long since run, so hand it to everyone who
        // is standing here or it exists server-side and renders for nobody. (Same pairing the combat
        // arenas' door does - the npc has to know its viewers as well as the other way round.)
        foreach (var viewer in Players)
        {
            viewer.OnAddVisibleNpcs(door);
            door.OnAddVisiblePlayers(viewer);
        }

        _exitDoor = door;
    }

    // Take the door away when the arena resets, so the next match doesn't start with an exit already up.
    private void RemoveExitDoor()
    {
        if (_exitDoor is not { } door)
            return;

        foreach (var viewer in door.VisiblePlayers.Values)
            viewer.SendTunneled(new PlayerUpdatePacketRemovePlayer { Guid = door.Guid });

        TryRemoveNpc(door.Guid);
        door.Dispose();
        _exitDoor = null;
    }

    public override void OnClientIsReady(Player player)
    {
        player.RecalculateStats(refill: true);

        player.SendTunneled(new PacketZoneDoneSendingInitialData());
        player.SendTunneled(new ClientUpdatePacketDoneSendingPreloadCharacters());

        JobWeaponAbilities.SendToolbarWithFxPreload(player, _resourceManager);
    }

    // The load screen has dropped. Packets sent during it can be discarded (the arena lesson), so the
    // goals pane and the team announcement both wait until here - and then a further beat, because the
    // client's zone-in tail resets encounter/UI state right after FinishedLoading and eats anything
    // delivered in the same instant (the same 1.5s the Frostfang entry sequence needs).
    public override void OnClientFinishedLoading(Player player)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1500);

                if (player.Zone == this)
                    SendMatchState(player);

                // The tutorial popup + the green arrow pointing at the snowball button. Sent after the
                // match state so the toolbar the arrow points at already exists.
                await Task.Delay(FteDelayMs);

                if (player.Zone == this)
                    SendSnowballFte(player);
            }
            catch { }
        });
    }

    // ★ THE FIRST-TIME-EVENT TUTORIAL ("Hit the [1] key on your keyboard to throw snowballs...") with the
    // big green arrow pointing down at the snowball on the toolbar. BOTH halves are pure CLIENT content -
    // nothing here supplies the text or draws the arrow:
    //   * `Resources/FirstTimeEvents.txt` row `75^FtesSnowball^` is the event.
    //   * The client's own Lua (ScriptsBase, firsttimeevent.lua) owns the dialog and maps the event to its
    //     arrow, `FteSnowballArrow` - both names are in the shipped scripts.
    //   * FTEs fire from one of three trigger types the scripts name: ClientScriptTrigger,
    //     ServerScriptTrigger, KillTrigger. ServerScriptTrigger is the one a server drives.
    //
    // The clean way in is `ExecuteScriptPacket` (op47/7), which is already proven to run client Lua here -
    // WallOfDataUIEventPacketHandler uses it to run `WelcomeHandler.close()`. That avoids implementing the
    // op107 BaseFirstTimeEvent family, whose wire format is not reversed (the packet names are known from
    // the opcode table, the layouts are not).
    //
    // ★ THE RECEIVER/SEPARATOR IS THE UNCERTAIN PART. The scripts show the call is
    // `TriggerFirstTimeEvent("<EventName>")` - it appears in the Job browser paired with "GamedockJobs" -
    // but whether it hangs off `FirstTimeEvent` as a method (`:`) or a plain field (`.`), or is a global,
    // can't be read off the constant pool. All the plausible forms are sent; the wrong ones are no-ops in
    // the client's script VM. `/snowball fte <lua>` overrides this to try anything else.
    public static string FteEventName { get; set; } = "FtesSnowball";

    // ★ The numeric id from Client/Resources/FirstTimeEvents.txt: row `75^FtesSnowball^`. The NAME is what
    // the client Lua uses internally, but the wire side of this system is id-based - see below.
    public const int FteEventId = 75;
    private const int FteDelayMs = 2_000;

    public static void SendSnowballFte(Player player) => SendSnowballFte(player, control: false);

    // Fire an arbitrary FirstTimeEvents.txt id. Exists so a KNOWN-GOOD event can be used as a control:
    // if e.g. 2 (GamedockJobs) displays and 75 (FtesSnowball) does not, the trigger mechanism is fine and
    // the problem is specific to this event's content/conditions; if neither displays, the display path
    // itself is the problem and the event id is irrelevant.
    public static void TriggerFte(Player player, int eventId, string name = "")
    {
        player.SendTunneled(new FirstTimeEventScriptPacket
        {
            Script = name,
            EventId = eventId,
            Clear = false,
        });
    }

    public static int FteSpacingMs { get; set; } = 600;

    public static void SendSnowballFte(Player player, bool control)
    {
        // ★★ THE SERVER IS THE ONLY THING THAT CAN FIRE THIS EVENT. `FtesSnowball` appears EXACTLY ONCE in
        // the whole client script set - in the FTE name registry - and in no trigger code anywhere. Compare
        // FtesCombatSplashScreen, which the client does fire itself (observed live in our own arena, since
        // we declare MiniGameType 4 = COMBAT). So waiting for the client to show this tutorial is hopeless;
        // it is a ServerScriptTrigger event by construction.
        //
        // ★ And the Script field is just the EVENT NAME. There is no data file mapping events to trigger
        // strings (FirstTimeEvents.txt is only ID^NAME), and the handler's tokenising on '.', ',' and ':'
        // simply yields one token when the string contains none - so a bare name is the natural input. The
        // earlier `Table.Function:Arg` spellings were over-built.
        // ★ Live-confirmed field meanings: the int is the EVENT ID and the bool is CLEAR. Sending
        // (id 0, clear true) made the client print "First time event 0 cleared" - which is also the proof
        // that this packet reaches the FTE system at all. So firing the tutorial is the same packet with
        // the real id and clear left OFF.
        _ = Task.Run(async () =>
        {
            try
            {
                player.SendTunneled(new FirstTimeEventScriptPacket
                {
                    Script = FteEventName,
                    EventId = FteEventId,
                    Clear = false,
                });
            }
            catch { }
        });
    }

    // Prune players who left (logged out, or were teleported away by something else) so a stale roster
    // can't keep scoring for a team that isn't here.
    protected override void UpdateEverySecondZone()
    {
        base.UpdateEverySecondZone();

        var present = Players.Select(p => p.Guid).ToHashSet();

        foreach (var guid in _teams.Keys)
        {
            if (present.Contains(guid))
                continue;

            _teams.TryRemove(guid, out _);
        }

        // The zone instance is shared and long lived, so a finished match has to clear itself once the last
        // player is out - otherwise _matchOver stays set and every later throw scores nothing, leaving the
        // arena permanently unplayable until someone remembers to run `/snowball arena reset`.
        if (present.Count == 0)
            ResetMatch();
    }

    #endregion

    #region Teams

    // Put a player on a side and hand back where they should spawn. Called by the entry command BEFORE the
    // teleport, because the spawn position is a parameter of it.
    //
    // Balancing is "whichever side is smaller", so a solo tester lands on Blue and the next arrival opposes
    // them - which is the only way this is testable without four people.
    public (Vector4 Position, Quaternion Rotation) PrepareEntry(Player player)
    {
        // First one in starts a fresh match rather than inheriting the last one's score - the tick-based
        // reset below covers the same case, this just doesn't wait for it.
        if (!Players.Any())
            ResetMatch();

        var team = _teams.GetOrAdd(player.Guid, _ => SmallerTeam());

        return (SpawnFor(team), FacingFor(team));
    }

    private SnowballTeam SmallerTeam()
    {
        var blue = _teams.Values.Count(t => t == SnowballTeam.Blue);
        var red = _teams.Values.Count(t => t == SnowballTeam.Red);

        return red < blue ? SnowballTeam.Red : SnowballTeam.Blue;
    }

    public bool TryGetTeam(ulong guid, out SnowballTeam team) => _teams.TryGetValue(guid, out team);

    // Friendly fire is OFF: a snowball never lands on your own side. Two players with no team assigned
    // (which shouldn't happen in here, but can if someone arrives by some other route) are treated as
    // opponents so a throw still does something rather than silently whiffing.
    // ★ THE COOLDOWN SWEEP NEEDS A REAL ENEMY. AbilityPacketLaunchAndLand is what actually starts the
    // spinning radial on an ability button, and its processor silently no-ops unless Guid2/Guid3 resolve
    // to a genuine enemy target - the caster's OWN guid (our fallback when a throw hit nothing, and the
    // only thing the self-targeted guard ever had) is rejected, so no sweep was ever drawn. In here there
    // is always an opponent, so hand the packet the nearest one.
    public ulong NearestOpponentGuid(Player player)
    {
        if (!_teams.TryGetValue(player.Guid, out var team))
            return 0;

        ulong nearest = 0;
        var nearestDistance = float.MaxValue;

        foreach (var candidate in Players)
        {
            if (candidate.Guid == player.Guid || candidate.IsDead)
                continue;
            if (_teams.TryGetValue(candidate.Guid, out var candidateTeam) && candidateTeam == team)
                continue;

            var dx = candidate.Position.X - player.Position.X;
            var dz = candidate.Position.Z - player.Position.Z;
            var distance = dx * dx + dz * dz;

            if (distance >= nearestDistance)
                continue;

            nearestDistance = distance;
            nearest = candidate.Guid;
        }

        return nearest;
    }

    public bool SameTeam(Player a, Player b) =>
        _teams.TryGetValue(a.Guid, out var teamA) &&
        _teams.TryGetValue(b.Guid, out var teamB) &&
        teamA == teamB;

    private static Vector4 SpawnFor(SnowballTeam team) =>
        team == SnowballTeam.Blue ? BlueSpawn : RedSpawn;

    // The measured facing for each camp - both look down the pitch at the other side. The client's
    // "rotation" is the facing DIRECTION packed as (sin h, 0, cos h, 0), not a quaternion; !pos reads it
    // back with Atan2(rot.X, rot.Z), which is where the two heading constants came from.
    private static Quaternion FacingFor(SnowballTeam team)
    {
        var heading = team == SnowballTeam.Blue ? BlueHeading : RedHeading;
        return new Quaternion(MathF.Sin(heading), 0f, MathF.Cos(heading), 0f);
    }

    #endregion

    #region Match state

    // The minigame state + goals pane for one player. Objectives have to be declared INLINE in the launch
    // packet - the client's op45 dispatch will only activate an objective that already exists in the
    // MiniGameState - so this is what has to be sent before any ObjectiveActivate can land.
    public void ResendMatchState(Player player) => SendMatchState(player);

    private void SendMatchState(Player player)
    {
        if (!_teams.TryGetValue(player.Guid, out var team))
            team = _teams.GetOrAdd(player.Guid, _ => SmallerTeam());

        // ★★ THE FULL ENTRY SEQUENCE, NOT JUST A LAUNCH. A lone LAUNCH creates the MiniGameState, which is
        // enough for the goals pane - but the client never calls showMinigame, so the map isn't treated as
        // a minigame and the minigame HUD (with its leave control) never appears. That was the missing
        // piece. This mirrors the real-server order decoded from the 2014-04-01 capture and already proven
        // by FrostfangArenaZone: LAUNCH -> PlayerEnter(0) -> LAUNCH again -> PlayerEnter(playerGuid). The
        // first Populate fires before the status handler exists, the PlayerEnter brings the HUD up, and the
        // second LAUNCH re-fires Populate against the now-live handler.
        EncounterDetailsResponsePacket MakeLaunch() => new()
        {
            NameId = ArenaNameId,
            DescriptionId = ArenaDescriptionId,
            IconId = ArenaIconId,
            Unknown = EncounterId,
            Unknown2 = EncounterInstanceId,
            MiniGameType = MiniGameType,
            ZoneContext = SnowballZoneContext,
            Launch = true, // create the MiniGameState
            ActivityId = EncounterId,
            Objectives =
            [
                new EncounterObjective { ObjectiveId = BlueScoreGoalId, NameId = BlueScoreGoalId, Status = 0, Total = 0 },
                new EncounterObjective { ObjectiveId = RedScoreGoalId, NameId = RedScoreGoalId, Status = 0, Total = 0 },
            ],
        };

        EncounterPacketPlayerEnter MakeEnter(ulong guid) => new()
        {
            EncounterId = EncounterId,
            InstanceId = EncounterInstanceId,
            PlayerGuid = guid,
        };

        // The two scoreboard rows. Total must be > 1 or the client's status-text builder never renders a
        // live "N/M" at all, which is the whole point of them.
        UiObjectiveAddPacket ScoreRow(int goalId) => new()
        {
            ObjectiveId = goalId,
            NameId = goalId,
            Total = HitsToWin,
            IsBonus = true,
            CategoryPrefixId = BonusCategoryPrefixId,
        };

        player.SendTunneled(MakeLaunch());
        player.SendTunneled(ScoreRow(BlueScoreGoalId));   // before PlayerEnter, or the rows are hidden
        player.SendTunneled(ScoreRow(RedScoreGoalId));
        player.SendTunneled(MakeEnter(0));            // showMinigame - brings the minigame HUD up

        // ★ THE SECOND LAUNCH IS THE SOURCE OF THE SECOND STATE, so it is OFF by default now. Each LAUNCH
        // creates a MiniGameState, and a state can only be removed once STARTED - while a game start only
        // ever marks the CURRENT state. Two states therefore means one is very easy to strand, and a
        // stranded state re-renders the Goals pane and re-locks the GameDock on any UI rebuild (opening the
        // mounts menu was enough to trigger it).
        //
        // The double LAUNCH was originally copied from the encounter entry sequence to make the client
        // re-run Populate against a live status handler. If the HUD or the goal rows fail without it,
        // `/snowball doublelaunch on` puts it back.
        if (SendSecondLaunch)
            player.SendTunneled(MakeLaunch());

        player.SendTunneled(MakeEnter(player.Guid));

        // ★★ MARK THE GAME AS STARTED, OR IT CAN NEVER BE TORN DOWN. The client's MiniGameState carries a
        // "started" byte at +0x62, set only when a game-start arrives (FUN_009b95cd, which also stamps the
        // start time into +0x68/+0x6c). The state-removal handler (FUN_009bf190) opens with
        // `cmp byte [state+0x62], 0 / je bail` - so a state that was never started is UNREMOVABLE, and every
        // MiniGameStateRemovePacket we sent was silently doing nothing.
        //
        // That single missing packet is what left the Goals pane alive (its rows are the state's inline
        // objectives, so they came back on any UI refresh - e.g. switching jobs) and the GameDock stuck in
        // MINIGAME_STATE with the Atlas/Welcome buttons dead. Every other minigame path here already sends
        // it (EncounterParticipantRequestEntranceHandler, DailyWheelGame); this zone never did.
        player.SendTunneled(new MiniGameGameStartPacket(0, -1, -1));

        // In-zone encounter state, the same 6 the combat arenas settle on once the player is inside.
        player.SendTunneled(new EncounterStatePacket
        {
            EncounterId = EncounterId,
            InstanceId = EncounterInstanceId,
            State = 6,
        });

        // Which side you're on. Retail's own join lines, used here as the "you are Blue" announcement.
        player.SendTunneled(new HudMessagePacket
        {
            Guid1 = player.Guid,
            Guid2 = player.Guid,
            StringId = team == SnowballTeam.Blue ? JoinBlueMessageId : JoinRedMessageId,
        });

        // Whatever the match is already at, so someone arriving mid-game sees the real score.
        player.SendTunneled(new UiObjectiveUpdateCountPacket { ObjectiveId = BlueScoreGoalId, Count = BlueScore });
        player.SendTunneled(new UiObjectiveUpdateCountPacket { ObjectiveId = RedScoreGoalId, Count = RedScore });

        ShowTeamMarkers(player);
    }

    // The floating red/blue flag over each head, so you can tell at a glance who to throw at.
    //
    // Two directions, both needed: this player's marker has to reach everyone already here, and everyone
    // else's markers have to reach this player - an effect tag is state on an actor, and a client that
    // never received the attach simply doesn't draw it.
    private void ShowTeamMarkers(Player arriving)
    {
        foreach (var other in Players)
        {
            if (other.Guid != arriving.Guid)
                arriving.SendTunneled(MakeTeamMarker(other));

            other.SendTunneled(MakeTeamMarker(arriving));
        }
    }

    private PlayerUpdatePacketAddEffectTagCompositeEffect MakeTeamMarker(Player player)
    {
        var isBlue = !_teams.TryGetValue(player.Guid, out var team) || team == SnowballTeam.Blue;

        return new PlayerUpdatePacketAddEffectTagCompositeEffect
        {
            Guid = player.Guid,
            TagId = TeamMarkerTagId,
            CompositeEffectId = isBlue ? BlueTeamMarkerFxId : RedTeamMarkerFxId,
            SourceGuid = player.Guid,
        };
    }

    // Pull the marker back off on the way out - a looping attached effect is held until something removes
    // it, and it would otherwise follow the player back into the overworld.
    private void HideTeamMarker(Player player)
    {
        var remove = new PlayerUpdatePacketRemoveEffectTagCompositeEffect
        {
            Guid = player.Guid,
            TagId = TeamMarkerTagId,
        };

        player.SendTunneled(remove);

        foreach (var other in Players)
            other.SendTunneled(remove);
    }

    private static int ScoreGoalFor(SnowballTeam team) =>
        team == SnowballTeam.Blue ? BlueScoreGoalId : RedScoreGoalId;

    private int ScoreFor(SnowballTeam team) => _scores.TryGetValue(team, out var score) ? score : 0;

    // A snowball landed on another player. Returns true when it counted for a point - the thrower and the
    // victim being on opposite sides, and the match still running.
    //
    // Called from SnowballTool.KnockDown, i.e. only on a hit that actually connected.
    public bool OnSnowballHit(Player thrower, Player victim)
    {
        if (!_teams.TryGetValue(thrower.Guid, out var team))
            return false;
        if (_teams.TryGetValue(victim.Guid, out var victimTeam) && victimTeam == team)
            return false; // friendly fire scores nothing (and FindTarget shouldn't have picked them anyway)

        int score;

        lock (_matchLock)
        {
            if (_matchOver)
                return false;

            score = _scores.AddOrUpdate(team, 1, (_, previous) => previous + 1);
            _hits.AddOrUpdate(thrower.Guid, 1, (_, previous) => previous + 1);

            if (score >= HitsToWin)
            {
                _matchOver = true;
                EndMatch(team);
                return true;
            }
        }

        // Push the new score to EVERYONE - both rows are a shared scoreboard, so the other team needs to
        // see it climb just as much as the scoring one does.
        var update = new UiObjectiveUpdateCountPacket { ObjectiveId = ScoreGoalFor(team), Count = score };

        foreach (var player in Players)
            player.SendTunneled(update);

        return true;
    }

    // One side reached the target: green-check their goal, tell everyone who won in retail's own words,
    // and send the whole arena home once the message has had time to be read.
    private void EndMatch(SnowballTeam winner)
    {
        var victoryMessageId = winner == SnowballTeam.Blue ? BlueVictoryMessageId : RedVictoryMessageId;
        var refereeGuid = winner == SnowballTeam.Blue ? _blueRefereeGuid : _redRefereeGuid;

        // The referee's call, in green, attributed to them so it reads "Blue Team Referee: Blue Team
        // wins! ...". ColorId 3 is the client's green; the speaker guid is what puts the name in front of
        // it, which is the whole reason the two referees exist (see SpawnReferees).
        var call = new ChatPacketFromStringId
        {
            SpeakerGuid = refereeGuid,
            StringId = winner == SnowballTeam.Blue ? BlueRefereeWinsMessageId : RedRefereeWinsMessageId,
            IsChatLogged = true,
            HasColor = true,
            ColorId = ChatColorGreen,
        };

        // ★ THE CHAT LINE IS THE WHOLE ANNOUNCEMENT. It renders where retail put it - bottom of the
        // screen, above the ability bar - in green, prefixed with the speaker's name. A HudMessagePacket
        // was also going out here and that was the mistake: op35/64 is the client's CENTRE-screen message
        // box (see StartingZone.SnowmenInvaders), so the call appeared in the middle of the screen.
        foreach (var player in Players)
            player.SendTunneled(call);

        _winner = winner;

        // Snapshot who was on which side. The card is raised after each player has already been dropped
        // from the roster on their way out, so it can't read _teams by then.
        lock (_matchLock)
        {
            _finalTeams.Clear();
            foreach (var pair in _teams)
                _finalTeams[pair.Key] = pair.Value;
        }

        PlayVictoryFireworks(winner);
        SpawnExitDoor();
        ShowResultCards(winner);

        // ★ NO AUTO-KICK. The exit door IS the way out now, the same as every combat encounter here -
        // players leave when they've finished looking at the fireworks. The Leave button on the minigame
        // HUD still works too, and an emptied arena resets itself on the zone tick.
    }

    #endregion

    #region The end-of-match result card

    // ★★ THE WINNERS' SCORE SCREEN - the client's own minigame end card (scoreScreen.gfx), raised the way
    // every combat encounter here raises its win card, and NOT the way the two earlier attempts did.
    //
    // What lands on screen: the big YOU WIN / TRY AGAIN letters (the SWF's own winLetter / loseLetter,
    // picked off the "Won" byte that op39/18 GameOver stamps onto the MiniGameState) over the score rows
    // carried by op39/47.
    //
    // ★ GAMEOVER FIRST, SCORE PACKET SECOND, and the order is not cosmetic. GameOver only sets a flag; the
    // thing that actually DRAWS the persistent card is the score data arriving. Send them the other way
    // round and the card renders as a loss for everyone. (Same ordering CombatEncounterZone and
    // BaseZone.SendFailEndScreen already encode.)
    //
    // ★ AND IT DOES NOT DEPEND ON MiniGameType. That was the open risk here - every proven card in this
    // codebase belongs to a type-4 COMBAT encounter, while this arena deliberately runs at type 1 (see
    // MiniGameType). Settled by reading the client's own ScoreScreen.lua out of ScriptsBase.bin: SetStates()
    // adds STATE_SCORE whenever score data exists and excludes only checkers/chess - the minigame type is
    // never consulted. (The REWARDS pane is a different story: it is gated on m_ValidRewardGameTypes, which
    // is why no loot wheel is armed here - see GrantWinnerPrize.)
    //
    // ★ WHY THE EARLIER TWO ATTEMPTS FAILED, and what is different now. Both tried to raise the card around
    // the trip home - once by holding the player in the instance to read it, once by re-creating minigame
    // context out in Snowhill - and got a card that dismissed itself or a Goals pane that would not go away.
    // This one is raised WHERE AND WHEN THE MATCH ENDS and gates nothing at all: the exit door is still the
    // only way out, and going through it tears the card down with the rest of the minigame UI. Nothing may
    // ever WAIT on the card, because the client does not report it being dismissed - CommandPacket sub42
    // ClosedMinigameEndScreen never arrives for cards like this one (see CombatEncounterZone's own note).

    // How long the referee's call and the first volley of fireworks get before the card covers the arena.
    // The card obscures the scene, so raising it instantly would throw away the show that was just built.
    // `/snowball arena cardat <ms>` retunes it live.
    public static int ResultCardDelayMs { get; set; } = 3_000;

    // The two client score-row names this card uses.
    //
    // ★ THE ROW LABEL IS A CLIENT-SIDE CONSTANT, so only names the client already knows are usable. The
    // row's leading string is a KEY rather than the text itself, and it does NOT go through the T4 locale -
    // checked this session, "scoreEnemiesDefeated" and its three siblings hash to no CID in en_us_data.dat
    // under any namespace tried. So an invented name like "scoreSnowballsLanded" has nothing behind it and
    // would render blank or raw. That leaves the four names the real 2014-04-01 server was recorded sending,
    // two of which happen to say something true about a snowball fight:
    //
    //   scorePlayerKnockouts - every landed snowball knocks its target flat (SnowballTool.KnockDown), so
    //                          this is literally "opponents you put down", not a borrowed label.
    //   scoreTotalScore      - the total line at the foot of the card.
    //
    // The team result needs no row of its own: it IS the card's headline (YOU WIN / TRY AGAIN) and the
    // referee already called it in chat - which is fortunate, because there is no client row name for it.
    private const string KnockdownsRowName = "scorePlayerKnockouts";
    private const string TotalScoreRowName = "scoreTotalScore";

    // The row field this codebase calls "Order" is really the SWF's "Score Type" column - a FORMAT selector,
    // not a sort key (see MiniGameGameEndScorePacket, where the live column mapping is now written down).
    // 3 renders "N of M"; 4 is the total line.
    private const int CountOfMaxScoreType = 3;
    private const int TotalScoreType = 4;

    // Raise the card on everyone still standing in the arena, a beat after the match is called.
    private void ShowResultCards(SnowballTeam winner)
    {
        var run = _matchRun;
        var blueScore = ScoreFor(SnowballTeam.Blue);
        var redScore = ScoreFor(SnowballTeam.Red);

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(ResultCardDelayMs);

                // A player who used the exit door inside that beat is already home - and a card raised in
                // Snowhill is exactly the failure the previous attempt died of, so re-check both the player
                // and the match before sending anything.
                if (run != _matchRun)
                    return;

                foreach (var player in Players)
                {
                    if (player.Zone != this)
                        continue;

                    ShowResultCard(player, winner, blueScore, redScore);
                }
            }
            catch { }
        });
    }

    // One player's card. Public so `/snowball arena card` can re-raise it while tuning the rows.
    public void ShowResultCard(Player player, SnowballTeam winner, int blueScore, int redScore)
    {
        // Which side they were on. Read the SNAPSHOT first: a player who left and came back has already been
        // dropped from the live roster, and their card still has to say what they did in the match.
        SnowballTeam team;
        lock (_matchLock)
        {
            if (!_finalTeams.TryGetValue(player.Guid, out team) && !_teams.TryGetValue(player.Guid, out team))
                return;
        }

        // ★ STAND THEM UP FIRST. A snowball that landed on the final beat leaves its victim stunned, and the
        // client wipes the end card the instant it draws a knocked-down player - the same trap
        // BaseZone.SendFailEndScreen documents. Clearing the effect server-side sends the state change; the
        // explicit None broadcast is the belt-and-braces half of the pairing that is already proven there.
        Sanctuary.Game.Combat.StatusEffects.ClearAll(player);
        player.SendTunneledToVisible(new PlayerUpdatePacketUpdateCharacterState
        {
            Guid = player.Guid,
            Status = CharacterStatus.None,
        }, sendToSelf: true);

        var won = team == winner;
        var hits = _hits.TryGetValue(player.Guid, out var landed) ? landed : 0;
        var teamScore = team == SnowballTeam.Blue ? blueScore : redScore;
        var points = hits * PointsPerHit;

        // The flag the card's headline is drawn from. Must precede the score packet - see the header above.
        player.SendTunneled(new MiniGameGameOverPacket(won));

        var score = new MiniGameGameEndScorePacket();

        // "N of M" - the opponents this player personally put down, out of their team's final score. It is
        // their share of the result, which is the only per-player number a snowball fight produces.
        score.Rows.Add(new MiniGameScoreRow
        {
            Name = KnockdownsRowName,
            Order = CountOfMaxScoreType,
            Value = hits,
            Max = teamScore,
            Points = points,
        });

        score.Rows.Add(new MiniGameScoreRow
        {
            Name = TotalScoreRowName,
            Order = TotalScoreType,
            Points = points,
        });

        player.SendTunneled(score);
    }

    // The current score, for the dev re-raise.
    public void ShowResultCard(Player player)
    {
        if (_winner is not { } winner)
            return;

        ShowResultCard(player, winner, BlueScore, RedScore);
    }

    #endregion

    #region Match state (continued)

    // The roster is dropped as each player leaves, so the side a player was on is snapshotted at EndMatch -
    // both the result card and the winner's payout still need it after they have left the roster.
    private readonly Dictionary<ulong, SnowballTeam> _finalTeams = [];

    // What one snowball hit is worth on the end-of-match score card. Invented - retail's per-hit value
    // isn't recorded anywhere - and picked so a full 80-hit win reads as a round 8000.
    private const int PointsPerHit = 100;

    // The winners' forts go off. Each of the three snow forts on that side gets its team-coloured
    // firework, and one big multi-colour celebration goes up over the middle of the pitch.
    //
    // The fort positions are the REAL ones read out of the world's own .gcnk placements - the same six
    // that decided where the team camps are.
    private void PlayVictoryFireworks(SnowballTeam winner)
    {
        var forts = winner == SnowballTeam.Blue ? BlueFortPositions : RedFortPositions;
        var run = ++_fireworkShowRun;

        _ = Task.Run(async () =>
        {
            try
            {
                var until = DateTime.UtcNow.AddMilliseconds(FireworkShowMs);
                var atCentre = true;

                while (DateTime.UtcNow < until && run == _fireworkShowRun)
                {
                    foreach (var player in Players)
                    {
                        foreach (var fort in forts)
                        {
                            player.SendTunneled(new PlayerUpdatePacketPlayCompositeEffect
                            {
                                Guid = 0, // world-positioned one-shot
                                CompositeEffectId = FireworkFxId,
                                Position = new Vector4(fort.X, fort.Y + 2f, fort.Z, 1f),
                            });
                        }

                        // A bigger burst over the middle every other beat, so the show has some shape
                        // rather than three identical fountains on a metronome.
                        if (atCentre)
                        {
                            player.SendTunneled(new PlayerUpdatePacketPlayCompositeEffect
                            {
                                Guid = 0,
                                CompositeEffectId = CelebrationFireworkFxId,
                                Position = new Vector4(ArenaCenter.X, GroundY + 4f, ArenaCenter.Z, 1f),
                            });
                        }
                    }

                    atCentre = !atCentre;

                    await Task.Delay(FireworkIntervalMs);
                }
            }
            catch { }
        });
    }

    // Stop the show (the loop checks the run number each beat).
    private void RemoveFireworks() => _fireworkShowRun++;

    // Back to wherever the player entered from. Also drops them off the roster, so the next match starts
    // from an empty arena rather than inheriting the last one's teams.
    //
    // ★★ THE MiniGameState TEARDOWN IS NOT OPTIONAL, AND LEAVING IT OUT IS WHAT STRANDED PLAYERS IN HERE.
    // SendMatchState creates one (EncounterDetails with Launch=true) and it is the master gate for the whole
    // minigame UI - while it exists the client stays in minigame context, which survives changing zones AND
    // reconnecting, so the player came back still locked into the arena with no way out. It used to be sent
    // only on victory; every exit path now goes through here and every exit path removes it. Same lesson the
    // encounter offer panel taught (see project_encounter_entry_npcs: a half-torn-down lobby locks input).
    public void SendHome(Player player)
    {
        HideTeamMarker(player);          // before the roster drop, so the broadcast still reaches everyone
        SnowballGuard.Clear(player);     // a live shield bubble would otherwise follow them to Snowhill
        SnowballSpecials.Clear(player);  // specials only exist inside a match

        // Read the side BEFORE the roster drop - it decides which result card they get.
        _teams.TryGetValue(player.Guid, out var playerTeam);
        _teams.TryRemove(player.Guid, out _);

        if (player.Zone != this)
        {
            SendUiTeardown(player); // stale call - just make sure nothing is left on their screen
            return;
        }

        var home = _zoneManager.StartingZone;
        var returnPosition = player.EncounterReturnPosition ?? home.SpawnPosition;

        // ★ THE RESULT CARD IS NOT RAISED HERE, AND THAT IS THE POINT. It goes up back at EndMatch, in the
        // arena, where the match was actually decided (see ShowResultCards) - the two shapes that failed
        // before were both built around this moment, either holding the player in the instance to read the
        // card or re-creating minigame context out in Snowhill. This path stays what it has always been: a
        // plain teardown-and-go, the one exit that has never stranded anyone, and the teardown below takes
        // the card down along with the rest of the minigame UI.
        //
        // The winner's prize is still granted directly rather than through a loot wheel - see below.
        if (_winner is { } decided && playerTeam == decided)
            GrantWinnerPrize(player);

        // One exit path for everyone now - winner, loser or someone bailing out mid-game.
        player.EncounterReturnPosition = null;
        SendUiTeardown(player);
        player.TeleportToZone(home, returnPosition, home.SpawnRotation, sky: null, geometryId: 0);
        SendUiTeardownAfterArrival(player);
    }

    // The winner's payout. Straight to the character row + the standard grant banner. No chat line: the
    // banner IS the feedback.
    //
    // ★ STILL NO LOOT WHEEL, even though the end card is back - the two are not the same thing. The wheel
    // lives on the card's REWARDS pane, and that pane alone IS type-gated: ScoreScreen.lua's
    // ValidateForGameType checks the minigame type against m_ValidRewardGameTypes before adding
    // STATE_REWARDS, where the score pane (STATE_SCORE) has no such check. Every encounter whose wheel is
    // known to work here runs at type 4; this arena runs at type 1 (see MiniGameType), so a wheel armed here
    // could silently never appear - and the wheel is what pays out, so the prize would go with it. A direct
    // grant cannot fail that way. If the reward pane is ever confirmed for type 1, this is the place to
    // switch back to MiniGameLootWheelSetItemToLandOnPacket + PendingWheelCoins.
    private void GrantWinnerPrize(Player player)
    {
        player.PendingWheelPrize = null;
        player.PendingWheelCoins = 0;

        // ★ WRITE THE CHARACTER ROW. Bumping Player.Coins alone only changes the in-memory copy and is lost
        // at logout - there is no save-on-logout path for it. Every other coin grant in the codebase
        // (GrantKillCoins, the quest rewards) goes through the db like this.
        using var dbContext = _dbContextFactory.CreateDbContext();
        var dbCharacter = dbContext.Characters
            .SingleOrDefault(x => x.Id == Sanctuary.Core.Helpers.GuidHelper.GetPlayerId(player.Guid));

        if (dbCharacter is null)
            return;

        dbCharacter.Coins += WinnerPrizeCoins;
        dbContext.SaveChanges();
        player.Coins = dbCharacter.Coins;

        player.SendTunneled(new ClientUpdatePacketCoinCount { Coins = player.Coins });
        player.SendTunneled(new RewardBundlePacket { RewardBundle = { Coins = WinnerPrizeCoins, Trailing = 957 } });
    }

    // The wheel-stopped signal no longer gates anything: the player is already home by the time the wheel
    // spins. Kept because BaseMiniGamePacketHandler routes it here, and the prize grant itself still runs
    // in that handler.
    public void NotifyRewardWheelStopped(Player player)
    {
    }

    // ★ THE MiniGameState AND THE GOALS WINDOW ARE SEPARATE TEARDOWNS. Removing the state (op39/19) does
    // NOT empty the Goals pane - its rows came in over op47 and need op47/sub5, or the two scoreboard rows
    // follow the player back into Snowhill and sit there. Same pair CombatEncounterZone sends.
    private static void SendUiTeardown(Player player)
    {
        // ★ ONE REMOVE. The state is already STARTED (SendMatchState sends MiniGameGameStartPacket on
        // entry), and removal is gated on exactly that - `cmp byte [state+0x62],0 / je bail` in
        // FUN_009bf190 - so nothing more is needed.
        //
        // This used to send start/remove PAIRS several times over. That was scaffolding from before the
        // real bug (ZoneContext = 9) was found, and it had a cost: starting a game purely to remove it made
        // the client flash its minigame end screen for a frame after the teleport.
        player.SendTunneled(new MiniGameStateRemovePacket());

        // Fully release combat mode - the MiniGameStateRemovePacket header calls for this pairing.
        player.SendTunneled(PacketEncounterDataCommon.CreateDefault());
        player.SendTunneled(new EncounterOverworldCombatPacket { Unknown3 = false });
        player.SendTunneled(new EncounterPacketIsFighting { InWorldCombat = false });

        // The Goals pane arrived over op47 and is not removed by the minigame teardown.
        player.SendTunneled(new UiObjectiveClearPacket());

        player.SendTunneled(new EncounterStatePacket
        {
            EncounterId = EncounterId,
            InstanceId = EncounterInstanceId,
            State = 0,
        });
    }

    // ★ NO POST-ARRIVAL RE-SEND. There used to be one (four, at one point) because the UI kept coming back
    // - but the cause of that was ZoneContext = 9 telling the client it was in a snowball fight, not a
    // teardown that failed to stick. With that fixed, the single pre-teleport teardown is enough.
    //
    // The re-send also had a visible cost: it fired ~300ms after the teleport, which is exactly when the
    // minigame end UI was flashing up in the overworld. Tearing down a minigame the client has already left
    // is not free.
    private static void SendUiTeardownAfterArrival(Player player)
    {
    }


    // Whether entry sends a SECOND LAUNCH (and so creates a second MiniGameState). Off by default - see
    // SendMatchState. `/snowball doublelaunch on|off` flips it.
    public static bool SendSecondLaunch { get; set; }


    // Dev: hand the win to a player's team right now, so the whole end-of-match sequence (referee call,
    // fireworks off their forts, exit door, result cards) can be tested without playing out 80 hits.
    // Returns the winning team, or null when the caller isn't on one or the match is already decided.
    public SnowballTeam? ForceWin(Player player)
    {
        if (!_teams.TryGetValue(player.Guid, out var team))
            return null;

        lock (_matchLock)
        {
            if (_matchOver)
                return null;

            _scores[team] = HitsToWin;

            // Credit the caller with the hits the command just handed their team, so the result card has
            // real numbers to draw. Without this a force-win shows "0 of 80" and a 0 total, which is the one
            // thing that can't be told apart from the rows failing to render at all - and this command
            // exists precisely to check that they do. Only tops up: a tester who really threw keeps theirs.
            _hits[player.Guid] = Math.Max(_hits.TryGetValue(player.Guid, out var thrown) ? thrown : 0, HitsToWin);

            _matchOver = true;
        }

        // Show the final score before the win lands, so the pane reads 80/80 rather than jumping straight
        // from whatever it was to the victory text.
        var update = new UiObjectiveUpdateCountPacket { ObjectiveId = ScoreGoalFor(team), Count = HitsToWin };

        foreach (var viewer in Players)
            viewer.SendTunneled(update);

        EndMatch(team);

        return team;
    }

    // Wipe the score and roster so the arena can be played again. The zone instance is shared and long
    // lived, so without this the second match would start already won.
    public void ResetMatch()
    {
        lock (_matchLock)
        {
            _matchOver = false;
            _scores.Clear();
            _hits.Clear();

            // Invalidates any result card still waiting on its delay - a card from the last match must not
            // land on someone who has already started the next one.
            _matchRun++;
        }

        RemoveExitDoor();
        RemoveFireworks();
        _winner = null;

        lock (_matchLock)
            _finalTeams.Clear();
    }

    #endregion

    #region Piles

    // A ring of Basic snowball piles between the two camps. Only the Basic type exists in this slice; the
    // Power / Rapid-Fire / Freezing / AOE / Storm piles retail also had are the next thing to layer on,
    // and they are per-team flagged, which this deliberately is not.
    private void SpawnPiles()
    {
        foreach (var team in new[] { SnowballTeam.Blue, SnowballTeam.Red })
        {
            var z = team == SnowballTeam.Blue ? BluePileZ : RedPileZ;

            CreatePile(team, SnowballSpecials.SpecialKind.Power,
                new Vector4(ArenaCenter.X - PileFlankX, GroundY, z, 1f));

            CreatePile(team, SnowballSpecials.SpecialKind.Freezing,
                new Vector4(ArenaCenter.X + PileFlankX, GroundY, z, 1f));
        }
    }

    // Same prop recipe as the Snowhill piles (model, scale, cursor, sparkle) so the two can't drift, with
    // three differences: the pile is NAMED for the special it hands out, it wears the minigame badge, and
    // clicking it grants that special rather than a generic armful.
    //
    // ★ A pile belongs to ONE TEAM. Each side has its own pair, so there is no reason to touch the other
    // team's - and letting a player restock from the enemy camp mid-fight would defeat the placement. If
    // that turns out to be wrong for retail, deleting the team check below is the whole change.
    private void CreatePile(SnowballTeam owner, SnowballSpecials.SpecialKind kind, Vector4 position)
    {
        if (!TryCreateNpc(out var pile))
            return;

        var facing = owner == SnowballTeam.Blue ? BlueHeading : RedHeading;
        var rotation = new Quaternion(MathF.Sin(facing), 0f, MathF.Cos(facing), 0f);

        pile.ModelId = SnowballTool.PileModelId;
        pile.NameId = kind == SnowballSpecials.SpecialKind.Power
            ? SnowballSpecials.PowerPileNameId
            : SnowballSpecials.FreezingPileNameId;
        pile.NotificationImageSetId = kind == SnowballSpecials.SpecialKind.Power
            ? SnowballSpecials.PowerBadgeId
            : SnowballSpecials.FreezingBadgeId;
        pile.Static = true;
        pile.Scale = _resourceManager.Models.TryGetValue(SnowballTool.PileModelId, out var model) && model.Scale != 0f
            ? model.Scale
            : 1f;
        pile.Visible = true;
        pile.CursorId = 17; // hand cursor so it's clickable

        pile.InteractAction = player =>
        {
            if (_teams.TryGetValue(player.Guid, out var team) && team != owner)
                return; // the other team's pile

            SnowballSpecials.Grant(player, kind, _resourceManager);
        };

        pile.UpdatePosition(position, rotation);
        GetTileFromPosition(position).Entities.TryAdd(pile.Guid, pile);

        pile.AttachedEffectId = SnowballTool.PileSparkleFxId;
        pile.AttachedEffectTagId = SnowballTool.PileSparkleTagId;
    }

    #endregion
}
