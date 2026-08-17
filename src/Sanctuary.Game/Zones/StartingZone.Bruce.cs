using System;
using System.Numerics;

using Microsoft.Extensions.Logging;

using Sanctuary.Game.Entities;
using Sanctuary.Packet;

namespace Sanctuary.Game.Zones;

// BRUCE'S SNOW DAYS PERFORMANCE - the rockstar who turns up near the Gifting Tree, plays his set, and goes.
//
// He is a REAL shipped npc, not a new one: Npcs.json 3549 "Bruce", NameId 201, model 20
// human_rockstar_m.adr. That entry places him elsewhere in the world and is left alone - this is a second,
// Snowhill instance for the Snow Days set-piece, so moving the event never disturbs the original.
//
// ★ He EXISTS only while performing. That is the whole design, and it is what keeps the show contained:
// his track's audible radius (~179 units) lives in client data and cannot be turned down from the server,
// so the reach was never controllable - but his PRESENCE is. No Bruce between shows, no music between
// shows, and nothing standing around silently in the meantime.
public sealed partial class StartingZone
{
    private const int BruceNameId = 201;
    private const int BruceModelId = 20; // human_rockstar_m.adr

    // emo_air_guitar (AnimationTypes.xml slot 3350, type 4 emote, loadType 2 OnDemand). ★ The ONLY guitar
    // animation the client ships - a search of all 2336 animation slots turns up nothing else with a
    // guitar/instrument/performance name. Written as a BASE animation, so it loops for as long as he is
    // here - and since he is only ever here while playing, it needs no idle to fall back to.
    private const int BruceAnimationId = 3350;

    // ★ MX_Bruce_ItsYourWorld (ActorSoundEmitterDefinitions.xml 17715) - the ONE-SHOT cut, loopCount="1".
    // The looping twin (17716) would never stop on its own, and there is no "stop this sound" packet: the
    // client's Lua sound calls only address UI sound handles it assigned itself, which the server never
    // sees. A cut that ends by itself is what lets a show simply be over.
    //
    // Reaching it at all needed CommandPacketPlaySoundIdOnTarget (op26/39): no COMPOSITE effect wraps either
    // track, and PlayCompositeEffect only speaks composite ids - a different table and id space from sound
    // emitters. That packet was unimplemented on both this server and OSFR; see it for the trace.
    private const int BruceSoundId = 17715;

    private const int BruceShowIntervalSeconds = 600;

    // How long he stays before packing up: a flat three-minute set.
    private const int BruceShowSeconds = 180;

    // Nobody within this of the spot means nobody to play to, so the show waits.
    private const float BruceAudienceRadius = 60f;

    // Measured in game (!pos): X=318.65 Y=29.83 Z=485.41, heading -138 degrees.
    private static readonly Vector4 BrucePosition = new(318.65f, 29.83f, 485.41f, 1f);
    private const float BruceHeading = -138f * MathF.PI / 180f;

    private Npc? _bruce;
    private DateTime _bruceNextShow = DateTime.UtcNow.AddSeconds(BruceShowIntervalSeconds);
    private DateTime _bruceShowEnds;

    // Called once a second from the zone tick. NOT from Npc.UpdateEverySecondAction: that hook never fires
    // for Bruce, because the entity loop skips Static npcs outright - and for most of the time there is no
    // Bruce to tick anyway.
    // ★★ ONE ACT AT A TIME, AND THEY TAKE TURNS. Bruce and the Snow Days band each bring their OWN music -
    // his MX_Bruce_ItsYourWorld one-shot, and Jingle Bell Rock riding the drummer's animation - and both
    // carry a ~72-180 unit audible radius that lives in client data and cannot be turned down from the
    // server. Played together they simply overlap into noise, and neither can be stopped early (there is no
    // "stop this sound" packet on this wire), so the only way to keep the stage clean is to never have both
    // on it. The stage therefore runs ONE set per slot and alternates who gets it.
    private bool _bandPerformsNext;

    private void UpdateBrucePerformance()
    {
        var now = DateTime.UtcNow;

        if (_bruce is { } performing)
        {
            if (now >= _bruceShowEnds)
                EndBruceShow(performing);

            return;
        }

        // The band's set runs on the same clock, and while it is up nobody else takes the stage.
        if (IsBandOnStage)
        {
            if (now >= _bruceShowEnds)
                EndSnowDaysBandShow();

            return;
        }

        if (now < _bruceNextShow)
            return;

        // Don't spend a show on an empty clearing - the clock just rolls forward until somebody is around,
        // the same rule the snowmen wave uses.
        if (!AnyPlayersNearBruce())
            return;

        if (_bandPerformsNext)
            StartSnowDaysBandShow();
        else
            StartBruceShow();
    }

    private bool AnyPlayersNearBruce()
    {
        foreach (var player in Players)
        {
            if (!player.IsDead && HorizontalDistance(player.Position, BrucePosition) <= BruceAudienceRadius)
                return true;
        }

        return false;
    }

    private void StartBruceShow()
    {
        if (!TryCreateNpc(out var bruce))
            return;

        bruce.ModelId = BruceModelId;
        bruce.NameId = BruceNameId;
        bruce.Name = "Bruce";
        bruce.Static = true;
        bruce.Visible = true;
        bruce.Scale = _resourceManager.Models.TryGetValue(BruceModelId, out var model) && model.Scale != 0f
            ? model.Scale
            : 1f;
        bruce.BaseAnimationId = BruceAnimationId;

        var rotation = new Quaternion(MathF.Sin(BruceHeading), 0f, MathF.Cos(BruceHeading), 0f);
        bruce.UpdatePosition(BrucePosition, rotation);
        GetTileFromPosition(BrucePosition).Entities.TryAdd(bruce.Guid, bruce);

        // Mid-session spawns aren't picked up by the load-time visibility sweep, so hand him to everyone who
        // can already see the tile - otherwise he exists server-side and renders for nobody.
        PushNpcToNearbyPlayers(bruce);

        _bruce = bruce;
        _bruceShowEnds = DateTime.UtcNow.AddSeconds(BruceShowSeconds);
        _bruceNextShow = DateTime.UtcNow.AddSeconds(BruceShowIntervalSeconds);

        foreach (var listener in bruce.VisiblePlayers.Values)
        {
            listener.SendTunneled(new CommandPacketPlaySoundIdOnTarget
            {
                SoundId = BruceSoundId,
                TargetType = CommandPacketPlaySoundIdOnTarget.TargetPositionAndActor,
                TargetPosition = bruce.Position,
                TargetGuid = bruce.Guid,
            });
        }

        // ★ THE BAND DOES NOT COME ON WITH HIM - they alternate, see UpdateBrucePerformance. Two acts on
        // the stage at once means two soundtracks at once, and neither can be stopped early.
        _logger.LogInformation("Bruce turned up for a set in front of {count} player(s).",
            bruce.VisiblePlayers.Count);
    }

    private void EndBruceShow(Npc bruce)
    {
        foreach (var viewer in bruce.VisiblePlayers.Values)
        {
            viewer.SendTunneled(new PlayerUpdatePacketRemovePlayerGracefully
            {
                Guid = bruce.Guid,
                Animate = true, // walk off rather than blink out
                Delay = 0,
                EffectDelay = 0,
                CompositeEffectId = 0,
                Duration = 1000,
            });
        }

        TryRemoveNpc(bruce.Guid);
        bruce.Dispose();
        _bruce = null;

        // The band gets the next slot.
        _bandPerformsNext = true;

        _logger.LogInformation("Bruce packed up; the band is up next in {mins} minute(s).",
            BruceShowIntervalSeconds / 60);
    }
}
