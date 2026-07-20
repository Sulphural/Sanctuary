using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using Microsoft.Extensions.DependencyInjection;

using Sanctuary.Game.Combat;
using Sanctuary.Game.Entities;
using Sanctuary.Game.Resources.Definitions.Zones;
using Sanctuary.Packet;

namespace Sanctuary.Game.Zones;

// COMBAT TUTORIAL ZONE (Basic Combat Tutorial, Gloam Version) — the retail tutorial is a SUMMONED
// DUNGEON INSTANCE, not an overworld script: the player is teleported into Briarheart Palace and runs
// the step sequence there, then teleports back out. This is the proper server-side zone handoff (the
// arena-zone pattern): TeleportToZone rebuilds tiles/visibility, and the client loads the real world.
//
//   world = bw_briarheart_castle_interior  (Briarheart Palace — quest doc: quest 82271 "Basic Combat
//           Tutorial (Gloam Version)", giver Darkthorne (33587), NPC Location "Briarheart Palace"; flow
//           opens "It worked! You're really here!" = Darkthorne teleports you to him in the Palace. A
//           world needs BOTH a TileInfo AND an Areas.xml to load, which is why sg_tutorial "didn't load".)
//   spawn = (67, 28, 130): the "Bed" AreaDefinition center (67, ·, 130); the interior FLOOR is at y≈20
//           (fire_01/Bed area defs sit there). The first two tries spawned at y=3 / y=15 UNDER the floor,
//           so the player fell through into the void and never saw the castle ("not it"); y=28 spawns
//           above the floor and lets physics settle the player onto it.
//
// The op45 objective system drives every step: the server ARMS a client-detected objective
// (ObjectiveLookAt 45/6, ObjectiveFirstMovement 45/11, or a plain kill/collect), the client detects the
// action and reports ObjectiveClientComplete (45/7, on the WORLD tunnel) carrying the objective id, and
// the server advances to the next step.
//
// FIRST SLICE: the three "look at the Magic Sphere" steps + three movement steps (fully verified path).
// The combat / potion steps are added on top of the same arm -> report -> advance loop.
public sealed class CombatTutorialZone : BaseZone
{
    private sealed class CombatTutorialDefinition : BaseZoneDefinition
    {
    }

    // The tutorial activity/minigame identity (matches the inline MiniGameState objectives below).
    private const int TutorialActivityId = 990000;
    private const int TutorialInstanceId = 1;

    // One tutorial step: how it's detected + the text ids from the FR Quest Documentation.
    private enum TutorialStepKind { LookAt, FirstMovement, Kill, UsePotion }

    private sealed record TutorialStepDef(
        TutorialStepKind Kind,
        int ObjectiveId,       // matched against the client's op45/7 report
        int NameId,            // goals-panel text (real goal string id)
        int InstructionId);    // Darkthorne's floating-dialogue string id (HUD message when the step arms)

    // The tutorial's steps in order. Objective + instruction ids are the real ones from the doc.
    private static readonly TutorialStepDef[] TutorialSteps =
    [
        new(TutorialStepKind.LookAt,        4419568, 4419568, 10258), // Look directly at this Magic Sphere...
        new(TutorialStepKind.LookAt,        4419570, 4419570, 10259), // Good, now ... the second Magic Sphere.
        new(TutorialStepKind.LookAt,        4419572, 4419572, 10260), // Good, now look at the third Magic Sphere.
        new(TutorialStepKind.FirstMovement, 4419576, 4419576, 10261), // Try using WASD/Arrow keys to move past these obstacles.
        new(TutorialStepKind.FirstMovement, 4419578, 4419578, 10262), // Press Spacebar to jump over these obstacles.
        new(TutorialStepKind.FirstMovement, 4419580, 4419580, 10263), // Get around the last obstacles; I'll meet you in the next room.
    ];

    // "Magic Sphere" = a purple orb (invisible anchor carrying the glowing orb - the retail pattern).
    private const int GlobeModelId = 875;      // invisible_cube_orb_purple
    private const float GlobeScale = 1.6f;      // sphere size

    // Green directional arrow pointing at each sphere (the "look here" indicator). 5567 is a COMPOSITE
    // EFFECT id (not a model) attached to the sphere via op35/41, so the arrow renders over it and is
    // removed with the sphere on completion.
    private const int ArrowEffectId = 5567;
    private const int ArrowEffectTagId = 0x7A11; // arbitrary non-zero tag slot for the attached arrow
    private const int TutorialMiniGameType = 4; // COMBAT — the goals-pane gate

    // Darkthorne (giver) guid, used as the floating-dialogue speaker (matches StartingZone's NpcGuidBase
    // + definition-id scheme so the client resolves the same portrait/name; the tutorial doesn't spawn
    // Darkthorne, the HUD message only needs a stable speaker guid).
    private const ulong DarkthorneGuid = 100000000000UL + 6445;

    private readonly IZoneManager _zoneManager;
    private readonly IResourceManager _resourceManager;

    public CombatTutorialZone(IServiceProvider serviceProvider)
        : base(CreateDefinition(), serviceProvider)
    {
        _zoneManager = serviceProvider.GetRequiredService<IZoneManager>();
        _resourceManager = serviceProvider.GetRequiredService<IResourceManager>();
    }

    private static BaseZoneDefinition CreateDefinition() => new CombatTutorialDefinition
    {
        Id = TutorialActivityId, // traceability; the runtime zone Id is assigned by the manager
        Name = "bw_briarheart_castle_interior",
        TileSize = 64,
        StartLongitude = -2,
        EndLongitude = 8,
        StartLatitude = -2,
        EndLatitude = 8,
        Sky = null, // Briarheart Palace interior's own ambience does the mood
        // "Bed" AreaDefinition center (67, ·, 130) r80. The interior FLOOR is at y≈20 (fire_01/Bed area
        // defs sit at y=20). The first two attempts spawned at y=3 / y=15 = UNDER the floor, so the player
        // fell through into the void and never actually saw the castle ("not it"). Spawn ABOVE the floor
        // and let the client physics settle the player onto it.
        SpawnPosition = new Vector4(67f, 28f, 130f, 1f),
        SpawnRotation = Quaternion.Identity,
    };

    #region Zone lifecycle

    public override void OnClientIsReady(Player player)
    {
        // Level-scaled stats + full HP/mana (and the hitpoints/mana packets) for the instanced world.
        player.RecalculateStats(refill: true);

        player.SendTunneled(new PacketZoneDoneSendingInitialData());
        player.SendTunneled(new ClientUpdatePacketDoneSendingPreloadCharacters());

        // Any kit job (ninja/archer) gets its weapon toolbar + FX cache warm-up.
        JobWeaponAbilities.SendToolbarWithFxPreload(player, _resourceManager);
    }

    // The load screen has dropped — the client reliably accepts AddNpc/objective packets from here
    // (the arena lesson: packets sent during the load screen can be discarded). Kick off the tutorial.
    public override void OnClientFinishedLoading(Player player)
    {
        StartCombatTutorial(player);
    }

    #endregion

    // Begin the tutorial for a player: create the client MiniGameState (with every step's objective
    // defined inline), spawn the look-at globes, and arm step 0.
    public void StartCombatTutorial(Player player)
    {
        // Define all objectives inline in the LAUNCH details packet — the client's op45 dispatch only
        // lets an objective be activated if it already exists in the MiniGameState.
        var objectives = TutorialSteps
            .Select(s => new EncounterObjective { ObjectiveId = s.ObjectiveId, NameId = s.NameId, Status = 0, Total = 0 })
            .ToList();

        player.SendTunneled(new EncounterDetailsResponsePacket
        {
            Unknown = TutorialActivityId,
            Unknown2 = TutorialInstanceId,
            MiniGameType = TutorialMiniGameType,
            Tutorial = true,
            Launch = true, // create the MiniGameState
            Objectives = objectives,
            ActivityId = TutorialActivityId,
        });

        // Pre-compute the three sphere positions at DISTINCT directions (left, right, up-ahead), captured
        // from the player's facing NOW. They spawn ONE AT A TIME (see ArmTutorialStep) - the next only
        // appears once the current one is looked at and despawns.
        player.TutorialPropGuids.Clear();
        player.TutorialGlobePositions.Clear();
        var forward = FacingForward(player);
        (float angleDeg, float dist, float heightOffset)[] placements =
        [
            (-45f, 7f, 2f),   // sphere 1: to the left, roughly eye level
            ( 45f, 7f, 2f),   // sphere 2: to the right
            (  0f, 7f, 4f),   // sphere 3: straight ahead, a bit high (look up)
        ];
        foreach (var (angleDeg, dist, height) in placements)
        {
            var a = angleDeg * (MathF.PI / 180f);
            var cos = MathF.Cos(a);
            var sin = MathF.Sin(a);
            var dirX = forward.X * cos - forward.Z * sin;
            var dirZ = forward.X * sin + forward.Z * cos;
            player.TutorialGlobePositions.Add(new Vector4(
                player.Position.X + dirX * dist,
                player.Position.Y + height,
                player.Position.Z + dirZ * dist,
                1f));
        }

        player.TutorialStep = 0;
        ArmTutorialStep(player); // spawns sphere 0
    }

    // Arm the current step: show Darkthorne's instruction, activate the objective, and (for a look-at
    // step) tell the client which globe to watch for.
    private void ArmTutorialStep(Player player)
    {
        var step = player.TutorialStep;
        if (step < 0 || step >= TutorialSteps.Length)
            return;

        var def = TutorialSteps[step];

        // Darkthorne's instruction as the on-screen HUD "floating dialogue box" (op35/64), the retail
        // tutorial presentation. StringId resolves the localized text client-side.
        player.SendTunneled(new HudMessagePacket
        {
            Guid1 = DarkthorneGuid,
            Guid2 = player.Guid,
            StringId = def.InstructionId,
        });

        // Announce/activate the objective (op45/1) so it shows in the goals pane.
        player.SendTunneled(new ObjectiveActivatePacket { ObjectiveId = def.ObjectiveId, Total = 1 });

        switch (def.Kind)
        {
            case TutorialStepKind.LookAt:
                // Spawn THIS step's sphere now (one at a time - the previous one despawned on its
                // completion), then arm look-at detection on it (op45/6). The client reports op45/7 on look.
                if (step < player.TutorialGlobePositions.Count)
                {
                    var spherePos = player.TutorialGlobePositions[step];
                    if (TrySpawnTutorialProp(player, spherePos, GlobeModelId, GlobeScale, out var globeGuid))
                    {
                        player.TutorialPropGuids.Add(globeGuid);
                        player.SendTunneled(new ObjectiveLookAtPacket { ObjectiveId = def.ObjectiveId, TargetGuid = globeGuid });

                        // Green directional arrow pointing at the sphere. 5567 is a COMPOSITE EFFECT id
                        // (not a model) — attach it TO the sphere so it renders the arrow over/at it and
                        // is torn down when the sphere is removed on completion.
                        player.SendTunneled(new PlayerUpdatePacketAddEffectTagCompositeEffect
                        {
                            Guid = globeGuid,
                            TagId = ArrowEffectTagId,
                            CompositeEffectId = ArrowEffectId,
                            SourceGuid = globeGuid,
                            Unknown = 0,
                            Unknown2 = 0,
                        });
                    }
                }
                break;

            case TutorialStepKind.FirstMovement:
                // Arm movement detection (op45/11): the client completes the objective on the player's
                // next movement and reports op45/7. (Barrier obstacle props are cosmetic - added later.)
                player.SendTunneled(new ObjectiveFirstMovementPacket { ObjectiveId = def.ObjectiveId, Flag = true });
                break;

                // Kill / UsePotion steps are wired in later slices.
        }
    }

    // The client reported a client-detected objective complete (op45/7). If it's the tutorial's current
    // step, tick it off and advance. Called from BaseObjectivePacketHandler.
    public void OnTutorialObjectiveComplete(Player player, int objectiveId)
    {
        var step = player.TutorialStep;
        if (step < 0 || step >= TutorialSteps.Length)
            return;
        if (TutorialSteps[step].ObjectiveId != objectiveId)
            return; // not the active step's objective

        // Green-check the objective (op45/3), then advance.
        player.SendTunneled(new ObjectiveCompletePacket { ObjectiveId = objectiveId });

        // Despawn the current step's props (the sphere + its arrow vanish once looked at). Only the
        // current step's props are ever live, so clear them all.
        foreach (var guid in player.TutorialPropGuids)
            if (guid != 0)
                player.SendTunneled(new PlayerUpdatePacketRemovePlayer { Guid = guid });
        player.TutorialPropGuids.Clear();

        player.TutorialStep = step + 1;
        if (player.TutorialStep >= TutorialSteps.Length)
            FinishCombatTutorial(player);
        else
            ArmTutorialStep(player);
    }

    private void FinishCombatTutorial(Player player)
    {
        player.TutorialStep = -1;

        // Remove any lingering spawned props.
        foreach (var guid in player.TutorialPropGuids)
            player.SendTunneled(new PlayerUpdatePacketRemovePlayer { Guid = guid });
        player.TutorialPropGuids.Clear();

        // Tear down the MiniGameState.
        player.SendTunneled(new MiniGameStateRemovePacket());

        // Teleport back to the overworld where the player triggered the tutorial (the arena return
        // pattern): EncounterReturnPosition is stashed by the entry command.
        var home = _zoneManager.StartingZone;
        var returnPos = player.EncounterReturnPosition ?? home.SpawnPosition;
        player.EncounterReturnPosition = null;
        player.TeleportToZone(home, returnPos, home.SpawnRotation, sky: null, geometryId: 0);
    }

    // Spawn a tutorial prop (sphere / arrow) visible to the player.
    private bool TrySpawnTutorialProp(Player player, Vector4 position, int modelId, float scale, out ulong guid)
    {
        guid = 0;
        if (!TryCreateNpc(out var npc))
            return false;

        npc.ModelId = modelId;
        npc.NameId = 0;
        npc.Visible = true;
        npc.Static = true;
        npc.HideNamePlate = true;
        npc.ShowHealthBar = false;
        npc.IsInteractable = false;
        npc.Scale = scale;

        npc.UpdatePosition(position, Quaternion.Identity, false);

        // Register in the zone (so guid lookups resolve) and show it to the player immediately.
        GetTileFromPosition(position).Entities.TryAdd(npc.Guid, npc);
        npc.VisiblePlayers.TryAdd(player.Guid, player);
        player.SendTunneled(npc.GetAddNpcPacket());

        guid = npc.Guid;
        return true;
    }

    // The horizontal facing direction the player is looking (from the packed rotation direction vector).
    private static Vector3 FacingForward(Player player)
    {
        var rot = player.Rotation;
        var len = MathF.Sqrt(rot.X * rot.X + rot.Z * rot.Z);
        if (len < 0.0001f)
            return new Vector3(0f, 0f, 1f);
        return new Vector3(rot.X / len, 0f, rot.Z / len);
    }
}
