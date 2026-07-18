using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Sanctuary.Core.Extensions;
using Sanctuary.Core.IO;
using Sanctuary.Game.Combat;
using Sanctuary.Game.Entities;
using Sanctuary.Game.Resources.Definitions;
using Sanctuary.Game.Resources.Definitions.Zones;
using Sanctuary.Packet;
using Sanctuary.Packet.Common;

namespace Sanctuary.Game.Zones;

public sealed class StartingZone : BaseZone
{
    private readonly IZoneManager _zoneManager;
    private readonly IResourceManager _resourceManager;
    private readonly StartingZoneDefinition _zoneDefinition;
    private readonly Sanctuary.Game.Quests.IQuestManager _questManager;
    private readonly Sanctuary.Game.Party.IPartyManager _partyManager;

    public StartingZone(StartingZoneDefinition zoneDefinition, IServiceProvider serviceProvider)
        : base(zoneDefinition, serviceProvider)
    {
        _zoneDefinition = zoneDefinition;

        _zoneManager = serviceProvider.GetRequiredService<IZoneManager>();
        _resourceManager = serviceProvider.GetRequiredService<IResourceManager>();
        _questManager = serviceProvider.GetRequiredService<Sanctuary.Game.Quests.IQuestManager>();
        _partyManager = serviceProvider.GetRequiredService<Sanctuary.Game.Party.IPartyManager>();

        // Spawn all static NPCs in the zone
        SpawnNpcs();

        // Place a clickable entrance at each atlas dungeon marker (notif=3 POI) — click -> start panel -> GO!.
        SpawnDungeonEntrances();

        // Place a wandering "Battle Starter" creature for each small combat encounter, among its own kind.
        SpawnEncounterEntryNpcs();
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

    private void SpawnNpcs()
    {
        int spawnedCount = 0;

        foreach (var definition in _resourceManager.Npcs.Values)
        {
            var guid = NpcGuidBase + (ulong)definition.Id;

            // WORLD COMBAT: curated enemy creatures (model matches the dungeon enemy set) spawn as hostile
            // CombatNpcs — they aggro on approach, chase, auto-attack the player, track HP, die, and respawn.
            // Excluded when the same model is doubling as a vendor, quest giver/target, or a quest kill-target,
            // which keep their existing interactive/quest paths (kill-targets get MakeQuestHostile below).
            if (IsWorldEnemyDefinition(definition))
            {
                SpawnWorldEnemy(definition);
                spawnedCount++;
                continue;
            }

            // INSTANCE (Tormented Spirits!): exactly ONE wandering spirit is the dungeon entrance (click ->
            // offer popup); every OTHER graveyard spirit spawns as a hostile world enemy you can fight.
            if (definition.NameId == TormentedSpiritsArenaZone.EntryNpcNameId)
            {
                if (_spiritEntranceGuid != 0)
                {
                    SpawnWorldEnemy(definition);
                    spawnedCount++;
                    continue;
                }

                _spiritEntranceGuid = guid; // the first one becomes the single entrance (configured below)
            }

            if (!TryCreateNpc(guid, out var npc))
                continue;

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
                npc.InteractAction = (interactingPlayer) =>
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
            }

            // Quest givers/targets (from Quests.json) route their interaction through the quest manager,
            // which decides whether to offer a quest or advance/turn one in based on the player's state.
            if (_questManager.IsQuestNpc(guid))
            {
                npc.CursorId = 17;
                var questNpc = npc;
                npc.InteractAction = interactingPlayer => _questManager.OnNpcInteract(interactingPlayer, questNpc);
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

            npc.UpdatePosition(definition.Position, definition.Rotation);

            var tile = GetTileFromPosition(definition.Position);
            tile.Entities.TryAdd(npc.Guid, npc);

            spawnedCount++;
        }

        SpawnQuestCollectibles();
    }

    // Collect-goal pickups (Quests.json goals of Type=Collect): interactable world objects the player clicks
    // to gather. Shared across players; per-player credit + hide are handled in QuestManager.OnCollectInteract.
    private void SpawnQuestCollectibles()
    {
        foreach (var collectible in _resourceManager.Quests.CollectibleSpawns)
        {
            if (!TryCreateNpc(collectible.Guid, out var npc))
                continue;

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

            npc.UpdatePosition(collectible.Position, System.Numerics.Quaternion.Identity);
            GetTileFromPosition(collectible.Position).Entities.TryAdd(npc.Guid, npc);
        }
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
            return; // the active job has no weapon-ability kit

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
        var guid = NpcGuidBase + (ulong)definition.Id;
        return !_resourceManager.NpcVendors.ContainsKey(guid)
            && !_questManager.IsQuestNpc(guid)
            && !_resourceManager.Quests.KillTargetNameIds.Contains(definition.NameId)
            && Sanctuary.Game.Dungeons.DungeonCatalog.EnemyModelIds.Contains(definition.ModelId);
    }

    private void SpawnWorldEnemy(NpcDefinition definition)
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

        enemy.SpawnPosition = definition.Position;
        enemy.SpawnRotation = definition.Rotation;
        enemy.LastSentPosition = definition.Position;
        enemy.UpdatePosition(definition.Position, definition.Rotation);

        var tile = GetTileFromPosition(definition.Position);
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

    private void SpawnDungeonEntrances()
    {
        foreach (var poi in _resourceManager.PointOfInterests.Values)
        {
            if (poi.NotificationType != 3)
                continue;
            if (!Sanctuary.Game.Dungeons.DungeonCatalog.ByAtlasPoi.TryGetValue(poi.Id, out var dungeon))
                continue;
            if (!TryCreateNpc(out var entrance))
                continue;

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

            var pos = poi.SpawnPosition != default ? poi.SpawnPosition : poi.Position;
            var rot = new Quaternion(MathF.Sin(poi.Heading), 0f, MathF.Cos(poi.Heading), 0f);
            var capturedDungeon = dungeon;
            entrance.InteractAction = player => SendDungeonOffer(player, capturedDungeon);

            entrance.UpdatePosition(pos, rot);
            GetTileFromPosition(pos).Entities.TryAdd(entrance.Guid, entrance);
        }
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

        player.SendTunneled(new EncounterDetailsResponsePacket
        {
            Unknown = dungeon.ActivityId,
            Unknown2 = instanceId,
            NameId = dungeon.TitleNameId,
            DescriptionId = dungeon.DescriptionId,
            Difficulty = dungeon.Difficulty,
            IconId = dungeon.IconId,
            MiniGameType = 4, // COMBAT
            PreviewRewards = FrostfangArenaZone.GetPrizePreviewFor(player),
            PreviewCoins = FrostfangArenaZone.PrizeCoins,
            PreviewXp = FrostfangArenaZone.PrizeXp,
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

    // COMBAT WIP: Shadow Army special — spawn temporary "shadow clone" NPCs around the caster, each using the
    // caster's own model, wearing a shadow aura, appearing/vanishing in a puff of black ninja smoke, then
    // despawning after a few seconds. (Customization/outfit copy is a client TODO, so clones are the base body
    // + the shadow aura for now.) FX ids from ActorCompositeEffectDefinitions.xml.
    private const int ShadowCloneModelId = 945;    // human_m_ninja_ghost.adr (Models.txt) — a clothed, ghostly shadow ninja
    private const int ShadowCloneSmokePoof = 21;   // PFX_smoke_black_explosion (ninja appear/vanish poof)
    // Clone AI: run to the enemy, then swing at it on a cooldown (the clones "help you fight").
    private const int CloneTickMs = 300;           // movement/AI tick (client interpolates between updates)
    private const int CloneAttackCooldownMs = 1400;
    private const int CloneAttackAnimation = 1021; // com_1hs_attack_01 — sword swing
    private const int CloneAttackDamage = 200;
    private const int CloneHitFx = 15999;          // PFX_ninja-shadowblade_impact (shadow-blade hit on target)
    private const float CloneMoveSpeed = 9f;       // units/sec toward the target
    private const float CloneAttackRange = 2.5f;   // stop & swing within this distance
    private const int CloneRunAnim = 3;            // loc_run · walk=2 · stand=1 (AnimationGroups.xml)

    public void SummonShadowClones(Player summoner, int count, int lifetimeSeconds)
    {
        // small arc around the caster
        (float dx, float dz)[] offsets = [(-2f, -2f), (2f, -2f), (0f, -3f), (-3f, 1f), (3f, 1f)];

        var clones = new List<Npc>(count);

        for (var i = 0; i < count; i++)
        {
            if (!TryCreateNpc(out var clone))
                break;

            var (dx, dz) = offsets[i % offsets.Length];
            var pos = new Vector4(summoner.Position.X + dx, summoner.Position.Y, summoner.Position.Z + dz, summoner.Position.W);

            clone.ModelId = ShadowCloneModelId; // clothed ghostly shadow ninja (fixes "naked" base body)
            clone.Name = "Shadow Ninja";        // nameplate text (matches the real ability)
            clone.NameId = 0;
            clone.HideNamePlate = false;        // show the "Shadow Ninja" nameplate
            clone.Disposition = 2;              // Ally (your shadow ninjas)
            clone.Scale = 1f;
            clone.IsInteractable = false;
            clone.CursorId = 0;
            clone.CompositeEffectId = 0;        // ghost model is already shadowy; NO persistent (_loop) aura -> nothing lingers
            clone.RunAnimId = CloneRunAnim;     // play the run clip while moving to the enemy
            clone.WalkAnimId = 2;               // loc_walk
            clone.StandAnimId = 1;              // loc_stand
            clone.Visible = true;
            clone.UpdatePosition(pos, summoner.Rotation);

            summoner.OnAddVisibleNpcs(clone);   // make it appear for the caster
            clone.OnAddVisiblePlayers(summoner); // track the caster so Dispose() removes it from their client

            // ninja smoke poof at the spawn spot
            summoner.SendTunneled(new PlayerUpdatePacketPlayCompositeEffect
            {
                Guid = clone.Guid,
                CompositeEffectId = ShadowCloneSmokePoof,
                Position = pos,
            });

            clones.Add(clone);
        }

        if (clones.Count == 0)
            return;

        _logger.LogInformation("Shadow Army: summoned {n} clones for {sec}s (model {model}).",
            clones.Count, lifetimeSeconds, summoner.Model);

        // despawn after the lifetime (off-thread, mirrors the damage-resolve pattern)
        _ = Task.Run(async () =>
        {
            try
            {
                // CLONE AI: run to the dummy (re-targeting its position each tick = chase), then swing on a
                // cooldown once in range. Position updates each tick; the client interpolates -> smooth run.
                var totalMs = lifetimeSeconds * 1000;
                var nextAttackMs = new int[clones.Count]; // per-clone next-attack time (ms since start)

                for (var elapsed = 0; elapsed < totalMs; elapsed += CloneTickMs)
                {
                    await Task.Delay(CloneTickMs);

                    var dummy = _trainingDummy;
                    if (dummy is null)
                        continue;

                    var target = new Vector3(dummy.Position.X, dummy.Position.Y, dummy.Position.Z);

                    for (var i = 0; i < clones.Count; i++)
                    {
                        var clone = clones[i];
                        var here = new Vector3(clone.Position.X, clone.Position.Y, clone.Position.Z);
                        var toTarget = target - here;
                        var dist = toTarget.Length();

                        // face the dummy (yaw about Y)
                        var yaw = (float)Math.Atan2(toTarget.X, toTarget.Z);
                        var rot = Quaternion.CreateFromYawPitchRoll(yaw, 0f, 0f);

                        if (dist > CloneAttackRange)
                        {
                            // step toward the dummy
                            var step = Math.Min(CloneMoveSpeed * (CloneTickMs / 1000f), dist - CloneAttackRange);
                            var dir = toTarget / dist;
                            var np = here + dir * step;
                            var newPos = new Vector4(np.X, np.Y, np.Z, clone.Position.W);

                            clone.UpdatePosition(newPos, rot);
                            summoner.SendTunneled(new PlayerUpdatePacketUpdatePosition
                            {
                                Guid = clone.Guid, Position = newPos, Rotation = rot, State = 1, Unknown = 0,
                            });
                        }
                        else
                        {
                            // in range: hold, face the dummy, swing on cooldown
                            clone.UpdatePosition(clone.Position, rot);
                            summoner.SendTunneled(new PlayerUpdatePacketUpdatePosition
                            {
                                Guid = clone.Guid, Position = clone.Position, Rotation = rot, State = 0, Unknown = 0,
                            });

                            if (elapsed >= nextAttackMs[i] && dummy.IsAlive)
                            {
                                nextAttackMs[i] = elapsed + CloneAttackCooldownMs;

                                // swing (StartCasting animates the clone's guid)
                                summoner.SendTunneled(new AbilityPacketStartCasting
                                {
                                    Unknown = clone.Guid, Unknown2 = dummy.Guid, CompositeEffectId = 0,
                                    Animation = CloneAttackAnimation, AbilityId = 0, ActionTime = 0.3f, HasActionProgress = false,
                                });

                                // damage + shadow-blade hit on the dummy
                                var killed = dummy.ApplyDamage(CloneAttackDamage);
                                summoner.SendTunneled(new CombatPacketAttackProcessed
                                {
                                    AttackerGuid = clone.Guid,
                                    TargetGuid = dummy.Guid,
                                    Damage = CloneAttackDamage,
                                    MaxHealth = dummy.MaxHealth,
                                    CompositeEffectId = CloneHitFx,
                                    CurrentHealth = dummy.Health,
                                });

                                if (killed)
                                    ResetTrainingDummy();
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Shadow Army clone AI failed.");
            }
            finally
            {
                // poof out + remove every clone
                foreach (var clone in clones)
                {
                    summoner.SendTunneled(new PlayerUpdatePacketPlayCompositeEffect
                    {
                        Guid = clone.Guid,
                        CompositeEffectId = ShadowCloneSmokePoof,
                        Position = clone.Position,
                    });

                    clone.Dispose(); // RemovePlayer to the caster + clears zone tile + zone registration
                }
            }
        });
    }


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
    }

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
                var valid = storeBundle.Entries.All(x => _resourceManager.ClientItemDefinitions.ContainsKey(x.MarketingItemId));

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
