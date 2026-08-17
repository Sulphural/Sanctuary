using System;
using System.Collections.Generic;
using System.Numerics;

using Microsoft.Extensions.Logging;

using Sanctuary.Game.Entities;
using Sanctuary.Packet;

namespace Sanctuary.Game.Zones;

// THE SNOW DAYS BAND - the three Christmas robgoblins who play Bruce's stage: two on guitar and one on
// the drums.
//
// ★★ THEY ARE PURPOSE-BUILT MODELS, NOT DRESSED-UP MOBS, and the client hands over almost the whole
// performance if you let it. Models.txt has `1952 robgoblin_m_drummer.adr` and `1953 robgoblin_m_guitar.adr`
// sitting next to each other, and decompressing those two .adr files answers the animation AND the music
// questions outright:
//
//   robgoblin_m_guitar   loc_stand      -> robgoblin_elf_amb_loc_stand_strum_03   (it strums by DEFAULT)
//                        amb_loop_01..04 -> four more strum loops, plus two one-shots and a fidget
//   robgoblin_m_drummer  loc_stand      -> a plain stand
//                        amb_loop_01    -> robgoblin_elf_amb_drum_loop_long
//                                          + **MX_Radio_JingleBellRock, tagged "Music LP"**
//
// ★★★ THE MUSIC COMES FREE WITH THE ANIMATION. The drummer's .adr binds Jingle Bell Rock to amb_loop_01
// itself, so the CLIENT starts the track when it plays that animation - the server sends no sound packet
// at all. That matters twice over: it is less work, and it steps around the trap Bruce documents. His
// track is played as a one-shot on purpose because `MX_Radio_JingleBellRock` is `loopCount="0"` (endless)
// and there is no "stop this sound" packet on this wire - so firing 17461 by hand would leave Jingle Bell
// Rock playing over Snowhill forever. Bound to the animation it simply stops when the drummer despawns.
// (The id is recorded below anyway, because knowing which sound it is matters even if nothing sends it.)
//
// Animation ids from AnimationTypes.xml: amb_loop_01 2100, _02 2110, _03 2120, _04 2130 - all type 13
// (looping), which is what a BaseAnimationId wants.
//
// ★ THEY SHARE BRUCE'S LIFECYCLE. He exists only while performing (see StartingZone.Bruce), and a band
// that outlived its singer would be three robgoblins drumming at an empty stage - so they are spawned and
// struck with him. The .adr files even carry `com_spawn`/`com_despawn` clips with a rainbow portal and
// gold fireworks, which is how retail brought them on; that is the natural next polish step and the
// effect names are listed at the bottom of this file.
public sealed partial class StartingZone
{
    private const int BandDrummerModelId = 1952; // robgoblin_m_drummer.adr
    private const int BandGuitarModelId = 1953;  // robgoblin_m_guitar.adr

    // The looping "playing my instrument" animations. The two guitarists deliberately get DIFFERENT strum
    // loops - a band whose members are frame-locked to each other reads as one puppet with three bodies.
    private const int BandAmbLoop01 = 2100; // drummer: drum loop (+ the music). guitar: strum_long
    private const int BandAmbLoop02 = 2110; // guitar: a second, longer strum

    // ActorSoundEmitterDefinitions.xml 17461 `MX_Radio_JingleBellRock` (Jinglebellrock.ogg, clipDistance
    // 72, loopCount 0 = endless). NOTHING SENDS THIS - it rides the drummer's amb_loop_01. See the header.
    private const int BandMusicSoundId = 17461;

    // ★ MEASURED IN GAME (!pos), used verbatim - the same rule every other spawn in this zone follows.
    //   guitarist 1, at the mic : X=318.79 Y=29.83 Z=485.89  heading -147
    //   guitarist 2, side stage : X=315.02 Y=29.81 Z=488.76  heading -176
    private static readonly Vector4 BandGuitarist1Position = new(318.79f, 29.83f, 485.89f, 1f);
    private const float BandGuitarist1Heading = -147f * MathF.PI / 180f;

    private static readonly Vector4 BandGuitarist2Position = new(315.02f, 29.81f, 488.76f, 1f);
    private const float BandGuitarist2Heading = -176f * MathF.PI / 180f;

    // ★ THE DRUMMER'S SPOT IS THE ONE VALUE HERE THAT IS NOT MEASURED - it was not supplied, so it is
    // derived rather than invented: the two guitarists' midpoint, pushed ~2.5 units AWAY from the way they
    // are facing (both headings point roughly south-west, at the audience), which puts the kit centre-back
    // behind the pair. Stage height is flat at 29.82 across both measured spawns.
    // It is a PROPERTY, not a const, so `/band drums <x> <y> <z> [heading]` can walk it into place from a
    // real !pos reading without a rebuild - and then it should be pasted back here as a measured value.
    public static Vector4 BandDrummerPosition { get; set; } = new(317.6f, 29.82f, 489.5f, 1f);
    public static float BandDrummerHeadingDegrees { get; set; } = -160f;

    private readonly List<Npc> _snowDaysBand = [];

    // ── Their set ─────────────────────────────────────────────────────────────────────────────────────
    // ★ THE BAND AND BRUCE NEVER SHARE THE STAGE. They each bring their own music and neither track can be
    // stopped early, so playing together just overlaps into noise - the stage runs one act per slot and
    // alternates between them (see UpdateBrucePerformance, which owns the clock for both).
    private void StartSnowDaysBandShow()
    {
        SpawnSnowDaysBand();

        if (_snowDaysBand.Count == 0)
            return;

        // Same slot length and spacing Bruce gets - they are two acts sharing one stage clock.
        _bruceShowEnds = DateTime.UtcNow.AddSeconds(BruceShowSeconds);
        _bruceNextShow = DateTime.UtcNow.AddSeconds(BruceShowIntervalSeconds);
    }

    private void EndSnowDaysBandShow()
    {
        RemoveSnowDaysBand();

        // Hand the stage back to Bruce for the next slot.
        _bandPerformsNext = false;

        _logger.LogInformation("Snow Days band packed up; Bruce is up next in {mins} minute(s).",
            BruceShowIntervalSeconds / 60);
    }

    private void SpawnSnowDaysBand()
    {
        if (_snowDaysBand.Count > 0)
            return;

        SpawnBandMember(BandGuitarModelId, BandGuitarist1Position, BandGuitarist1Heading, BandAmbLoop01, "guitarist (mic)");
        SpawnBandMember(BandGuitarModelId, BandGuitarist2Position, BandGuitarist2Heading, BandAmbLoop02, "guitarist (side)");
        SpawnBandMember(BandDrummerModelId, BandDrummerPosition,
            BandDrummerHeadingDegrees * MathF.PI / 180f, BandAmbLoop01, "drummer");

        _logger.LogInformation("Snow Days band took the stage ({count} robgoblins); the drummer's amb_loop_01 " +
                               "carries MX_Radio_JingleBellRock ({sound}).", _snowDaysBand.Count, BandMusicSoundId);
    }

    private void SpawnBandMember(int modelId, Vector4 position, float heading, int animationId, string role)
    {
        if (!TryCreateNpc(out var member))
            return;

        member.ModelId = modelId;
        member.Name = null;          // no nameplate - they are set dressing, not people to talk to
        member.HideNamePlate = true;
        member.Static = true;
        member.Visible = true;
        member.Scale = _resourceManager.Models.TryGetValue(modelId, out var model) && model.Scale != 0f
            ? model.Scale
            : 1f;

        // ★ A BASE animation, not a one-shot: it has to run for the whole set. Type 13 loops on its own,
        // so nothing has to re-fire it - and on the drummer this is also what starts the music.
        member.BaseAnimationId = animationId;

        // Not clickable. They have no interaction and no health bar; leaving MaxHealth at 0 keeps the
        // nameplate/health gate off (see reference_nameplate_healthbar_gate).
        member.IsInteractable = false;
        member.MaxHealth = 0;
        member.ShowHealthBar = false;

        var rotation = new Quaternion(MathF.Sin(heading), 0f, MathF.Cos(heading), 0f);
        member.UpdatePosition(position, rotation);
        GetTileFromPosition(position).Entities.TryAdd(member.Guid, member);

        // Mid-session spawn, so the load-time visibility sweep has long since run - hand them to everyone
        // already near the stage or they exist server-side and render for nobody. Same pairing Bruce needs.
        PushNpcToNearbyPlayers(member);

        _snowDaysBand.Add(member);

        _logger.LogInformation("Snow Days band: {role} (model {model}, anim {anim}) at {position}.",
            role, modelId, animationId, position);
    }

    private void RemoveSnowDaysBand()
    {
        foreach (var member in _snowDaysBand)
        {
            foreach (var viewer in member.VisiblePlayers.Values)
            {
                viewer.SendTunneled(new PlayerUpdatePacketRemovePlayerGracefully
                {
                    Guid = member.Guid,
                    Animate = true,
                    Delay = 0,
                    EffectDelay = 0,
                    CompositeEffectId = 0,
                    Duration = 1000,
                });
            }

            TryRemoveNpc(member.Guid);
            member.Dispose();
        }

        _snowDaysBand.Clear();
    }

    // Put the band on NOW, for tuning the drummer's spot without waiting for their slot to come round.
    // ★ Clears Bruce off the stage first if he is mid-set - the whole point of the alternation is that
    // only one act is ever up, and a debug command should not be the thing that breaks that rule.
    public void StageSnowDaysBandNow()
    {
        if (_bruce is { } performing)
            EndBruceShow(performing);

        RemoveSnowDaysBand();
        StartSnowDaysBandShow();
    }

    // Put BRUCE on now, clearing the band first for the same reason.
    public void StageBruceNow()
    {
        RemoveSnowDaysBand();

        if (_bruce is { } alreadyOn)
            EndBruceShow(alreadyOn);

        StartBruceShow();
    }

    // Empty the stage - both acts off, and the next slot goes to whoever was due anyway.
    public void ClearStage()
    {
        if (_bruce is { } performing)
            EndBruceShow(performing);

        RemoveSnowDaysBand();
    }

    public bool IsBandOnStage => _snowDaysBand.Count > 0;
    public bool IsBrucePerforming => _bruce is not null;

    public string StageStatus =>
        IsBrucePerforming ? "Bruce is playing"
        : IsBandOnStage ? "the band is playing"
        : $"stage empty ({(_bandPerformsNext ? "band" : "Bruce")} up next)";

    // ── Not wired yet: the arrival ────────────────────────────────────────────────────────────────────
    // Both .adr files carry a full entrance/exit, which is how retail put them on stage rather than having
    // them blink in. The clips are `com_spawn` / `com_despawn`, and each is authored with two effects:
    //   portal      rainbow-starburst_multi_out_pt_med_loop_dares-portal_.xml   (on ROOT; the drummer
    //                                                                           calls the same socket
    //                                                                           `spawn_portal`)
    //   fireworks   fireworks_gold_exp_pt_lg_short_l_dares-stage-appearance.xml (drummer: one per hand,
    //                                                                           L_ATTACH / R_ATTACH)
    // Those are composite-effect XML names rather than ids, so wiring them means finding the matching
    // ActorCompositeEffectDefinitions rows first - deliberately left for a later pass.
}
