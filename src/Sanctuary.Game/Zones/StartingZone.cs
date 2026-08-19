using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Sanctuary.Core.Extensions;
using Sanctuary.Core.Helpers;
using Sanctuary.Core.IO;
using Sanctuary.Game.Combat;
using Sanctuary.Game.Entities;
using Sanctuary.Game.Interactions;
using Sanctuary.Game.Pathfinding;
using Sanctuary.Game.Resources.Definitions;
using Sanctuary.Game.Resources.Definitions.Zones;
using Sanctuary.Packet;
using Sanctuary.Packet.Common;
using Sanctuary.Packet.Common.Chat;

namespace Sanctuary.Game.Zones;

public sealed partial class StartingZone : BaseZone
{
    private readonly IZoneManager _zoneManager;
    private readonly IResourceManager _resourceManager;
    private readonly StartingZoneDefinition _zoneDefinition;
    private readonly Sanctuary.Game.Quests.IQuestManager _questManager;
    private readonly Sanctuary.Game.Gathering.IGatheringManager _gatheringManager;
    private readonly Sanctuary.Game.Party.IPartyManager _partyManager;
    private readonly Microsoft.EntityFrameworkCore.IDbContextFactory<Sanctuary.Database.DatabaseContext> _dbContextFactory;

    // Real routing for BOTH "Take Me There" (see ClientPathBasePacketHandler.BuildPath) and the overworld
    // enemy AI (see CombatNpc.MoveTowards) - they read the zone's shared NavGraph/NavObstacles, so a
    // player's auto-walk route and a chasing enemy respect exactly the same geometry.
    //
    // No client-facing navmesh exists anywhere in the extracted assets, so this is seeded from real
    // walkable ground: every curated Npcs.json spawn position (the same data TrySpawnNpc places the
    // overworld roster from) becomes a waypoint node, proximity-linked to its nearest neighbors, plus
    // wall-hug corner nodes (see WaypointGraphBuilder.AddWallHugPoints). Built once at zone construction.
    private const float WaypointMaxEdgeDistance = 30f;
    private const int WaypointMaxNeighborsPerNode = 6;
    // Rejects edges between points on a different floor/elevation despite being close in X/Z (a likely
    // "outside vs. upstairs/inside a building" pair) - see WaypointGraph.BuildFromPoints.
    private const float WaypointMaxYDelta = 10f;

    // The world whose .gcnk/.gzne geometry backs this zone - see ObstacleMapLoader.
    private const string GeometryWorld = "FabledRealms";

    public StartingZone(StartingZoneDefinition zoneDefinition, IServiceProvider serviceProvider)
        : base(zoneDefinition, serviceProvider)
    {
        _zoneDefinition = zoneDefinition;

        _zoneManager = serviceProvider.GetRequiredService<IZoneManager>();
        _resourceManager = serviceProvider.GetRequiredService<IResourceManager>();
        _questManager = serviceProvider.GetRequiredService<Sanctuary.Game.Quests.IQuestManager>();
        _gatheringManager = serviceProvider.GetRequiredService<Sanctuary.Game.Gathering.IGatheringManager>();
        _partyManager = serviceProvider.GetRequiredService<Sanctuary.Game.Party.IPartyManager>();
        _dbContextFactory = serviceProvider.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<Sanctuary.Database.DatabaseContext>>();

        // Props (.gcnk) AND walls (.gzne). The wall half is what this zone was missing for a long time:
        // it loaded only the placement files, so the overworld obstacle map had zero coverage of the real
        // terrain/cave boundary that the dungeon zones were already routing around from the same on-disk
        // data. Take Me There would happily draw a route straight through a cliff.
        var obstacleMap = ObstacleMapLoader.TryLoad(GeometryWorld, _logger, out var wallStrips);

        var waypointPoints = _resourceManager.Npcs.Values
            .Where(d => d.SpawnPosition[0] != 0f || d.SpawnPosition[2] != 0f) // drop origin/placeholder entries
            .Select(d => d.Position)
            .ToList();
        var npcSeedCount = waypointPoints.Count;

        // Nodes hugging each wall segment, so a route can round a wall's edge instead of the graph
        // fragmenting against the geometry we just taught it about. Keep each wall point's own real Y:
        // outdoor terrain isn't a single flat floor the way an arena is.
        //
        // MEASURED 2026-08-05: this adds ~581 nodes but does NOT measurably change overworld routing
        // success (identical hit rate with and without). FabledRealms.gzne only carries ~417 wall
        // segments for the whole world, so there's very little wall to hug out here - it's kept because
        // it's correct and costs ~2ms, not because it fixed anything measurable. The real overworld
        // limitation is seed COVERAGE, not linking: nodes only exist where NPCs happen to stand, so A*
        // connects only ~52% of pairs even at enemy-chase range (~40u) and ~30% within 200u; the rest
        // fall back to a straight line. Fixing that needs walkable-ground sampling like the dungeons do
        // (WaypointGraphBuilder.SampleWalkableGrid), which the current O(n^2) graph build can't absorb at
        // overworld scale - a separate piece of work, not something more wall nodes can paper over.
        if (obstacleMap is not null && wallStrips.Count > 0)
            WaypointGraphBuilder.AddWallHugPoints(waypointPoints, wallStrips, obstacleMap, flattenY: null);

        NavObstacles = obstacleMap;
        NavGraph = WaypointGraph.BuildFromPoints(waypointPoints, WaypointMaxEdgeDistance, WaypointMaxNeighborsPerNode, WaypointMaxYDelta, obstacleMap);

        if (obstacleMap is null)
            _logger.LogInformation("Built overworld waypoint graph: {n} nodes (no geometry data - assets directory not found).", NavGraph.NodeCount);
        else
            _logger.LogInformation("Built overworld waypoint graph: {n} nodes ({seed} NPC seeds + {hug} wall-hug), {obstacles} props, {walls} wall segments.",
                NavGraph.NodeCount, npcSeedCount, waypointPoints.Count - npcSeedCount, obstacleMap.ObstacleCount, obstacleMap.WallSegmentCount);

        // The Npcs.json roster itself is spawned by the zone's Lua script (Scripts/Zone/FabledRealms.lua,
        // generated 1:1 from Npcs.json) via TrySpawnNpc below — see OnStart, called by ZoneManager right
        // after construction finishes. Collect-goal pickups aren't part of that roster (they come from
        // Quests.json), so they still spawn directly here.
    }

    // Debug aid for CommandRouter's /waypoints - nodes near a position, with their edges, so bad
    // connections can be reported back by exact id.
    public List<(int Id, Vector4 Position, IReadOnlyList<int> Neighbors)> GetNearbyWaypoints(Vector4 position, float radius)
    {
        return NavGraph?.GetNodesNear(position, radius) ?? [];
    }

    public override void OnStart()
    {
        // MUST run before the two calls below: this reserves the deterministic guid range
        // (NpcGuidBase + id, used by Quests.json/NpcVendors.json lookups) and bumps the zone's shared
        // auto-guid counter past it, so the auto-assigned guids TrySpawnDungeonEntrance/SpawnEncounterEntryNpcs
        // hand out can't collide with it. (This used to be guaranteed by SpawnNpcs() running first,
        // synchronously, in the constructor — same guarantee, now via the zone script instead.)
        base.OnStart();

        // The atlas dungeon entrances (notif=3 POIs — click -> start panel -> GO!) are placed by the zone
        // script now, via TrySpawnDungeonEntrance, alongside everything else it puts in the world.

        // Place a wandering "Battle Starter" creature for each small combat encounter, among its own kind.
        SpawnEncounterEntryNpcs();

        // Calvin Coldcastle, the Snowball Fight match-maker - permanent, unlike Bruce, because he is both a
        // quest target and the minigame's entrance.
        SpawnCalvinColdcastle();

        // Trina Turtledove, the Snow Days Cheer Specialist - permanent for the same reason: she is the
        // event's hub and the giver of the 12 Days of Holidays.
        SpawnTrinaTurtledove();

        // Bruce is NOT spawned here - he only exists while performing, and the zone tick brings him in for
        // each show (see UpdateBrucePerformance).
    }

    // NPCs come from the community-contributed Npcs.json (fixed scale/rotation, static-marked). The guid
    // is derived from each entry's id so that Quests.json giver/target guids and NpcVendors.json - both
    // keyed by guid = NpcGuidBase + id - keep resolving after swapping the source from NpcSpawns.txt.
    private const ulong NpcGuidBase = 100000000000UL;

    // Guid of the single Tormented Spirit that acts as the dungeon entrance (click -> offer).
    // Every other graveyard spirit spawns as a hostile world enemy instead. 0 until the first is spawned.
    private ulong _spiritEntranceGuid;

    // The one Tormented Spirit entrance's guid (0 = none). The interact handler opens the encounter
    // offer ONLY for this spirit; the rest are fightable world enemies.
    public ulong SpiritEntranceGuid => _spiritEntranceGuid;

    // NPCs that have their OWN authored lines, taken from the dialogue ids Quests.json already pairs with
    // them (GiverDialogueId / TargetDialogueId / per-goal DialogueId). These are the real retail words that
    // character speaks. Only lines that survive as a bubble are listed: the client renders <BR> but NOT
    // <font>, so markup-bearing and paragraph-length quest text is deliberately left out — it would show
    // raw tags over the NPC's head. Everyone else falls back to AmbientGreetingIds.
    private static readonly Dictionary<ulong, int[]> NpcOwnLineIds = new()
    {
        [100000001557] = [78867],         // Chloe: "And that's how the Flying Dragons do things! You're a natural, kid."
        [100000002049] = [94388],         // Ricky Danger: "That lamp post? Came out of nowhere, I swear!"
        [100000002335] = [72906, 73239],  // Nomi: homework / worried about Hasti
        [100000002889] = [104100],        // Raina Rush: "Aren't my fans great!..."
        [100000003016] = [140679],        // Hasti: "Ah jeez, tell Nomi I'll be home in a bit..."
        [100000033082] = [104152],        // Gerold: "Nice job! Thanks to you, the Growlers won't be..."
    };

    // Real retail greeting lines (Global.Text ids, recovered from the client locale) that friendly NPCs
    // bubble when a player walks up. Sent with IsChatLogged=false, so they never touch the chat log.
    private static readonly int[] AmbientGreetingIds =
    [
        8026,  // "Hello, traveler!"
        39666, // "Hello and welcome!"
        8130,  // "Welcome to Free Realms!"
        8179,  // "Safe travels"
        8128,  // "Good luck!"
        38360, // "Come back soon!"
        39599, // "Glad you visited. Have a good day!"
        20628, // "Thank you for helping us. Come back anytime."
        8123,  // "How are you?"
    ];

    // Lua-facing spawn API (see BaseZone.TrySpawnNpc / Scripts/Zone/FabledRealms.lua, generated 1:1 from
    // Npcs.json). Reimplements every spawn rule the old Npcs.json foreach loop had — vendor wiring, quest
    // npc routing, world-enemy conversion, the single Tormented Spirits entrance, ambient dialogue —
    // parameterized by the position the script supplies instead of reading NpcDefinition.Position
    // directly, so placement now lives in the .lua file while every side effect below is unchanged.
    public override bool TrySpawnNpc(int npcId, ulong? npcGuid, float x, float y, float z, float heading)
    {
        var definition = _resourceManager.Npcs.Values.FirstOrDefault(d => d.Id == npcId);
        if (definition is null)
        {
            _logger.LogWarning("TrySpawnNpc: no Npcs.json definition found for NpcId {NpcId}.", npcId);
            return false;
        }

        var guid = npcGuid ?? NpcGuidBase + (ulong)definition.Id;
        var position = new Vector4(x, y, z, 1f);
        var rotation = new Quaternion(MathF.Sin(heading), 0f, MathF.Cos(heading), 0f);

        // WORLD COMBAT: curated enemy creatures (model matches the dungeon enemy set) spawn as hostile
        // CombatNpcs — they aggro on approach, chase, auto-attack the player, track HP, die, and respawn.
        // Excluded when the same model is doubling as a vendor, quest giver/target, or a quest kill-target,
        // which keep their existing interactive/quest paths (kill-targets get MakeQuestHostile below).
        if (IsWorldEnemyDefinition(definition))
        {
            SpawnWorldEnemy(definition, position, rotation);
            return true;
        }

        // INSTANCE (Tormented Spirits!): exactly ONE wandering spirit is the dungeon entrance (click ->
        // offer popup); every OTHER graveyard spirit spawns as a hostile world enemy you can fight.
        if (definition.NameId == TormentedSpiritsArenaZone.EntryNpcNameId)
        {
            if (_spiritEntranceGuid != 0)
            {
                SpawnWorldEnemy(definition, position, rotation);
                return true;
            }

            _spiritEntranceGuid = guid; // the first one becomes the single entrance (configured below)
        }

        if (!TryCreateNpc(guid, out var npc))
            return false;

        npc.ModelId = definition.ModelId;
        npc.NameId = definition.NameId;
        npc.TextureAlias = definition.TextureAlias;
        npc.Name = definition.Name;
        npc.Static = definition.Static;
        // Scale from the model definition (Models.txt), matching the client; 0 -> default 1.
        npc.Scale = _resourceManager.Models.TryGetValue(definition.ModelId, out var model) && model.Scale != 0f
            ? model.Scale
            : 1f;
        npc.Visible = true;

        if (_resourceManager.NpcVendors.TryGetValue(guid, out var vendorDef))
        {
            npc.VendorItems = vendorDef.Items;
            npc.VendorCosts = vendorDef.ItemCosts;
            npc.VendorBundles = vendorDef.Bundles;
            npc.ResourceManager = _resourceManager;
            npc.CursorId = 17;
            npc.NameplateImageId = vendorDef.NameplateImageId;
            npc.ImageSetId = vendorDef.ImageSetId;
            npc.NotificationImageSetId = vendorDef.NotificationImageSetId;
            if (vendorDef.ActiveProfile != 0)
                npc.ActiveProfile = vendorDef.ActiveProfile;
            if (vendorDef.SubTextNameId != 0)
                npc.SubTextNameId = vendorDef.SubTextNameId;
            var capturedNpc = npc;
            Action<Player> openShop = (interactingPlayer) =>
            {
                var itemListPacket = new CoinStoreItemListPacket();
                foreach (var itemDefId in capturedNpc.VendorItems)
                {
                    if (_resourceManager.CoinStoreItems.TryGetValue(itemDefId, out var meta))
                        itemListPacket.StaticItems[itemDefId] = meta;
                    else if (_resourceManager.ClientItemDefinitions.TryGetValue(itemDefId, out var def))
                        itemListPacket.StaticItems[itemDefId] = new ItemDefinitionMetaData { Id = itemDefId, CategoryId = def.CategoryId };
                }
                interactingPlayer.SendTunneled(itemListPacket);

                var merchantPacket = new CoinStoreMerchantListPacket();
                merchantPacket.MerchantList.PlayerGuid = (long)interactingPlayer.Guid;
                merchantPacket.MerchantList.NpcGuid = capturedNpc.Guid;
                merchantPacket.MerchantList.NameId = capturedNpc.NameId;
                foreach (var itemDefId in capturedNpc.VendorItems)
                {
                    if (!_resourceManager.ClientItemDefinitions.TryGetValue(itemDefId, out var def))
                        continue;
                    merchantPacket.MerchantList.Entries.Add(new MerchantList.Entry
                    {
                        ItemDefinitionId = itemDefId,
                        IconId = def.Icon.Id,
                        TintId = def.Icon.TintId,
                        NameId = def.NameId,
                        DescriptionId = def.DescriptionId,
                        PurchasableQty = -1,
                        MembersOnly = def.MembersOnly,
                        CanBuy = true
                    });
                }
                interactingPlayer.ActiveMerchantGuid = capturedNpc.Guid;
                interactingPlayer.SendTunneled(merchantPacket);
            };

            // Registered as an option rather than assigned to InteractAction so a vendor who ALSO gives
            // a quest keeps its shop - the quest wiring below used to overwrite the delegate outright.
            npc.InteractionProviders.Add(_ => [new NpcInteractionOption
            {
                IconId = ContextIcons.Merchant,
                ButtonTextId = MerchantInteractionTextId,
                Invoke = openShop
            }]);
        }

        // Quest givers/targets (from Quests.json) route their interaction through the quest manager,
        // which decides whether to offer a quest or advance/turn one in based on the player's state.
        if (_questManager.IsQuestNpc(guid))
        {
            npc.CursorId = 17;
            var questNpc = npc;
            npc.InteractionProviders.Add(interactingPlayer => _questManager.GetInteractionOptions(interactingPlayer, questNpc));
        }

        // Kill-goal targets (Quests.json goals of Type=Kill, matched by NameId): spawn as attackable
        // hostiles (red name + health bar + attack cursor) so combat abilities can target them.
        if (_resourceManager.Quests.KillTargetNameIds.Contains(definition.NameId))
            MakeQuestHostile(npc);

        // INSTANCE (Tormented Spirits!): the wandering graveyard spirits are the encounter 146
        // entries — clicking one opens the offer popup (routed by NameId in
        // CommandPacketInteractRequestHandler). Swords cursor + a comfortable click range.
        if (definition.NameId == TormentedSpiritsArenaZone.EntryNpcNameId)
        {
            npc.CursorId = 11;
            npc.InteractRange = 15;
        }

        // Friendly, interactive NPCs (vendors + quest folk) greet passers-by. Enemies, props and
        // kill-targets stay silent — CombatNpc never gets lines assigned. Characters with their own
        // authored dialogue use it; the rest get the generic retail greetings.
        if (npc is not CombatNpc &&
            (_resourceManager.NpcVendors.ContainsKey(guid) || _questManager.IsQuestNpc(guid)))
        {
            npc.AmbientLineIds = NpcOwnLineIds.TryGetValue(guid, out var ownLines)
                ? ownLines
                : AmbientGreetingIds;
        }

        npc.UpdatePosition(position, rotation);

        var tile = GetTileFromPosition(position);
        tile.Entities.TryAdd(npc.Guid, npc);

        return true;
    }

    // Collect-goal pickups (Quests.json goals of Type=Collect): interactable world objects the player clicks
    // to gather. Shared across players; per-player credit + hide are handled in QuestManager.OnCollectInteract.
    //
    // PLACEMENT LIVES IN THE ZONE SCRIPT, but the IDENTITY doesn't: the guid was handed out when Quests.json
    // loaded and is what ties this pickup to its (quest, goal), so an unknown guid means the script has gone
    // stale against Quests.json and is refused rather than spawned somewhere it can credit nothing.
    public override bool TrySpawnQuestCollectible(ulong guid, float x, float y, float z)
    {
        var collectible = _resourceManager.Quests.CollectibleSpawns.FirstOrDefault(c => c.Guid == guid);
        if (collectible is null)
        {
            _logger.LogWarning("Quest collectible {guid} is not in Quests.json - regenerate FabledRealms.lua "
                               + "(gen_fabledrealms_lua.py) after editing quest collect goals.", guid);
            return false;
        }

        if (!TryCreateNpc(guid, out var npc))
            return false;

        npc.ModelId = collectible.ModelId;
        npc.NameId = collectible.NameId;
        npc.Static = true;
        npc.Scale = _resourceManager.Models.TryGetValue(collectible.ModelId, out var model) && model.Scale != 0f
            ? model.Scale
            : 1f;
        npc.Visible = true;
        npc.CursorId = 17; // hand cursor so it's clickable

        var questCollectible = npc;
        npc.InteractAction = interactingPlayer => _questManager.OnCollectInteract(interactingPlayer, questCollectible);

        var position = new Vector4(x, y, z, 1f);
        npc.UpdatePosition(position, Quaternion.Identity);
        GetTileFromPosition(position).Entities.TryAdd(npc.Guid, npc);

        return true;
    }

    // Miner job pass 1: hand-placed ore veins (real "sanctuary"-themed mining node models, Models.txt ids
    // 612-616), each granting a real ore item (ClientItemDefinitions CategoryId 16). Shared world resource
    // nodes - gather state/respawn timer owned by GatheringManager. Not the real "Singing Crystal Mines"
    // location (that interior zone isn't stood up in this repo yet).
    //
    // PLACEMENT LIVES IN THE ZONE SCRIPT (Scripts/Zone/FabledRealms.lua, generated from
    // Resources/MiningNodes.json) - this only knows how to build one where the script says.
    public override bool TrySpawnGatheringNode(int modelId, int itemDefinitionId, string name, float x, float y, float z)
    {
        if (!TryCreateNpc(out var node))
            return false;

        node.ModelId = modelId;
        // The name is the deposit you're mining ("Copper Vein"), not the ore it grants - but it's never
        // drawn: the nameplate renders as an ugly filled "unresolved name" pill in-game, because this
        // project's other clickable static props (quest collectibles, the daily-wheel kiosk) all label
        // themselves via a real NameId (a localized Global.Text id resolved client-side), never a bare Name
        // string with NameId left 0. We have no real localized "X Vein" string to point at, so the
        // nameplate is hidden rather than shipping the broken-looking fallback. The name stays for logs.
        node.Name = name;
        node.HideNamePlate = true;
        node.Static = true;
        node.Scale = _resourceManager.Models.TryGetValue(modelId, out var model) && model.Scale != 0f
            ? model.Scale
            : 1f;
        node.Visible = true;
        node.CursorId = 17; // hand cursor so it's clickable

        var position = new Vector4(x, y, z, SpawnPosition.W);
        node.UpdatePosition(position, SpawnRotation);
        GetTileFromPosition(position).Entities.TryAdd(node.Guid, node);

        _gatheringManager.RegisterNode(node, itemDefinitionId);

        return true;
    }

    // Snow Days snowball fight: piles of snowballs ringing the Snowhill village around the Gifting Tree.
    // Clicking one hands the player the snowball tool and drops it on the cosmetic toolbar slot.
    //
    // PLACEMENT LIVES IN THE ZONE SCRIPT (generated from Resources/SnowballPiles.json). The shipped
    // positions are NOT guesses: each was measured in game with !pos, so the heights are real standing
    // ground; take any new one the same way.
    public override bool TrySpawnSnowballPile(float x, float y, float z, float heading)
    {
        var position = new Vector4(x, y, z, 1f);
        var rotation = new Quaternion(MathF.Sin(heading), 0f, MathF.Cos(heading), 0f);

        // The year-round pile: its ordinary name, no badge.
        if (CreateSnowballPile(position, rotation, SnowballTool.PileNameId, 0) is not { } pile)
            return false;

        // Remember where the piles ended up: the Snowmen Invaders event spawns its wave around them, so it
        // has to read the same script-driven placement rather than carry a second copy that could drift.
        SnowballPilePositions.Add(position);

        // ...and the piles themselves, because the Snowmen Invaders event swaps them out for its own while
        // it runs (see SetSnowballPileEventState).
        SnowballPiles.Add(pile);

        return true;
    }

    // One snowball pile. Shared by the permanent placement above and by the Snowmen Invaders event, which
    // puts up its own set under a different name and with a badge - so the two can never drift apart in
    // model, scale, cursor, sparkle or what clicking one actually does.
    internal Npc? CreateSnowballPile(Vector4 position, Quaternion rotation, int nameId, int badgeImageId)
    {
        if (!TryCreateNpc(out var pile))
            return null;

        pile.ModelId = SnowballTool.PileModelId;
        pile.NameId = nameId;
        pile.NotificationImageSetId = badgeImageId;
        pile.Static = true;
        pile.Scale = _resourceManager.Models.TryGetValue(SnowballTool.PileModelId, out var model) && model.Scale != 0f
            ? model.Scale
            : 1f;
        pile.Visible = true;
        pile.CursorId = 17; // hand cursor so it's clickable

        pile.InteractAction = player => SnowballTool.Give(player, _resourceManager);

        pile.UpdatePosition(position, rotation);
        GetTileFromPosition(position).Entities.TryAdd(pile.Guid, pile);

        // The sparkle rides the prop. Attached rather than world-played so it follows the pile and cleans up
        // with it; sent on every viewer's first sight of it, not just to whoever is standing here at startup.
        pile.AttachedEffectId = SnowballTool.PileSparkleFxId;
        pile.AttachedEffectTagId = SnowballTool.PileSparkleTagId;

        return pile;
    }

    #region Client Is Ready

    public override void OnClientIsReady(Player player)
    {
        SendQuickChatData(player);

        SendPointOfInterests(player);

        // Level-scaled character stats for the active job, plus full HP/mana. This also caches the stats
        // on the player and sends the hitpoints + mana packets.
        player.RecalculateStats(refill: true);

        SendReferenceData(player);

        SendCoinStoreItemList(player);

        SendAdventurersJournalInfo(player);

        // LOGIN ONLY — not on re-zone. This handler runs on EVERY zone-in to the overworld (including
        // the return from the Frostfang arena), and PacketLoadWelcomeScreen makes the client pop the
        // Welcome screen each time it arrives — on the return trip it opened OVER the encounter's
        // victory score screen (user report 2026-07-04). Returning from a battle is a plain re-zone,
        // not a fresh login. (The other reference-data sends above are invisible/idempotent — left
        // alone to keep this change minimal.)
        if (!player.LoginBurstSent)
        {
            player.LoginBurstSent = true;
            SendWelcomeInfo(player);
        }

        SendPlayerCustomizations(player);

        SendMembershipSubscriptionInfo(player);

        SendListOfActivities(player);

        SendInGamePurchase(player);

        // SendPetList(player); // DISABLED - now sent in GatewayConnection immediately after ClientPcData

        player.SendTunneled(new PacketZoneDoneSendingInitialData());
        player.SendTunneled(new ClientUpdatePacketDoneSendingPreloadCharacters());

        SendFriendList(player);
        SendIgnoreList(player);

        UpdateFriendStatus(player);

        // Repopulate the Hero's Journal + tracker for any in-progress quest after a relog (the client's
        // quest UI starts empty; player.Quests is restored from the DB but the packets must be replayed).
        // LOGIN ONLY: the client keeps the journal across a re-zone (e.g. returning from the Frostfang
        // arena), so re-sending here would append duplicate rows that completion can't fully clear.
        if (!player.JournalRestored)
        {
            player.JournalRestored = true;
            _questManager.RestoreJournal(player);
        }

        // Force the combat HUD OFF on every zone-in. op41 sub132/133 are LATCHING client state and their
        // appliers are edge-guarded, so a client that came up already flagged (a crash, a mid-fight zone
        // change, a server restart) would otherwise sit there with a health bar over every npc and nothing
        // able to clear it. SendWorldCombatState(false) drives a guaranteed true->false edge.
        player.SendWorldCombatState(false);

        SpawnTrainingDummy(player);

        SpawnGrowlerWolf(player);

        // COMBAT WIP: populate the left ability toolbar on zone load (so we don't have to swap jobs to
        // trigger it). Combat/"fighting" state is NOT set here — it's set on the first attack (in the
        // StartAbility handler) so job swaps still work until you swing.
        SendJobAbilityToolbar(player);
    }

    // The load screen has dropped — the client reliably accepts everything from here (the Frostfang
    // AddNpc lesson: packets sent during the load screen can be discarded).
    public override void OnClientFinishedLoading(Player player)
    {
        // EVERY overworld entry (not just login): re-point the tracker arrow at the tracked quest's
        // ACTIVE goal. A goal that completes inside a battle instance activates its next goal there,
        // where the next goal's NPC isn't spawned — that in-arena target update is skipped, so a
        // returning player kept the stale pre-dungeon arrow (live 2026-07-10: still on the entry
        // spirit instead of "Return to Chloe" after winning the Tormented Spirits dungeon).
        _questManager.RefreshObjectiveTarget(player);

        // Restore the HUD/camera on every overworld load — in particular when RETURNING FROM A DUNGEON the
        // encounter/minigame end screen leaves the friends list + quest helper hidden until the server
        // acknowledges. This is the same camera+HUD restore the quest-dialog flow uses (sub29 ->
        // FUN_00a99220 -> restore camera + DismissEndScreen). On a plain login there's nothing to dismiss,
        // so it's a harmless no-op.
        player.SendTunneled(new CommandPacketQuestDialogComplete());

        // Re-send the Adventurer's Journal definitions here, AFTER the client has finished loading. The
        // copy sent in OnClientIsReady arrives before the journal UI subsystem is initialized on first
        // login, so it's stored but never drawn (the sticker pages stayed blank until a later refresh -
        // observed: they only appeared after a teleport or a quest accept, both of which re-push the data
        // once the UI is up). This send lands when the UI is ready, so the journal renders on login too.
        // The per-player completed-quest map (op209/2, sent in OnClientIsReady's RestoreJournal) persists
        // client-side across this re-send, so earned stickers keep their completed state.
        SendAdventurersJournalInfo(player);
    }

    // COMBAT: fill the ability toolbar from the player's EQUIPPED WEAPON for any job with a
    // weapon-ability kit (ninja Shadow Blades, archer Bows — see Combat/JobWeaponAbilities). Each
    // "of X" weapon grants the X special; no kit weapon equipped => an empty bar. This is the
    // zone-load populate (so no away-and-back job swap is needed).
    private void SendJobAbilityToolbar(Player player)
    {
        // Sends the bar + warms the client's FX cache (first-cast effects are otherwise invisible
        // while the on-demand asset load streams — see JobWeaponAbilities.PreloadAbilityEffects).
        if (!JobWeaponAbilities.SendToolbarWithFxPreload(player, _resourceManager))
        {
            // No weapon-ability kit on the active job - but the third slot (held power-up / snowball tool)
            // isn't the job's, and a player standing in Snowhill in a non-combat job should still see the
            // snowball they picked up. This send carries just that.
            JobWeaponAbilities.SendToolbar(player, _resourceManager);
            return;
        }

        _logger.LogInformation("Job toolbar on zone-load: profile={profile}, equipped weapon def={def}.",
            player.ActiveProfileId, player.GetEquippedWeaponDefinitionId());
    }

    // COMBAT WIP: spawn a single hostile "training dummy" NPC near the spawn point so we have a
    // target to select + attack while building ability resolution. Pushed directly to the readying
    // player; the tile-visibility system shows it to anyone else nearby. (See docs/STATUS.md.)
    private Npc? _trainingDummy;

    // High HP so the bar visibly drains over many hits instead of dying every ~10 hits and respawning
    // full (which made it look like only the last couple hits registered). Bumped to 50000 because the real
    // ninja ability damage (from the wiki: 2609 melee .. 10674 special) would otherwise one-shot a 5000 dummy.
    private const int TrainingDummyMaxHealth = 50000;

    // Label on the vendor's radial-menu entry: the client's own "Merchant" string
    // (Resources/CodeStringMappings.txt `Merchant^3227^`).
    private const int MerchantInteractionTextId = 3227;

    private void SpawnTrainingDummy(Player player)
    {
        if (_trainingDummy is null)
        {
            if (!TryCreateNpc(out var npc))
                return;

            npc.ModelId = 4;                // robgoblin_m_basic.adr — tagged "Combat NPC" in Models.txt
                                            // (the crab 1667 is a passive critter; may not get a combat
                                            //  health bar). Testing whether a real enemy model fixes it.
            npc.Name = "Training Dummy";
            npc.NameId = 0;
            npc.Disposition = 0;            // 0 = Hostile
            npc.ActiveProfile = 1;          // ★ non-default -> AddNpc apply runs SetProfileId -> color
                                            // resolver re-runs post-disposition -> hostile = RED name
                                            // (user-found 2026-07-03; default 0 skips the resolve)
            npc.Scale = 1f;
            npc.IsInteractable = false;     // no "Press X to talk" prompt — it's a combat target, not an NPC
            npc.Visible = true;
            npc.CursorId = 11;              // cursor_interaction_fight.cur -> crossed-swords attack cursor.
                                            // (was 1 "cursor_interaction_combat" which renders NO cursor in this client)

            // COMBAT WIP: make it damageable + show a health bar so abilities have a visible effect.
            npc.MaxHealth = TrainingDummyMaxHealth;
            npc.Health = TrainingDummyMaxHealth;
            npc.ShowHealthBar = true;

            // A few units off the zone spawn point so it stands in front of the player.
            var pos = new Vector4(SpawnPosition.X + 5f, SpawnPosition.Y, SpawnPosition.Z, SpawnPosition.W);
            npc.UpdatePosition(pos, SpawnRotation);

            _trainingDummy = npc;
        }

        // Make sure this player sees it immediately (don't wait on tile movement).
        player.OnAddVisibleNpcs(_trainingDummy);

        // Mark it attackable (combat cursor) so the client lets the player select it as a target.
        SendNpcRelevance(player, _trainingDummy);

        // RED-NAME TEST (2026-07-03): the AddNpc Disposition int is IGNORED client-side (the apply uses
        // the global arena flag; ctor default = 2 ALLY -> bluish name). op35/sub28 UpdateDisposition is
        // the real per-NPC lever: Disposition 0 HOSTILE -> the color resolver (sub_966460) paints the
        // overhead name RED (0xFFFF0000) as long as no static NameColor is set.
        // (2026-07-03 red-name experiments removed — the dummy's blue name is correct client behavior
        // for a non-arena zone; the nameplate color is resolved once at spawn. See docs/STATUS.md.)

        // Initialize its health bar on the client.
        SendNpcHealth(player, _trainingDummy);
    }

    // INSTANCE WIP (Frostfang Fury, step 1): the "Frostfang Growler" wolf NPC = the adventure-giver. For now
    // (per user) he stands next to the HOME spawn so we can iterate — the icy cave-mouth POI (id 59,
    // 92.81789,66.33743,554.8647) is NOT the video spot; the Sunrise video shows him out in the green grove, so
    // the real overworld location is still TBD. Neutral + interactable (clicking opens the future offer popup).
    private Npc? _growlerWolf;

    // Every real atlas dungeon's entrance widget, keyed by ActivityId - populated by TrySpawnDungeonEntrance.
    // Lets QuestManager.ResolveGoalTargetGuid route an EncounterComplete quest's tracker/breadcrumb at the
    // real dungeon mouth for ANY atlas dungeon generically, not just the two bespoke wandering-NPC
    // encounters (Frostfang/Tormented Spirits) that already had their own dedicated accessor. Live feedback
    // 2026-07-28 ("Bixies Gone Bad" tracker light was on the giver NPC instead of the dungeon entrance).
    private readonly Dictionary<int, Npc> _dungeonEntranceByActivityId = [];
    public Npc? DungeonEntrance(int activityId) => _dungeonEntranceByActivityId.GetValueOrDefault(activityId);

    private void SpawnGrowlerWolf(Player player)
    {
        if (_growlerWolf is null)
        {
            if (!TryCreateNpc(out var npc))
                return;

            npc.ModelId = 176;              // wolf.adr (basic wolf). Tint/swap to the white "frostfang" look later.
            npc.Name = "Frostfang Growler";
            npc.NameId = 0;
            npc.Disposition = 1;            // Neutral — friendly adventure-giver, NOT a combat target
            npc.Scale = 1f;
            npc.IsInteractable = true;
            npc.Visible = true;
            npc.CursorId = 11;              // cursor_interaction_fight.cur — the crossed-swords FIGHT cursor.
                                            // (cursor 1 "cursor_interaction_combat" renders NOTHING in this client
                                            // — that's why the dummy showed no cursor. 11 is the real swords one.)
                                            // ⚠️ VERIFY ON TEST: if the fight cursor turns the wolf into an attack
                                            // target and breaks click-to-open, fall back to 5 (talk) + the marker.
            // The purple crossed-swords encounter badge ABOVE the head is NOT a nameplate field at
            // all — it's a NOTIFICATION (op35/sub10 AddNotifications -> OverHeadBitmapElement at
            // offset (0,-0.9,0)); see the badge push after OnAddVisibleNpcs below.
            npc.NameplateImageId = 0;
            npc.ImageSetId = 0;
            npc.ShowHealthBar = false;      // MaxHealth stays 0 => not damageable

            // Out in Snowhill, east of Gerold (150.9, 23.7, 381.4) — matching the quest text "knock out
            // Frostfang Growlers to the east of Gerold". Position measured in-game via !arena.
            var pos = new Vector4(202.2f, 34.6f, 504.7f, 1f);
            npc.UpdatePosition(pos, SpawnRotation);

            _growlerWolf = npc;
        }

        player.OnAddVisibleNpcs(_growlerWolf);

        // Same recipe the training dummy uses to be clickable: tell the client it has a cursor (relevance).
        SendNpcRelevance(player, _growlerWolf);

        SendGrowlerBadge(player);
    }

    // The reference video's crossed-swords badge floating ABOVE the Growler's head. RE'd 2026-07-02:
    // op35/sub10 AddNotifications (byte-exact vs live 2014 pcap) -> client attaches an
    // OverHeadBitmapElement above the character. ImageId 24 in NotificationImages.txt =
    // tint-circle + circle + crossed-swords icon 1345 (the combat-encounter badge art).
    //
    // ★ RED BADGE + RED MINIMAP DOT, BLUE NAME (2026-07-05): the color is driven by the notification's
    // Type field, NOT the NPC disposition — so we get the red combat look while the name stays neutral
    // blue. GROUND TRUTH: in BOTH 2014 captures the img-24 combat-encounter badge is sent with
    // Type = 3 (the "combat" category) + Unknown3 = 7, and EVERY red minimap-dot notification is Type 3
    // (04-01 idx 1928/4291). Our old badge used the default Type = 1 (a quest category) -> the blue
    // bubble/dot the user saw. Type 3 tints the bubble (NotificationImages layer 369, APPLY_TINT) and
    // the minimap blip (layer 1345) red without any disposition change.
    private void SendGrowlerBadge(Player player)
    {
        if (_growlerWolf is null)
            return;

        var badge = new PlayerUpdatePacketAddNotifications();
        badge.Notifications.Add(new NotificationInfo
        {
            Guid = _growlerWolf.Guid,
            ImageId = 24,
            Type = 3,        // COMBAT category -> red badge tint + red minimap dot (live img-24 value)
            Unknown3 = 7,    // live combat-badge value (was default 1)
            Unknown10 = true // constant 1 across all live samples
        });

        player.SendTunneled(badge);
    }

    // INSTANCE WIP: the Frostfang Growler adventure-giver wolf.
    public Npc? GrowlerWolf => _growlerWolf;

    // INSTANCE (Tormented Spirits!): a wandering Tormented Spirit — the encounter 146
    // entry NPC the tracker arrow / breadcrumb points at for EncounterComplete(146) goals.
    public Npc? TormentedSpiritEntry()
    {
        foreach (var (id, definition) in _resourceManager.Npcs)
        {
            if (definition.NameId != TormentedSpiritsArenaZone.EntryNpcNameId)
                continue;

            if (TryGetNpc(NpcGuidBase + (ulong)id, out var npc))
                return npc;
        }

        return null;
    }

    // Re-push the Growler wolf to a player (e.g. after a "!grove" teleport re-zone).
    public void ShowGrowlerWolf(Player player)
    {
        if (_growlerWolf is not null)
        {
            player.OnAddVisibleNpcs(_growlerWolf);
            SendGrowlerBadge(player);
        }
    }

    // (SendNpcRelevance / SendNpcHealth moved to BaseZone — shared with the Frostfang arena zone.)

    // COMBAT WIP: the live combat target (training dummy).
    public Npc? TrainingDummy => _trainingDummy;

    // COMBAT WIP: eternal training dummy — instead of despawn/respawn (which stacked extra dummies
    // across relogs), just reset it to full HP and refresh the bar so it's always there to hit.
    public void ResetTrainingDummy()
    {
        var dummy = _trainingDummy;

        if (dummy is null)
            return;

        dummy.Health = dummy.MaxHealth;

        foreach (var zonePlayer in Players)
            SendNpcHealth(zonePlayer, dummy);
    }

    // ---- Quest kill targets (world hostiles from Npcs.json, made attackable by a Kill goal) ----

    // HP of a quest kill target (~2 ninja melee swings at current damage numbers).
    private const int QuestHostileHealth = 5000;

    // Live dying-wolf graceful-removal params (04-01 capture): death clip + poof fx 5017.
    private const int QuestHostileDeathFxId = 5017;
    private const int QuestHostileDeathHoldMs = 2000;

    // How long a defeated quest hostile stays gone before respawning (shared world spawns —
    // a 6-kill goal only has 5 spirit spawns, so respawns are required to finish it).
    private const int QuestHostileRespawnMs = 20_000;

    private static void MakeQuestHostile(Npc npc)
    {
        npc.Disposition = 0;        // hostile — with NameColor 0 the client resolves the name RED...
        npc.ActiveProfile = 1;      // ...but only if a non-default profile re-runs the color resolver
        npc.EnemyStatus = true;     // AddNpc "render as enemy" flag (set on every live camp hostile)
        npc.CursorId = 11;          // crossed-swords attack cursor (delivered via NpcRelevance)
        npc.MaxHealth = QuestHostileHealth;
        npc.Health = QuestHostileHealth;
    }

    // Re-spawn a defeated quest hostile from its Npcs.json definition (same guid).
    private void RespawnQuestHostile(NpcDefinition definition)
    {
        var guid = NpcGuidBase + (ulong)definition.Id;

        if (!TryCreateNpc(guid, out var npc))
            return;

        npc.ModelId = definition.ModelId;
        npc.NameId = definition.NameId;
        npc.TextureAlias = definition.TextureAlias;
        npc.Name = definition.Name;
        npc.Static = definition.Static;
        npc.Scale = _resourceManager.Models.TryGetValue(definition.ModelId, out var model) && model.Scale != 0f
            ? model.Scale
            : 1f;
        npc.Visible = true;

        MakeQuestHostile(npc);

        npc.UpdatePosition(definition.Position, definition.Rotation);

        var tile = GetTileFromPosition(definition.Position);
        tile.Entities.TryAdd(npc.Guid, npc);
    }

    // ---- World combat enemies (curated hostile creatures, spawned as CombatNpc) ----

    // Baseline level for overworld enemies (drives HP/damage/XP via CombatNpc.InitializeFromLevel).
    // Modest so early players can fight them; tune per-region later.
    private const int WorldEnemyLevel = 3;

    // How long a defeated world enemy stays gone before a fresh one respawns at its post. Kept
    // short so clearing a spot doesn't leave you standing around with nothing to shoot.
    private const int WorldEnemyRespawnMs = 8_000;

    // True when an overworld NPC definition spawns as a killable hostile CombatNpc
    // (its model is a dungeon-enemy model and it isn't claimed as a vendor / quest giver / quest kill-target).
    // Used both to spawn the world enemies and to keep Battle-Starter anchors AWAY from them.
    private bool IsWorldEnemyDefinition(NpcDefinition definition)
    {
        // NOTE: quest KILL-TARGETS are deliberately NOT excluded here. A kill-target with a combat
        // model spawns as a full world enemy — aggressive AI, fights back — and its death still
        // credits the quest goal (OnNpcKilled matches by NameId regardless of spawn path). Excluding
        // them (as this once did) downgraded the whole Bixie camp to passive punching bags the moment
        // a quest counted them. Kill-targets with NON-combat models still fall through to the passive
        // MakeQuestHostile path below.
        var guid = NpcGuidBase + (ulong)definition.Id;
        return !_resourceManager.NpcVendors.ContainsKey(guid)
            && !_questManager.IsQuestNpc(guid)
            && Sanctuary.Game.Dungeons.DungeonCatalog.EnemyModelIds.Contains(definition.ModelId);
    }

    private void SpawnWorldEnemy(NpcDefinition definition, Vector4 position, Quaternion rotation)
    {
        if (!TryCreateCombatNpc(out var enemy))
            return;

        enemy.ModelId = definition.ModelId;
        enemy.NameId = definition.NameId;
        enemy.TextureAlias = definition.TextureAlias;
        enemy.Name = definition.Name;
        enemy.Static = false;               // MUST be false — the zone tick loop skips Static NPCs (no AI)
        enemy.Scale = _resourceManager.Models.TryGetValue(definition.ModelId, out var model) && model.Scale != 0f
            ? model.Scale
            : 1f;
        enemy.Visible = true;
        enemy.EnemyStatus = true;           // AddNpc "render as enemy" flag (red name)
        enemy.ActiveProfile = 1;            // re-runs the client name-color resolver -> red
        enemy.CursorId = 11;                // crossed-swords attack cursor
        enemy.IsInteractable = false;       // it's a combat target, not an NPC — no "Press X to talk" prompt
                                            // (the dungeon enemies already do this; the overworld ones didn't)
        enemy.ShowHealthBar = true;
        enemy.MovementType = 2;             // PHYSICS — grounded with gravity (CONTROLLER/1 left them "flying")

        enemy.InitializeFromLevel(WorldEnemyLevel, Entities.EnemyTiers.FromName(definition.Name)); // HP/damage/XP by tier
        enemy.Speed = enemy.CombatSpeed;

        // The player's ability handler damages via Npc.Health and routes the kill through OnNpcKilled, so
        // mirror the combat HP into the Npc fields (that's what makes it a damageable target + drives the bar).
        enemy.MaxHealth = enemy.MaxHitpoints;
        enemy.Health = enemy.CurrentHitpoints;

        enemy.SpawnPosition = position;
        enemy.SpawnRotation = rotation;
        enemy.LastSentPosition = position;
        enemy.UpdatePosition(position, rotation);

        var tile = GetTileFromPosition(position);
        tile.Entities.TryAdd(enemy.Guid, enemy);
    }

    // Re-spawn a fresh world enemy at a defeated one's post (captured model/name/level/position).
    private void RespawnWorldEnemy(int modelId, int nameId, string? name, string? textureAlias, float scale,
        int level, Vector4 spawnPosition, Quaternion spawnRotation)
    {
        if (!TryCreateCombatNpc(out var enemy))
            return;

        enemy.ModelId = modelId;
        enemy.NameId = nameId;
        enemy.TextureAlias = textureAlias;
        enemy.Name = name;
        enemy.Static = false;
        enemy.Scale = scale;
        enemy.Visible = true;
        enemy.EnemyStatus = true;
        enemy.ActiveProfile = 1;
        enemy.CursorId = 11;
        enemy.IsInteractable = false;       // combat target, not a talkable NPC (same as the initial spawn)
        enemy.ShowHealthBar = true;
        enemy.MovementType = 2;             // PHYSICS — grounded with gravity (CONTROLLER/1 left them "flying")

        enemy.InitializeFromLevel(level, Entities.EnemyTiers.FromName(name));
        enemy.Speed = enemy.CombatSpeed;
        enemy.MaxHealth = enemy.MaxHitpoints;
        enemy.Health = enemy.CurrentHitpoints;

        enemy.SpawnPosition = spawnPosition;
        enemy.SpawnRotation = spawnRotation;
        enemy.LastSentPosition = spawnPosition;
        enemy.UpdatePosition(spawnPosition, spawnRotation);

        var tile = GetTileFromPosition(spawnPosition);
        tile.Entities.TryAdd(enemy.Guid, enemy);

        // Explicitly push the fresh enemy to every already-present player who can see its tile. The INITIAL
        // spawn is picked up by each player's load-time visibility sweep, but a mid-session respawn isn't —
        // so without this the enemy is alive + targetable server-side yet never rendered or known to the
        // client, i.e. "I'm standing right by enemies but nothing gets shot." (Guard against the rare double-
        // send when UpdatePosition's tile transition already notified the player.)
        foreach (var player in Players)
        {
            if (enemy.VisiblePlayers.ContainsKey(player.Guid))
                continue;

            var playerTile = GetTileFromPosition(player.Position);
            if (playerTile == tile || playerTile.VisibleTiles.Contains(tile))
            {
                player.OnAddVisibleNpcs([enemy]);
                enemy.OnAddVisiblePlayers(player);
            }
        }
    }

    // ---- Dungeon entrances (atlas notif=3 POIs -> the BIG walk-through dungeon) ----

    // Each atlas dungeon marker is a NotificationType=3 PointOfInterest. Fast-travel drops you at its
    // overworld position, where we place a clickable entrance whose click opens the dungeon start panel;
    // GO! routes through EncounterParticipantRequestEntranceHandler -> EnterEncounterArena. The atlas
    // markers map to the BIG walk-through dungeon worlds (the real dungeon worlds like sg_robgoblin_trove),
    // NOT the small scattered encounter arenas. Look them up by POI id (DungeonCatalog.ByAtlasPoi) — the
    // catalog is keyed by the REAL client activity id now, so the old "900000 + poiId" key is gone.

    // Model 511 = human_invisible_m.adr (Models.txt): an invisible CHARACTER actor — renders
    // nothing, is still sent to the client (so it's clickable via its nameplate/actor box), and unlike the
    // "Invisible Block" widget it has no solid environment collision, so the player (who teleports onto
    // this exact spot) doesn't get stuck inside it.
    private const int AtlasEntranceModelId = 511;

    // PLACEMENT LIVES IN THE ZONE SCRIPT, keyed by the atlas POI id - that id is what the dungeon catalog
    // is indexed by, so the script only has to say "an entrance stands here, for this marker" and the
    // dungeon, its name and its offer are resolved here. Most POIs are ordinary map markers with no dungeon
    // behind them; that's expected, so it's a debug line and not a warning.
    public override bool TrySpawnDungeonEntrance(int poiId, float x, float y, float z, float heading)
    {
        if (!Sanctuary.Game.Dungeons.DungeonCatalog.ByAtlasPoi.TryGetValue(poiId, out var dungeon))
        {
            _logger.LogDebug("POI {poi} has no dungeon behind it - no entrance placed.", poiId);
            return false;
        }

        if (!TryCreateNpc(out var entrance))
            return false;

        // The entrance is an INVISIBLE clickable widget (model 69 "widget_01.adr" = "Invisible Block"):
        // no creature stands at the dungeon mouth, but the actor is still sent to the client (required
        // to be clickable) with the dungeon's NAME on its floating nameplate as the click cue. Clicking
        // it opens the start panel. (A truly Visible=false NPC isn't sent to the client at all, so it
        // couldn't be clicked — hence an invisible-but-present model instead.)
        entrance.ModelId = AtlasEntranceModelId;
        entrance.NameId = dungeon.TitleNameId;   // floating nameplate = the dungeon's name (the click cue)
        entrance.Name = dungeon.Comment;
        entrance.Static = true;
        entrance.Scale = 1f;
        entrance.Visible = true;                 // present/clickable; the model itself renders nothing
        entrance.HideNamePlate = false;          // keep the nameplate as the visible target
        entrance.CursorId = 11;                  // crossed-swords / adventure cursor on hover
        entrance.InteractRange = 18;
        entrance.ShowCombatBadge = true;         // red crossed-swords badge + red minimap dot

        var pos = new Vector4(x, y, z, 1f);
        var rot = new Quaternion(MathF.Sin(heading), 0f, MathF.Cos(heading), 0f);
        var capturedDungeon = dungeon;
        entrance.InteractAction = player => SendDungeonOffer(player, capturedDungeon);

        entrance.UpdatePosition(pos, rot);
        GetTileFromPosition(pos).Entities.TryAdd(entrance.Guid, entrance);
        _dungeonEntranceByActivityId[dungeon.ActivityId] = entrance;

        return true;
    }

    // Open the dungeon start panel (adventure offer + auto-ready GO!) for a data-driven dungeon —
    // the same offer/handshake the Growler/Spirit entries use, keyed to this dungeon's activity id so the
    // GO! button routes to its EncounterArenaZone.
    public static void SendDungeonOffer(Player player, Sanctuary.Game.Dungeons.DungeonDefinition dungeon)
    {
        const int instanceId = 1;

        foreach (var state in new[] { 2, 3, 4 })
        {
            player.SendTunneled(new EncounterStatePacket
            {
                EncounterId = dungeon.ActivityId,
                InstanceId = instanceId,
                State = state,
            });
        }

        // Same primary(+bonus) objective rows the real launch packet defines (EncounterArenaZone's
        // MakeLaunch) — mirrored here so the pre-entry info/offer popup shows the bonus goal too, not
        // just the main "defeat everyone" row, and carries the dungeon's real per-goal XP reward.
        List<EncounterObjective> objectives =
        [
            new EncounterObjective
            {
                ObjectiveId = dungeon.ActivityId, NameId = dungeon.DescriptionId,
                DescriptionId = dungeon.DescriptionId,
                Status = 1, Count = 0, Total = 1, Xp = dungeon.Xp,
            },
        ];
        if (dungeon.HasBonus)
        {
            objectives.Add(new EncounterObjective
            {
                ObjectiveId = 900000 + dungeon.ActivityId, NameId = dungeon.BonusNameId,
                DescriptionId = dungeon.BonusNameId,
                Status = 1, Count = 0, Total = dungeon.BonusTotal,
            });
        }

        player.SendTunneled(new EncounterDetailsResponsePacket
        {
            Unknown = dungeon.ActivityId,
            Unknown2 = instanceId,
            NameId = dungeon.TitleNameId,
            DescriptionId = dungeon.DescriptionId,
            Difficulty = dungeon.Difficulty,
            IconId = dungeon.IconId,
            MiniGameType = 4, // COMBAT
            MembersOnly = true, // gates the win screen's "Members Only Bonus" Coins box - see EncounterArenaZone.MakeLaunch
            Objectives = objectives,
            PreviewRewards = FrostfangArenaZone.GetPrizePreviewFor(player),
            PreviewCoins = dungeon.Coins,
            PreviewXp = FrostfangArenaZone.PrizeXp,
            RewardXp = dungeon.Xp,
            MemberCoins = dungeon.Coins,
            ProfileType = FrostfangArenaZone.CombatProfileType,
            ActivityId = dungeon.ActivityId,
        });

        // Auto-complete the ready handshake so the spinner flips to the green GO! (same as the two hand-built
        // encounters — no "!ready" chat command needed).
        _ = Task.Run(async () =>
        {
            await Task.Delay(600);
            player.SendTunneled(new EncounterZoneIsReadyPacket());
            player.SendTunneled(new EncounterStatePacket
            {
                EncounterId = dungeon.ActivityId,
                InstanceId = instanceId,
                State = 5,
            });
        });
    }

    // Opens the wheel: minigame definition + activity launch for activity 8, Type=22 (the client's only
    // IS_MICRO type). The SWF name has to come from us - the client's MiniGameData.txt has the wheel's row
    // but leaves every asset column blank. The player also needs the "wheel" repeating activity or the
    // client refuses to start it; DailyWheelGame.SendSpinAvailability covers that.
    public void LaunchSpinForTheWinGame(Player player, ClientActivityDefinition clientActivityDefinition)
    {
        const int ActivityId = 8;
        const string WheelSwf = "game_wheel.gfx";
        const int WheelIconId = 20985; // the client's own MiniGameData.txt row for game 8

        var miniGameInfo = new MiniGameInfo()
        {
            NameId = clientActivityDefinition.DisplayNameId,
            IconId = WheelIconId,
            DescriptionId = clientActivityDefinition.DisplayDescriptionId,
            Difficulty = clientActivityDefinition.Difficulty,
            ProfileType = 0,
            Type = 22, // Wheel - native Client\UI\game_wheel.swf widget, not a hosted Flash game
            PreselectedGameId = ActivityId,

            // The start panel and framed window come with the minigame launch. These flags are the only
            // levers on it - "/wheel flag <name> <0|1>" toggles them live.
            Unknown11 = WheelUnknown11,
            ShowStarCounter = WheelShowStarCounter,
            ShowStatusIcon = WheelShowStatusIcon,
            ShowActionBar = WheelShowActionBar,
            ShowEndDialog = WheelShowEndDialog,

            Unknown13 = WheelSwf
        };

        // BackgroundSwf is what the client draws behind the minigame. Empty, like every row in the
        // client's own MiniGameGroupData.txt - "/wheel bg <name>" puts one back for testing.
        var miniGameGroupInfo = new MiniGameGroupInfo()
        {
            Id = 69,
            NameId = clientActivityDefinition.DisplayNameId,
            DescriptionId = clientActivityDefinition.DisplayDescriptionId,
            IconId = WheelIconId,
            BackgroundSwf = WheelBackgroundSwf
        };

        using var writer = new PacketWriter();

        miniGameInfo.Serialize(writer);

        writer.Write(0); // Unused
        writer.Write(0); // Unused

        writer.Write(miniGameGroupInfo.Serialize());

        var clientActivityLaunchPacketInviteDetails = new ClientActivityLaunchPacketInviteDetails(ActivityId, 0)
        {
            Guid = player.Guid,
            Inviter = "Test",
            Members =
            {
                new()
                {
                    Id = 1,
                    Guid = player.Guid,
                    InviteStatus = 2,
                    IsFoundingMember = true
                }
            },
            Request =
            {
                RequestorGuid = player.Guid,
                SysHashkey = JenkinsHelper.OneAtATimeHash("Minigame"),
                ReqId = 69420,
                MinMembers = 1,
                MaxMembers = 1,
                ImageSetId = clientActivityDefinition.ImageSetId,
                NameStringId = clientActivityDefinition.DisplayNameId,
                DescStringId = clientActivityDefinition.DisplayDescriptionId,
                SysSpecificData = writer.Buffer
            }
        };

        player.SendTunneled(clientActivityLaunchPacketInviteDetails);

        var clientActivityLaunchPacketActivityLaunched = new ClientActivityLaunchPacketActivityLaunched(ActivityId, 0);

        clientActivityLaunchPacketActivityLaunched.Guids.Add(player.Guid);

        player.SendTunneled(clientActivityLaunchPacketActivityLaunched);

        var miniGameInfoPacket = new MiniGameInfoPacket(ActivityId, -1, -1)
        {
            Info = miniGameInfo
        };

        player.SendTunneled(miniGameInfoPacket);

        _logger.LogInformation("Spin For The Win: launch attempt (Type=22, game_wheel.gfx) sent to {name}.", player.Name);
    }

    // (The world "Spin For The Win!" kiosk that used to stand at spawn is gone: it existed only as a way
    // around the minigames Browser's greyed-out Play button, which is fixed now that the server grants the
    // "wheel" repeating activity - see DailyWheelGame.SendSpinAvailability.)

    // ---- Wandering combat-encounter entries ("Battle Starters") ----
    //
    // Retail placed a wandering "Battle Starter" creature for each combat encounter, standing among its own
    // kind (per the FR bestiary: 58 battle-starter mob types named Robgoblin*/Thugawug*/Cray Marauder/Frostfang
    // Snarler/...). We do the same: one EncounterEntryNpc per small ARENA encounter (the 900000+ atlas
    // walk-throughs already have their own clickable atlas entrances), wearing that encounter's boss model,
    // placed next to an existing overworld NPC of the same theme (matched by name) so it stands where its kin
    // roam. It ambles peacefully; clicking it opens the encounter start panel (SendDungeonOffer -> GO!).

    // Battle-map -> theme keyword, the fallback anchor search term when an encounter's own title
    // nouns don't match any overworld NPC name.
    private static readonly Dictionary<string, string> EncounterThemeKeyword = new()
    {
        ["sg_random_encounter_skullcamp"] = "Robgoblin",
        ["sg_random_encounter_treefort"] = "Thugawug",
        ["sg_random_encounter_creek"] = "Wolf",
        ["sg_random_encounter_clearing"] = "Wolf",
        ["sh_random_encounter_01"] = "Troll",
        ["bw_random_encounter_bristlewood_01"] = "Floren",
        ["bw_random_encounter_01"] = "Grave",
        ["bw_random_encounter_02"] = "Hooligan",
        ["bw_random_encounter_03"] = "Bixie",
        ["bw_random_encounter_thistlerow_01"] = "Asp",
        ["ss_random_encounter_01"] = "Cray",
    };

    private void SpawnEncounterEntryNpcs()
    {
        // Overworld NPCs with a real (non-origin) position = candidate anchor spots (valid, walkable ground).
        // EXCLUDE the ones that spawn as killable world enemies: anchoring a Battle Starter next to its themed
        // "kin" put it right on top of a same-model enemy, so you couldn't tell the encounter-entry creature from
        // the mobs you fight ("mixed in with the killable enemies"). Anchor to peaceful NPCs (towns / quest hubs)
        // so the badged Battle Starter stands clearly apart from the combat crowd.
        var anchors = _resourceManager.Npcs.Values
            .Where(d => (d.SpawnPosition[0] != 0f || d.SpawnPosition[2] != 0f) && !IsWorldEnemyDefinition(d))
            .ToList();
        if (anchors.Count == 0)
            return;

        var used = new HashSet<int>();   // anchor ids already claimed, so entries don't stack on each other
        int spreadCursor = 0;
        int spawned = 0;

        foreach (var dungeon in Sanctuary.Game.Dungeons.DungeonCatalog.ByActivity.Values)
        {
            if (dungeon.PoiId != 0)
                continue; // atlas walk-through dungeons already have their own entrances (PoiId = the marker)

            var anchor = FindThematicAnchor(dungeon, anchors, used)
                         ?? PickSpreadAnchor(anchors, used, ref spreadCursor);
            if (anchor is null)
                break; // ran out of anchors
            used.Add(anchor.Id);

            if (!TryCreateEncounterEntryNpc(out var entry))
                continue;

            // The encounter's boss (or first) enemy is the creature that represents it in the world.
            var lead = System.Array.Find(dungeon.Enemies, e => e.Boss) ?? dungeon.Enemies[0];
            entry.ModelId = lead.ModelId;
            entry.NameId = dungeon.TitleNameId;  // floating nameplate = the encounter's name (the click cue)
            entry.Name = dungeon.Comment;
            entry.Static = false;                // MUST be false — the tick loop skips Static NPCs (no wander)
            entry.Scale = (_resourceManager.Models.TryGetValue(lead.ModelId, out var model) && model.Scale != 0f
                ? model.Scale : 1f) * 1.15f;     // a touch larger than its kin so the "leader" reads as special
            entry.Visible = true;
            entry.CursorId = 11;                 // crossed-swords adventure cursor on hover
            entry.MovementType = 2;              // PHYSICS — grounded amble (matches the world enemies)
            entry.InteractRange = 6;
            entry.ShowHealthBar = false;
            entry.ShowCombatBadge = true;        // red crossed-swords badge - see this method's own header comment

            // A few units off the anchor NPC so it doesn't stack exactly on top of it.
            var pos = new Vector4(anchor.Position.X + 4f, anchor.Position.Y, anchor.Position.Z + 4f, 1f);

            var captured = dungeon;
            entry.InteractAction = player => SendDungeonOffer(player, captured);

            entry.UpdatePosition(pos, anchor.Rotation);
            entry.StartWander(pos);
            GetTileFromPosition(pos).Entities.TryAdd(entry.Guid, entry);
            spawned++;
        }

        _logger.LogInformation("Spawned {count} wandering combat-encounter entries.", spawned);
    }

    // Find an overworld NPC whose name matches the encounter's theme — first by the significant words
    // in the encounter's own title (e.g. "Band of Robgoblins!" -> "Robgoblin"), then by the battle-map's
    // theme keyword — so each Battle Starter stands among its own kind. Null if nothing matches.
    private static NpcDefinition? FindThematicAnchor(Sanctuary.Game.Dungeons.DungeonDefinition dungeon,
        List<NpcDefinition> anchors, HashSet<int> used)
    {
        var keywords = new List<string>();
        foreach (var raw in dungeon.Comment.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var word = new string(raw.Where(char.IsLetter).ToArray());
            if (word.Length < 4)
                continue;
            keywords.Add(word);
            if (word.EndsWith('s'))
                keywords.Add(word[..^1]); // singular ("Robgoblins" -> "Robgoblin")
        }
        if (EncounterThemeKeyword.TryGetValue(dungeon.World, out var themeKeyword))
            keywords.Add(themeKeyword);

        foreach (var keyword in keywords)
            foreach (var anchor in anchors)
                if (!used.Contains(anchor.Id) && !string.IsNullOrEmpty(anchor.Name)
                    && anchor.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    return anchor;

        return null;
    }

    // Fallback: hand out an unused anchor, striding through the list so the fallbacks spread across
    // the world instead of clumping.
    private static NpcDefinition? PickSpreadAnchor(List<NpcDefinition> anchors, HashSet<int> used, ref int cursor)
    {
        int stride = Math.Max(1, anchors.Count / 60);
        for (int i = 0; i < anchors.Count; i++)
        {
            var anchor = anchors[(cursor + i * stride) % anchors.Count];
            if (!used.Contains(anchor.Id))
            {
                cursor = (cursor + i * stride + stride) % anchors.Count;
                return anchor;
            }
        }
        return null;
    }

    // COMBAT: kill routing for this zone — the eternal training dummy resets, quest kill targets
    // credit the killer's active Kill goal and respawn after a delay.
    // (The Frostfang encounter wolves live in FrostfangArenaZone, which has its own override.)
    // Clear the dead NPC's overhead/minimap notification entry (op35/sub11 RemoveNotifications) on
    // every client that had it visible, plus the killer. THIS is what unsticks a bow after a kill: the client
    // keeps auto-firing (Target=0, server picks the nearest) as long as it holds a combat entry for the enemy
    // it engaged; when that enemy dies WITHOUT its notification being cleared, the client stays latched to the
    // corpse and silently stops sending fire requests until a full state reset (job swap). Every other combat
    // zone (Frostfang, Spirits, the walk-through dungeons) already sends this on mob death — the overworld was
    // the only one that didn't, which is exactly why the dungeon worked and the open world didn't.
    private static void BroadcastKillSignal(Player killer, Npc npc)
    {
        var clear = new PlayerUpdatePacketRemoveNotifications { Guids = { npc.Guid } };
        foreach (var viewer in npc.VisiblePlayers.Values)
            viewer.SendTunneled(clear);
        if (!npc.VisiblePlayers.ContainsKey(killer.Guid))
            killer.SendTunneled(clear);
    }

    // How far a party member can be from the kill and still share its XP. Stops a party-mate on the
    // other side of the map from leeching, but is generous enough that anyone actually in the fight counts.
    private const float XpShareRange = 100f;

    // Overworld kills pay the whole PARTY, not just whoever landed the last hit — so players fighting
    // together both level up. Every nearby member gets the FULL reward (not a split), which is how the dungeon
    // zones already pay out. Members must be in this zone and within XpShareRange of the kill.
    // Solo players (no party) are unaffected: the killer just gets their XP as before.
    private void AwardSharedXp(Player killer, int xp, Vector4 killPos)
    {
        killer.AwardXp(xp);

        var party = _partyManager.GetParty(killer);
        if (party is null)
            return;

        var range2 = XpShareRange * XpShareRange;

        foreach (var member in party.Members)
        {
            if (member.Guid == killer.Guid || member.Zone != this)
                continue;

            var dx = member.Position.X - killPos.X;
            var dz = member.Position.Z - killPos.Z;
            if (dx * dx + dz * dz > range2)
                continue; // too far from the fight to have taken part

            member.AwardXp(xp);
        }
    }

    public override void OnNpcKilled(Player killer, Npc npc)
    {
        if (ReferenceEquals(npc, _trainingDummy))
        {
            ResetTrainingDummy();
            return;
        }

        // Snowmen Invaders event spawns: the event owns their lifecycle (coal drops, the boss hand-off, the
        // reward list), and they must NOT go through the respawn-at-post path below - a defeated invader
        // stays down until the next battle. Checked before the generic world-enemy branch for that reason.
        if (TryHandleSnowmenKill(killer, npc))
        {
            if (npc is Sanctuary.Game.Entities.CombatNpc eventEnemy)
            {
                if (eventEnemy.IsDead)
                    return;
                eventEnemy.IsDead = true;

                // Quest credit still counts (Snowmen Disassembly is a kill goal), but NO XP for invaders -
                // they are one-hit, instantly replaced, and endless, so awarding XP would make the event an
                // XP faucet rather than a snowball fight. The Abominable Snowman still pays out.
                _questManager.OnNpcKilled(killer, npc);

                if (npc.NameId != SnowmanInvaderNameId)
                    AwardSharedXp(killer, eventEnemy.XpReward, eventEnemy.Position);
            }

            BroadcastKillSignal(killer, npc);

            // The full death presentation, same as a world enemy: play the death CLIP, hold the body, then
            // poof. Animate:true + the hold is what stops a snowman blinking out of existence the instant its
            // health hits zero.
            npc.GracefulRemoval = (true, QuestHostileDeathHoldMs, 0, QuestHostileDeathFxId, 1000);
            npc.Dispose();

            // A snowball can kill something the thrower's client never had in view (targets resolve by range,
            // not tile visibility), and Dispose only notifies VisiblePlayers - without this that player keeps
            // the corpse on screen forever.
            if (!npc.VisiblePlayers.ContainsKey(killer.Guid))
                killer.SendTunneled(new PlayerUpdatePacketRemovePlayerGracefully
                {
                    Guid = npc.Guid,
                    Animate = true,
                    Delay = QuestHostileDeathHoldMs,
                    EffectDelay = 0,
                    CompositeEffectId = QuestHostileDeathFxId,
                    Duration = 1000,
                });

            return;
        }

        // World combat enemy: award XP, play the death (clip + poof), then respawn a fresh one at its post.
        if (npc is Sanctuary.Game.Entities.CombatNpc worldEnemy)
        {
            // Idempotency guard (belt-and-suspenders with the atomic ApplyDamage): process each death
            // exactly once. Overlapping archer shots could otherwise route the same kill here repeatedly,
            // double-awarding XP and firing multiple graceful-removes that jam the client. Mirrors the
            // dungeon zone's "already removed?" guard.
            if (worldEnemy.IsDead)
                return;
            worldEnemy.IsDead = true;

            // Credit the active Kill goal of any of the killer's in-progress quests (matched by
            // NameId). World enemies are valid hunt targets too — without this, only the passive
            // MakeQuestHostile spawns credited, which is why kill-targets used to be excluded from
            // the world-enemy path (and the whole camp went passive when a quest counted them).
            _questManager.OnNpcKilled(killer, npc);

            AwardSharedXp(killer, worldEnemy.XpReward, worldEnemy.Position);

            // Capture what we need to rebuild it before Dispose() clears the entity.
            int modelId = worldEnemy.ModelId, nameId = worldEnemy.NameId, level = worldEnemy.Level;
            string? name = worldEnemy.Name, textureAlias = worldEnemy.TextureAlias;
            float scale = worldEnemy.Scale;
            var spawnPos = worldEnemy.SpawnPosition;
            var spawnRot = worldEnemy.SpawnRotation;

            BroadcastKillSignal(killer, npc); // clear the dead enemy's client notification entry (matches
                                              // every combat arena; the overworld was the only zone missing it)

            // A roaming enemy can be killed by a player who ISN'T in its VisiblePlayers set — the attack picks
            // targets by RANGE, not tile-visibility, and the mob may have shifted tiles since the client rendered
            // it. Dispose() only sends the graceful-remove to VisiblePlayers, so that killer's client would keep
            // the corpse forever ("dead body won't disappear"). Capture it, then send the killer the removal too.
            var killerSaw = npc.VisiblePlayers.ContainsKey(killer.Guid);

            npc.GracefulRemoval = (true, QuestHostileDeathHoldMs, 0, QuestHostileDeathFxId, 1000);
            npc.Dispose();

            if (!killerSaw)
                killer.SendTunneled(new PlayerUpdatePacketRemovePlayerGracefully
                {
                    Guid = npc.Guid,
                    Animate = true,
                    Delay = QuestHostileDeathHoldMs,
                    EffectDelay = 0,
                    CompositeEffectId = QuestHostileDeathFxId,
                    Duration = 1000,
                });

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(WorldEnemyRespawnMs);
                    RespawnWorldEnemy(modelId, nameId, name, textureAlias, scale, level, spawnPos, spawnRot);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "World enemy respawn failed (model {model}).", modelId);
                }
            });

            return;
        }

        // Credit the active Kill goal of any of the killer's in-progress quests (matched by NameId).
        _questManager.OnNpcKilled(killer, npc);

        // World hostiles die with the live death flow (death clip + poof) and respawn after a delay.
        if (npc.Guid > NpcGuidBase
            && _resourceManager.Npcs.TryGetValue((int)(npc.Guid - NpcGuidBase), out var definition)
            && _resourceManager.Quests.KillTargetNameIds.Contains(definition.NameId))
        {
            BroadcastKillSignal(killer, npc); // release the client's ranged target-lock (bow re-fire fix)
            npc.GracefulRemoval = (true, QuestHostileDeathHoldMs, 0, QuestHostileDeathFxId, 1000);
            npc.Dispose();

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(QuestHostileRespawnMs);
                    RespawnQuestHostile(definition);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Quest hostile respawn failed for npc definition {id}.", definition.Id);
                }
            });
        }
    }

    // COMBAT: a non-fatal hit — make a world enemy fight back. If the player poked it from range (or before
    // it noticed them), lock it onto the attacker so it charges + auto-attacks instead of standing idle.
    public override void OnNpcDamaged(Player player, Npc npc)
    {
        if (npc is Sanctuary.Game.Entities.CombatNpc enemy)
            enemy.AggroOnto(player);
    }

    // COMBAT: Shadow Army generalized 2026-07-29 out of this zone into BaseZone.SummonCombatClones -
    // it was hardcoded here to chase one fixed training dummy in this zone only. Callers now build a
    // CombatCloneConfig (Combat namespace) and call zone.SummonCombatClones(...) directly; see
    // AbilityPacketClientRequestStartAbilityHandler for the Shadow Army / Nurse! dispatch.
    private void SendQuickChatData(Player player)
    {
        var quickChatSendDataPacket = new QuickChatSendDataPacket();

        quickChatSendDataPacket.QuickChats = _resourceManager.QuickChats.ToDictionary();

        player.SendTunneled(quickChatSendDataPacket);
    }

    private void SendPointOfInterests(Player player)
    {
        var packetPointOfInterestDefinitionReply = new PacketPointOfInterestDefinitionReply();
        using var writer = new PacketWriter();

        foreach (var pointOfInterest in _resourceManager.PointOfInterests.Values)
        {
            writer.Write(true);

            pointOfInterest.Serialize(writer);
        }

        writer.Write(false);

        packetPointOfInterestDefinitionReply.Payload = writer.Buffer;

        player.SendTunneled(packetPointOfInterestDefinitionReply);
    }

    private void SendReferenceData(Player player)
    {
        var referenceDataPacketItemClassDefinitions = new ReferenceDataPacketItemClassDefinitions();

        referenceDataPacketItemClassDefinitions.ItemClasses = _resourceManager.ItemClasses.ToDictionary();

        player.SendTunneled(referenceDataPacketItemClassDefinitions);

        var referenceDataPacketItemCategoryDefinitions = new ReferenceDataPacketItemCategoryDefinitions();

        referenceDataPacketItemCategoryDefinitions.ItemCategories = _resourceManager.ItemCategories.ToDictionary();
        referenceDataPacketItemCategoryDefinitions.ItemCategoryGroups = _resourceManager.ItemCategoryGroups.ToDictionary();

        player.SendTunneled(referenceDataPacketItemCategoryDefinitions);

        var referenceDataPacketClientProfileData = new ReferenceDataPacketClientProfileData();

        referenceDataPacketClientProfileData.Profiles = _resourceManager.Profiles.ToDictionary();

        player.SendTunneled(referenceDataPacketClientProfileData);
    }

    private void SendCoinStoreItemList(Player player)
    {
        var coinStoreItemListPacket = new CoinStoreItemListPacket();

        coinStoreItemListPacket.StaticItems = _resourceManager.CoinStoreItems.ToDictionary();

        player.SendTunneled(coinStoreItemListPacket);

        var clientItemDefinitions = new List<ClientItemDefinition>();

        foreach (var coinStoreItem in _resourceManager.CoinStoreItems)
        {
            if (!_resourceManager.ClientItemDefinitions.TryGetValue(coinStoreItem.Key, out var clientItemDefinition))
                continue;

            clientItemDefinitions.Add(clientItemDefinition);
        }

        using var writer = new PacketWriter();

        writer.Write(clientItemDefinitions);

        var playerUpdatePacketItemDefinitions = new PlayerUpdatePacketItemDefinitions();

        playerUpdatePacketItemDefinitions.Payload = writer.Buffer;

        player.SendTunneled(playerUpdatePacketItemDefinitions);
    }

    private void SendAdventurersJournalInfo(Player player)
    {
        // DO NOT REMOVE even if it's not fully implemented. This packet is needed
        // due to an Area Definition called "Newbiezone" in FabledRealmsAreas.xml.

        var adventurersJournal = new AdventurersJournalInfoPacket();

        AdventurersJournalRegionDefinition[] regions =
        [
            new()
            {
                Id = 1,
                NameId = 5100069,
                DescriptionId = 5100031,
                TabImageId = 35449,
                ChapterMapImageId = 0,
                GeometryId = 244,
                CompletedStringId = 5101408
            },
            new()
            {
                Id = 2,
                NameId = 442123,
                DescriptionId = 5100032,
                TabImageId = 9532,
                ChapterMapImageId = 0,
                GeometryId = 5,
                CompletedStringId = 442681,
            },
            new()
            {
                Id = 3,
                NameId = 3501,
                DescriptionId = 2129,
                TabImageId = 9538,
                ChapterMapImageId = 0,
                GeometryId = 8,
                CompletedStringId = 5101409,
            },
            new()
            {
                Id = 4,
                NameId = 3505,
                DescriptionId = 442685,
                TabImageId = 9529,
                ChapterMapImageId = 0,
                GeometryId = 1,
                CompletedStringId = 442686,
            }
        ];

        adventurersJournal.Regions = regions.ToDictionary(x => x.Id);

        AdventurersJournalHubDefinition[] hubs =
        [
            new()
            {
                Id = 1,
                RegionId = 1,
                DisplayOrder = 1,
                NameId = 442216,
                ActiveImageSetId = 19,
                ImageSetId = 44308,
                CompletedImageSetId = 44310,
                CompletedDescriptionId = 5100071,
                MapX = 0,
                MapY = 0
            },
            new()
            {
                Id = 2,
                RegionId = 1,
                DisplayOrder = 2,
                NameId = 18735,
                ActiveImageSetId = 19,
                ImageSetId = 44308,
                CompletedImageSetId = 44311,
                CompletedDescriptionId = 5100072,
                MapX = 0,
                MapY = 0
            },
            new()
            {
                Id = 3,
                RegionId = 1,
                DisplayOrder = 3,
                NameId = 5100069,
                ActiveImageSetId = 19,
                ImageSetId = 44308,
                CompletedImageSetId = 44309,
                CompletedDescriptionId = 5100073,
                MapX = 0,
                MapY = 0
            },
            new()
            {
                Id = 4,
                RegionId = 2,
                DisplayOrder = 1,
                NameId = 7262,
                ActiveImageSetId = 19,
                ImageSetId = 44308,
                CompletedImageSetId = 44941,
                CompletedDescriptionId = 442125,
                MapX = 0,
                MapY = 0
            },
            new()
            {
                Id = 5,
                RegionId = 2,
                DisplayOrder = 2,
                NameId = 428995,
                ActiveImageSetId = 19,
                ImageSetId = 44308,
                CompletedImageSetId = 44942,
                CompletedDescriptionId = 442126,
                MapX = 0,
                MapY = 0
            },
            new()
            {
                Id = 6,
                RegionId = 2,
                DisplayOrder = 3,
                NameId = 442124,
                ActiveImageSetId = 19,
                ImageSetId = 44308,
                CompletedImageSetId = 44945,
                CompletedDescriptionId = 442127,
                MapX = 0,
                MapY = 0
            },
            new()
            {
                Id = 7,
                RegionId = 2,
                DisplayOrder = 4,
                NameId = 4428,
                ActiveImageSetId = 19,
                ImageSetId = 44308,
                CompletedImageSetId = 44943,
                CompletedDescriptionId = 442128,
                MapX = 0,
                MapY = 0
            },
            new()
            {
                Id = 8,
                RegionId = 3,
                DisplayOrder = 1,
                NameId = 5101823,
                ActiveImageSetId = 19,
                ImageSetId = 44308,
                CompletedImageSetId = 45267,
                CompletedDescriptionId = 5101824,
                MapX = 0,
                MapY = 0
            },
            new()
            {
                Id = 9,
                RegionId = 3,
                DisplayOrder = 2,
                NameId = 5101825,
                ActiveImageSetId = 19,
                ImageSetId = 44308,
                CompletedImageSetId = 45268,
                CompletedDescriptionId = 5101826,
                MapX = 0,
                MapY = 0
            },
            new()
            {
                Id = 10,
                RegionId = 4,
                DisplayOrder = 1,
                NameId = 442623,
                ActiveImageSetId = 19,
                ImageSetId = 44308,
                CompletedImageSetId = 45600,
                CompletedDescriptionId = 442687,
                MapX = 0,
                MapY = 0
            }
        ];

        adventurersJournal.Hubs = hubs.ToDictionary(x => x.Id);

        AdventurersJournalHubQuestDefinition[] hubQuests =
        [
            new()
            {
                HubId = 1,
                Id = 2514,
                Unknown = 2
            },
            new()
            {
                HubId = 1,
                Id = 2513,
                Unknown = 1
            },
            new()
            {
                HubId = 2,
                Id = 2521,
                Unknown = 2
            },
            new()
            {
                HubId = 2,
                Id = 2526,
                Unknown = 7
            },
            new()
            {
                HubId = 2,
                Id = 2522,
                Unknown = 3
            },
            new()
            {
                HubId = 2,
                Id = 2523,
                Unknown = 4
            },
            new()
            {
                HubId = 2,
                Id = 2524,
                Unknown = 5
            },
            new()
            {
                HubId = 2,
                Id = 2525,
                Unknown = 6
            },
            new()
            {
                HubId = 3,
                Id = 2529,
                Unknown = 3
            },
            new()
            {
                HubId = 3,
                Id = 2528,
                Unknown = 2
            },
            new()
            {
                HubId = 3,
                Id = 2527,
                Unknown = 1
            },
            new()
            {
                HubId = 3,
                Id = 2566,
                Unknown = 5
            },
            new()
            {
                HubId = 3,
                Id = 2530,
                Unknown = 4
            },
            new()
            {
                HubId = 4,
                Id = 2493,
                Unknown = 6
            },
            new()
            {
                HubId = 4,
                Id = 2492,
                Unknown = 5
            },
            new()
            {
                HubId = 4,
                Id = 2491,
                Unknown = 4
            },
            new()
            {
                HubId = 4,
                Id = 2490,
                Unknown = 3
            },
            new()
            {
                HubId = 4,
                Id = 2489,
                Unknown = 2
            },
            new()
            {
                HubId = 4,
                Id = 2538,
                Unknown = 1
            },
            new()
            {
                HubId = 5,
                Id = 2498,
                Unknown = 6
            },
            new()
            {
                HubId = 5,
                Id = 2497,
                Unknown = 5
            },
            new()
            {
                HubId = 5,
                Id = 2496,
                Unknown = 4
            },
            new()
            {
                HubId = 5,
                Id = 2495,
                Unknown = 3
            },
            new()
            {
                HubId = 5,
                Id = 2494,
                Unknown = 2
            },
            new()
            {
                HubId = 5,
                Id = 2531,
                Unknown = 1
            },
            new()
            {
                HubId = 6,
                Id = 2502,
                Unknown = 4
            },
            new()
            {
                HubId = 6,
                Id = 2501,
                Unknown = 3
            },
            new()
            {
                HubId = 6,
                Id = 2500,
                Unknown = 2
            },
            new()
            {
                HubId = 6,
                Id = 2499,
                Unknown = 1
            },
            new()
            {
                HubId = 6,
                Id = 2503,
                Unknown = 5
            },
            new()
            {
                HubId = 7,
                Id = 2533,
                Unknown = 7
            },
            new()
            {
                HubId = 7,
                Id = 2532,
                Unknown = 1
            },
            new()
            {
                HubId = 7,
                Id = 2504,
                Unknown = 2
            },
            new()
            {
                HubId = 7,
                Id = 2508,
                Unknown = 6
            },
            new()
            {
                HubId = 7,
                Id = 2507,
                Unknown = 5
            },
            new()
            {
                HubId = 7,
                Id = 2505,
                Unknown = 3
            },
            new()
            {
                HubId = 7,
                Id = 2506,
                Unknown = 4
            },
            new()
            {
                HubId = 8,
                Id = 2580,
                Unknown = 5
            },
            new()
            {
                HubId = 8,
                Id = 2578,
                Unknown = 3
            },
            new()
            {
                HubId = 8,
                Id = 2579,
                Unknown = 4
            },
            new()
            {
                HubId = 8,
                Id = 2577,
                Unknown = 2
            },
            new()
            {
                HubId = 8,
                Id = 2576,
                Unknown = 1
            },
            new()
            {
                HubId = 9,
                Id = 2585,
                Unknown = 10
            },
            new()
            {
                HubId = 9,
                Id = 2584,
                Unknown = 9
            },
            new()
            {
                HubId = 9,
                Id = 2583,
                Unknown = 8
            },
            new()
            {
                HubId = 9,
                Id = 2582,
                Unknown = 7
            },
            new()
            {
                HubId = 9,
                Id = 2581,
                Unknown = 6
            },
            new()
            {
                HubId = 9,
                Id = 2600,
                Unknown = 11
            },
            new()
            {
                HubId = 10,
                Id = 2595,
                Unknown = 6
            },
            new()
            {
                HubId = 10,
                Id = 2594,
                Unknown = 5
            },
            new()
            {
                HubId = 10,
                Id = 2591,
                Unknown = 4
            },
            new()
            {
                HubId = 10,
                Id = 2590,
                Unknown = 3
            },
            new()
            {
                HubId = 10,
                Id = 2596,
                Unknown = 7
            },
            new()
            {
                HubId = 10,
                Id = 2588,
                Unknown = 1
            },
            new()
            {
                HubId = 10,
                Id = 2599,
                Unknown = 10
            },
            new()
            {
                HubId = 10,
                Id = 2598,
                Unknown = 9
            },
            new()
            {
                HubId = 10,
                Id = 2597,
                Unknown = 8
            },
            new()
            {
                HubId = 10,
                Id = 2589,
                Unknown = 2
            }
        ];

        adventurersJournal.HubQuests = hubQuests.ToDictionary(x => x.Id);

        AdventurersJournalStickerDefinition[] stickers =
        [
            new()
            {
                Id = 1,
                RegionId = 1,
                DisplayOrder = 1,
                QuestId = 2563,
                NameId = 5100479,
                DescriptionId = 5100480,
                CompletedImageSetId = 43279,
                ImageSetId = 43278,
                Unknown = 0
            },
            new()
            {
                Id = 2,
                RegionId = 1,
                DisplayOrder = 2,
                QuestId = 2564,
                NameId = 5100483,
                DescriptionId = 5100484,
                CompletedImageSetId = 43287,
                ImageSetId = 43286,
                Unknown = 0
            },
            new()
            {
                Id = 3,
                RegionId = 1,
                DisplayOrder = 3,
                QuestId = 2565,
                NameId = 5100487,
                DescriptionId = 5100488,
                CompletedImageSetId = 43273,
                ImageSetId = 43272,
                Unknown = 0
            },
            new()
            {
                Id = 4,
                RegionId = 1,
                DisplayOrder = 4,
                QuestId = 2572,
                NameId = 5100772,
                DescriptionId = 5100773,
                CompletedImageSetId = 43281,
                ImageSetId = 43280,
                Unknown = 0
            },
            new()
            {
                Id = 5,
                RegionId = 1,
                DisplayOrder = 5,
                QuestId = 2573,
                NameId = 5100776,
                DescriptionId = 5100777,
                CompletedImageSetId = 43291,
                ImageSetId = 43290,
                Unknown = 0
            },
            new()
            {
                Id = 6,
                RegionId = 1,
                DisplayOrder = 6,
                QuestId = 2587,
                NameId = 5101187,
                DescriptionId = 5101188,
                CompletedImageSetId = 43283,
                ImageSetId = 43282,
                Unknown = 0
            },
            new()
            {
                Id = 16,
                RegionId = 2,
                DisplayOrder = 1,
                QuestId = 2568,
                NameId = 5100756,
                DescriptionId = 5100757,
                CompletedImageSetId = 43305,
                ImageSetId = 43304,
                Unknown = 0
            },
            new()
            {
                Id = 17,
                RegionId = 2,
                DisplayOrder = 2,
                QuestId = 2569,
                NameId = 5100760,
                DescriptionId = 5100761,
                CompletedImageSetId = 43287,
                ImageSetId = 43286,
                Unknown = 0
            },
            new()
            {
                Id = 18,
                RegionId = 2,
                DisplayOrder = 3,
                QuestId = 2570,
                NameId = 5100764,
                DescriptionId = 5100765,
                CompletedImageSetId = 43273,
                ImageSetId = 43272,
                Unknown = 0
            },
            new()
            {
                Id = 19,
                RegionId = 2,
                DisplayOrder = 4,
                QuestId = 2571,
                NameId = 5100768,
                DescriptionId = 5100769,
                CompletedImageSetId = 43279,
                ImageSetId = 43278,
                Unknown = 0
            },
            new()
            {
                Id = 20,
                RegionId = 2,
                DisplayOrder = 5,
                QuestId = 2574,
                NameId = 5100780,
                DescriptionId = 5100781,
                CompletedImageSetId = 43277,
                ImageSetId = 43276,
                Unknown = 0
            },
            new()
            {
                Id = 21,
                RegionId = 2,
                DisplayOrder = 6,
                QuestId = 2575,
                NameId = 5100784,
                DescriptionId = 5100785,
                CompletedImageSetId = 43283,
                ImageSetId = 43282,
                Unknown = 0
            },
            new()
            {
                Id = 32,
                RegionId = 3,
                DisplayOrder = 2,
                QuestId = 2602,
                NameId = 442851,
                DescriptionId = 442857,
                CompletedImageSetId = 43287,
                ImageSetId = 43286,
                Unknown = 0
            },
            new()
            {
                Id = 35,
                RegionId = 3,
                DisplayOrder = 5,
                QuestId = 2605,
                NameId = 442854,
                DescriptionId = 442860,
                CompletedImageSetId = 43279,
                ImageSetId = 43278,
                Unknown = 0
            },
            new()
            {
                Id = 36,
                RegionId = 3,
                DisplayOrder = 6,
                QuestId = 2606,
                NameId = 442855,
                DescriptionId = 442861,
                CompletedImageSetId = 43305,
                ImageSetId = 43304,
                Unknown = 0
            },
            new()
            {
                Id = 37,
                RegionId = 4,
                DisplayOrder = 1,
                QuestId = 2592,
                NameId = 0,
                DescriptionId = 0,
                CompletedImageSetId = 0,
                ImageSetId = 0,
                Unknown = 0
            }
            // NOTE: journal stickers are a CURATED retail set (only certain milestone/story quests earn
            // one, each with dedicated sticker art) - NOT every quest. Our custom quests deliberately have
            // no stickers here; they're still marked complete via the op209/2 QuestUpdate map, which earns
            // the stickers that DO exist (e.g. Introduce Yourself 2563 / Call the Crew 2564 above).
        ];

        adventurersJournal.Stickers = stickers.ToDictionary(x => x.Id);

        player.SendTunneled(adventurersJournal);
    }

    private void SendWelcomeInfo(Player player)
    {
        var packetLoadWelcomeScreen = new PacketLoadWelcomeScreen();

        packetLoadWelcomeScreen.Contents.AddRange(
        [
            new ContentInfo
            {
                NameId = 6185,
                DescriptionId = 6186,
            },
            new ContentInfo
            {
                NameId = 6187,
                DescriptionId = 6188,
            },
            new ContentInfo
            {
                NameId = 6189,
                DescriptionId = 6190,
            }
        ]);

        packetLoadWelcomeScreen.ClaimCodes.AddRange(
        [
            new ClaimCodeInfo
            {
                Code = "MMMDONUT",
                NameId = 401519,
                DescriptionId = 401534,
                IconId = 929
            },
            new ClaimCodeInfo
            {
                Code = "BERRYCUPCAKE",
                NameId = 401517,
                DescriptionId = 401532,
                IconId = 939
            },
            new ClaimCodeInfo
            {
                Code = "SKELETAL",
                NameId = 409157,
                DescriptionId = 109132,
                IconId = 3459
            },
            new ClaimCodeInfo
            {
                Code = "STRAWBERRIES",
                NameId = 409158,
                DescriptionId = 108948,
                IconId = 3441
            },
            new ClaimCodeInfo
            {
                Code = "FROGGY",
                NameId = 409159,
                DescriptionId = 3141,
                IconId = 1258
            },
            new ClaimCodeInfo
            {
                Code = "SANDWICH",
                NameId = 409160,
                DescriptionId = 2430,
                IconId = 949
            },
            new ClaimCodeInfo
            {
                Code = "BOSSCAKE",
                NameId = 30109,
                DescriptionId = 30118,
                IconId = 6380
            }
        ]);

        player.SendTunneled(packetLoadWelcomeScreen);

        SendWelcomeAnnouncements(player);
    }

    // The "What's New" tiles on the welcome screen - this is how the wheel gets a tile there.
    public void SendWelcomeAnnouncements(Player player)
    {
        var announcements = new AnnouncementDataSendPacket();

        announcements.Announcements.Add(new AnnouncementInfo
        {
            Id = 1,
            Priority = 1,

            // A raw IMAGE_ID from Images.txt, not an image-set id - the two spaces overlap and neither
            // errors, so a set id here quietly draws the wrong picture.
            IconId = WelcomeWheelIconId,

            TitleStringId = 409962,     // "Spin For The Win!"
            BodyStringId = 409969,      // "Welcome to the Free Realms Daily Rewards wheel! ..."
            ButtonStringId = 433041,    // "PLAY NOW"

            LuaCall = "Minigame",
            Param1 = WheelCategoryId
        });

        player.SendTunneled(announcements);
    }

    // Param1 for "Minigame" is a CATEGORY id, not an activity id - welcome.lua hands it to
    // MinigameDetail:Populate, which branches on it to build the wheel's panel. Matches the Category the
    // client's own ActivityCategories.txt gives activity 8.
    public const int WheelCategoryId = 17;

    // What the client loads BEHIND the wheel minigame. Empty = nothing, so it floats over the game world.
    // "/wheel bg <name>" puts a movie back (e.g. game_wheel.gfx, the old behaviour) to compare.
    public static string WheelBackgroundSwf = "";

    // MiniGameInfo flags for the wheel launch, toggled live with "/wheel flag <name> <0|1>" while hunting
    // for whichever one drops the start screen and the framed minigame window.
    public static bool WheelUnknown11 = true;
    public static bool WheelShowStarCounter;
    public static bool WheelShowStatusIcon;
    public static bool WheelShowActionBar;
    public static bool WheelShowEndDialog;

    // icon_ui_wheelstart_panelgraphic.dds - the icon the client's own MiniGameData.txt gives the wheel.
    // "/wheel welcome <id>" swaps it live.
    public static int WelcomeWheelIconId = 20985;

    public override void RefreshPlayerCustomizations(Player player)
    {
        SendPlayerCustomizations(player);
        player.SendTunneled(new ClientUpdatePacketDoneSendingPreloadCharacters());
    }

    private void SendPlayerCustomizations(Player player)
    {
        var playerUpdatePacketCustomizationData = new PlayerUpdatePacketCustomizationData();

        var customizations = new[]
        {
            new PlayerCustomizationData
            {
                Id = 0, // Head
                Param = player.HeadId,
                StringParam = player.Head
            },
            new PlayerCustomizationData
            {
                Id = 1, // Skin Tone
                Param = player.SkinToneId,
                StringParam = player.SkinTone
            },
            new PlayerCustomizationData
            {
                Id = 2, // Hair
                Param = player.HairId,
                StringParam = player.Hair
            },
            new PlayerCustomizationData
            {
                Id = 3, // Hair Color
                Param = player.HairColor
            },
            new PlayerCustomizationData
            {
                Id = 4, // Eye Color
                Param = player.EyeColor
            },
            new PlayerCustomizationData
            {
                Id = 5, // Model Customization
                Param = player.ModelCustomizationId,
                StringParam = player.ModelCustomization
            },
            new PlayerCustomizationData
            {
                Id = 6, // Face Paint
                Param = player.FacePaintId,
                StringParam = player.FacePaint
            },
            new PlayerCustomizationData
            {
                Id = 8, // Model — use TemporaryAppearance when a transform is active
                Param = player.TemporaryAppearance != 0 ? player.TemporaryAppearance : player.Model
            }
        };

        playerUpdatePacketCustomizationData.Customizations.AddRange(customizations);

        player.SendTunneled(playerUpdatePacketCustomizationData);
    }

    private void SendMembershipSubscriptionInfo(Player player)
    {
        var packetMembershipSubscriptionInfo = new PacketMembershipSubscriptionInfo
        {
            IsMember = player.MembershipStatus != 0
        };

        player.SendTunneled(packetMembershipSubscriptionInfo);
    }

    private void SendListOfActivities(Player player)
    {
        /* var activityProfileListPacket = new ActivityProfileListPacket
        {
            Activities = new Dictionary<int, ActivityForProfileType>()
            {
                {
                    // Fisherman
                    137, new ActivityForProfileType
                    {
                        ProfileId = 137,
                        QuestId = 1968,
                        IconId = 20740,
                        BadgeId = 4843,
                        QuestTitle = 412490,
                        QuestDescription = 412491,
                    }
                },
                {
                    // Soccer Star
                    52, new ActivityForProfileType
                    {
                        ProfileId = 52,
                        QuestId = 1965,
                        IconId = 20743,
                        BadgeId = 4842,
                        QuestTitle = 412463,
                        QuestDescription = 412464
                    }
                },
                {
                    // Demo Derby Driver
                    49, new ActivityForProfileType
                    {
                        ProfileId = 49,
                        QuestId = 1960,
                        IconId = 8059,
                        BadgeId = 46,
                        QuestTitle = 412342,
                        QuestDescription = 412343
                    }
                },
                {
                    // Kart Driver
                    48, new ActivityForProfileType
                    {
                        ProfileId = 48,
                        QuestId = 1961,
                        IconId = 20725,
                        BadgeId = 46,
                        QuestTitle = 407752,
                        QuestDescription = 412379
                    }
                },
                {
                    // Chef
                    45, new ActivityForProfileType
                    {
                        ProfileId = 45,
                        QuestId = 1978,
                        IconId = 156,
                        BadgeId = 11,
                        QuestTitle = 413021,
                        QuestDescription = 413022
                    }
                },
                {
                    // Archer
                    35, new ActivityForProfileType
                    {
                        ProfileId = 35,
                        QuestId = 1952,
                        IconId = 1335,
                        BadgeId = 32,
                        QuestTitle = 412187,
                        QuestDescription = 412188
                    }
                },
                {
                    // Warrior
                    32, new ActivityForProfileType
                    {
                        ProfileId = 32,
                        QuestId = 1966,
                        IconId = 21594,
                        BadgeId = 10,
                        QuestTitle = 412471,
                        QuestDescription = 412472
                    }
                },
                {
                    // Miner
                    14, new ActivityForProfileType
                    {
                        ProfileId = 14,
                        QuestId = 1979,
                        IconId = 1341,
                        BadgeId = 11,
                        QuestTitle = 139748,
                        QuestDescription = 413026
                    }
                },
                {
                    // Wizard
                    12, new ActivityForProfileType
                    {
                        ProfileId = 12,
                        QuestId = 1967,
                        IconId = 1343,
                        BadgeId = 12,
                        QuestTitle = 412481,
                        QuestDescription = 412482
                    }
                },
                {
                    // Medic
                    11, new ActivityForProfileType
                    {
                        ProfileId = 11,
                        QuestId = 1962,
                        IconId = 1340,
                        BadgeId = 13,
                        QuestTitle = 412422,
                        QuestDescription = 412423
                    }
                },
                {
                    // Postman
                    4, new ActivityForProfileType
                    {
                        ProfileId = 4,
                        QuestId = 1964,
                        IconId = 1339,
                        BadgeId = 11,
                        QuestTitle = 412445,
                        QuestDescription = 412446
                    }
                },
                {
                    // Ninja
                    2, new ActivityForProfileType
                    {
                        ProfileId = 2,
                        QuestId = 1963,
                        IconId = 1342,
                        BadgeId = 10,
                        QuestTitle = 412437,
                        QuestDescription = 412438
                    }
                },
                {
                    // Brawler
                    43, new ActivityForProfileType
                    {
                        ProfileId = 43,
                        QuestId = 1593,
                        IconId = 1337,
                        BadgeId = 10,
                        QuestTitle = 388503,
                        QuestDescription = 388504
                    }
                },
                {
                    // Card Duelist
                    120, new ActivityForProfileType
                    {
                        ProfileId = 120,
                        QuestId = 1304,
                        IconId = 396,
                        BadgeId = 1783,
                        QuestTitle = 103744,
                        QuestDescription = 103745
                    }
                },
                {
                    // Blacksmith
                    16, new ActivityForProfileType
                    {
                        ProfileId = 16,
                        QuestId = 1019,
                        IconId = 1336,
                        BadgeId = 11,
                        QuestTitle = 90071,
                        QuestDescription = 90072
                    }
                }
            }
        };

        player.SendTunneled(activityProfileListPacket); */

        var clientActivities = _resourceManager.ClientActivityDefinitions.Values.Where(x => x.ServerType == 2).ToList();

        var activityPacketListOfActivities = new ActivityPacketListOfActivities
        {
            ServerType = 2,
            Activities = clientActivities
        };

        player.SendTunneled(activityPacketListOfActivities);

        var clientWorldActivities = _resourceManager.ClientActivityDefinitions.Values.Where(x => x.ServerType == 1).ToList();

        activityPacketListOfActivities.ServerType = 1;
        activityPacketListOfActivities.Activities = clientWorldActivities;

        player.SendTunneled(activityPacketListOfActivities);
    }

    private void SendInGamePurchase(Player player)
    {
        var packetInGamePurchaseEnableMarketplace = new PacketInGamePurchaseEnableMarketplace
        {
            Enabled = true
        };

        player.SendTunneled(packetInGamePurchaseEnableMarketplace);

        var packetInGamePurchaseStoreEnablePaymentSources = new PacketInGamePurchaseStoreEnablePaymentSources
        {
            Sms = true,
            Paypal = true
        };

        player.SendTunneled(packetInGamePurchaseStoreEnablePaymentSources);

        var packetInGamePurchaseStoreBundleCategoryGroups = new PacketInGamePurchaseStoreBundleCategoryGroups();

        packetInGamePurchaseStoreBundleCategoryGroups.CategoryGroups = _resourceManager.StoreBundleCategoryGroups.ToDictionary();

        player.SendTunneled(packetInGamePurchaseStoreBundleCategoryGroups);

        var packetInGamePurchaseStoreBundleCategories = new PacketInGamePurchaseStoreBundleCategories();

        packetInGamePurchaseStoreBundleCategories.CategoryTree.Categories = _resourceManager.StoreBundleCategories.ToDictionary();

        player.SendTunneled(packetInGamePurchaseStoreBundleCategories);

        if (_resourceManager.Stores.TryGetValue(1, out var mainStore))
        {
            var packetInGamePurchaseStoreBundles = new PacketInGamePurchaseStoreBundles();

            packetInGamePurchaseStoreBundles.StoreId = mainStore.Id;

            packetInGamePurchaseStoreBundles.Store.Id = mainStore.Id;
            packetInGamePurchaseStoreBundles.Store.NameId = mainStore.NameId;
            packetInGamePurchaseStoreBundles.Store.DescriptionId = mainStore.DescriptionId;
            packetInGamePurchaseStoreBundles.Store.Image = mainStore.Image;

            foreach (var storeBundle in mainStore.Bundles.Values)
            {
                var containsHouse = storeBundle.Entries.Any(entry =>
                    (_resourceManager.ClientItemDefinitions.TryGetValue(entry.MarketingItemId, out var marketingDefinition) &&
                        marketingDefinition.Type == 16) ||
                    (_resourceManager.ClientItemDefinitions.TryGetValue(entry.GameItemId, out var gameDefinition) &&
                        gameDefinition.Type == 16));
                var valid = storeBundle.Entries.All(entry =>
                    _resourceManager.ClientItemDefinitions.ContainsKey(entry.MarketingItemId) ||
                    (containsHouse && _resourceManager.ClientItemDefinitions.ContainsKey(entry.GameItemId)));

                if (valid)
                    packetInGamePurchaseStoreBundles.Store.Bundles.Add(storeBundle.Id, storeBundle);
            }

            player.SendTunneled(packetInGamePurchaseStoreBundles);
        }

        var packetInGamePurchaseStoreBundleGroups = new PacketInGamePurchaseStoreBundleGroups();

        packetInGamePurchaseStoreBundleGroups.BundleGroups = _resourceManager.StoreBundleGroups.ToDictionary();

        player.SendTunneled(packetInGamePurchaseStoreBundleGroups);

        // Send empty claim list so the Claim window doesn't get stuck on "Processing..."
        player.SendTunneled(new PromotionalBundleDataPacket());

        /* var inGamePurchaseUpdateSaleDisplay = new InGamePurchaseUpdateSaleDisplay();

        inGamePurchaseUpdateSaleDisplay.Sales.Add(new SaleDisplayInfo
        {
            Id = 12951,
            IconId = 7866,
            TintId = 0,
            TitleId = 824,
            BodyId = 825,
            SecondsLeft = 1000,
            Unknown = 0,
            IsMembership = false
        });

        player.SendTunneled(inGamePurchaseUpdateSaleDisplay); */
    }

    private void SendFriendList(Player player)
    {
        var friendListPacket = new FriendListPacket();

        friendListPacket.Friends = player.Friends;

        player.SendTunneled(friendListPacket);
    }

    private void SendIgnoreList(Player player)
    {
        var ignoreListPacket = new IgnoreListPacket();

        ignoreListPacket.Ignores = player.Ignores;

        player.SendTunneled(ignoreListPacket);
    }

    private void SendPetList(Player player)
    {
        var petListPacket = new PetListPacket
        {
            Pets = player.Pets
        };

        player.SendTunneled(petListPacket);
    }

    private void UpdateFriendStatus(Player player)
    {
        var friendOnlinePacket = new FriendOnlinePacket();

        friendOnlinePacket.Guid = player.Guid;

        friendOnlinePacket.IsLocal = true;

        var friendStatusPacket = new FriendStatusPacket
        {
            Guid = player.Guid,
            Status =
            {
                ProfileId = player.ActiveProfile.Id,
                ProfileRank = player.ActiveProfile.Rank,
                ProfileIconId = player.ActiveProfile.Icon,
                ProfileNameId = player.ActiveProfile.NameId,
                ProfileBackgroundImageId = player.ActiveProfile.BadgeImageSet
            }
        };

        foreach (var friend in player.Friends)
        {
            if (!_zoneManager.TryGetPlayer(friend.Guid, out var friendPlayer))
                continue;

            var otherFriendPlayer = friendPlayer.Friends.FirstOrDefault(x => x.Guid == player.Guid);

            if (otherFriendPlayer is null || otherFriendPlayer.Online)
                continue;

            otherFriendPlayer.Online = true;

            friendPlayer.SendTunneled(friendOnlinePacket);
            friendPlayer.SendTunneled(friendStatusPacket);
        }
    }

    #endregion

    public int GetZoneAreaId(Vector4 position)
    {
        foreach (var areaDefinition in _zoneDefinition.AreaDefinitions)
        {
            if (areaDefinition.Shape == "Circle")
            {
                var circle = new Vector3(areaDefinition.X1, 0, areaDefinition.Z1);

                if (position.IsInCircle(circle, areaDefinition.Radius))
                    return areaDefinition.Id;
            }
            else if (areaDefinition.Shape == "Rectangle")
            {
                var p1 = new Vector3(areaDefinition.X1, 0, areaDefinition.Z1);
                var p2 = new Vector3(areaDefinition.X2, 0, areaDefinition.Z2);

                if (position.IsInRectangle(p1, p2))
                    return areaDefinition.Id;
            }
            else
            {
                throw new NotImplementedException(nameof(areaDefinition.Shape));
            }
        }

        return 0;
    }

    public override int GetClaimCodeItemCount(string code, int itemId)
    {
        if (string.Equals(code, "BOSSCAKE", StringComparison.OrdinalIgnoreCase) && itemId == 69828)
            return 3;
        return base.GetClaimCodeItemCount(code, itemId);
    }

    public override List<ClaimCodeInfo> GetClaimCodes()
    {
        return
        [
            new ClaimCodeInfo
            {
                Code = "MMMDONUT",
                NameId = 401519,
                DescriptionId = 401534,
                IconId = 929
            },
            new ClaimCodeInfo
            {
                Code = "BERRYCUPCAKE",
                NameId = 401517,
                DescriptionId = 401532,
                IconId = 939
            },
            new ClaimCodeInfo
            {
                Code = "SKELETAL",
                NameId = 409157,
                DescriptionId = 109132,
                IconId = 3459
            },
            new ClaimCodeInfo
            {
                Code = "STRAWBERRIES",
                NameId = 409158,
                DescriptionId = 108948,
                IconId = 3441
            },
            new ClaimCodeInfo
            {
                Code = "FROGGY",
                NameId = 409159,
                DescriptionId = 3141,
                IconId = 1258
            },
            new ClaimCodeInfo
            {
                Code = "SANDWICH",
                NameId = 409160,
                DescriptionId = 2430,
                IconId = 949
            },
            new ClaimCodeInfo
            {
                Code = "BOSSCAKE",
                NameId = 30109,
                DescriptionId = 30118,
                IconId = 6380
            }
        ];
    }
}
