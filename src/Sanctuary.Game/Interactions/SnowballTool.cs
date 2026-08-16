using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;

using Sanctuary.Core.IO;
using Sanctuary.Game.Combat;
using Sanctuary.Game.Entities;
using Sanctuary.Game.Zones;
using Sanctuary.Packet;

namespace Sanctuary.Game.Interactions;

// The Snow Days snowball fight: piles of snowballs stand around Snowhill, clicking one hands the player
// the throwing tool, and the tool lobs a real travelling snowball that knocks whoever it hits flat.
//
// The tool sits on the COMBAT toolbar, slot index 2 - the "3" key - alongside the job's attack (0) and
// special (1), NOT on the consumables item bar. That slot already has an owner in this codebase (the held
// power-up, see PowerupSystem.MakeHeldSlot), so the two share it: a held power-up wins while you have one,
// since it's transient and combat-critical, and the snowball comes back the moment it's spent.
//
// A grab is TEMPORARY: an armful lasts ToolDurationMs and then melts, so the piles are somewhere you keep
// going back to rather than a one-time unlock. No inventory item backs it - see _expiry.
//
// Everything below is REAL retail content, resolved from the shipped client data rather than invented:
//   model 1757 evnt_winter_holiday_snowballs_01.adr - Models.txt's own description for it is literally
//       "Snowball fighting snowball pile".
//   nameplate 421142 - the real localized "Snowball Pile" string (found by hashing Global.Text.<id>
//       against en_us_data.dat), so the prop labels itself the way retail's did instead of needing its
//       nameplate hidden like the ore veins do.
//   FX 15329 PRJ_ice_snowflakes (trail) + 5236 PFX_snowball_white_cog_land (splat) - the dedicated
//       snowball pair out of ActorCompositeEffectDefinitions.xml.
public static class SnowballTool
{
    // How long a grab of snowballs lasts before it's gone and you have to go back to a pile.
    public const int ToolDurationMs = 65_000;

    public const int PileModelId = 1757;             // evnt_winter_holiday_snowballs_01.adr
    public const int PileNameId = 421142;            // "Snowball Pile"

    // Sparkle on the pile so it reads as "grab me" from across the clearing. A LOOP, attached to the prop via
    // an effect tag (op35/41) so it rides it and can be pulled off with it - the "_world" variant is the one
    // authored for world objects rather than a skeleton socket, which matters on a prop with no bones.
    public const int PileSparkleFxId = 15932;        // PFX_heal_sparkles_small_loop_world
    public const int PileSparkleTagId = 91010;

    // Where the tool lands on the HUD: the combat toolbar's FOURTH slot, i.e. the "4" key. It used to sit
    // on index 2 and fight a held combat power-up for that spot; they have a key each now.
    public const int ToolbarSlotIndex = 3;

    // ★ ...but INSIDE the arena the bar is rebuilt as 0 = throw, 1 = guard, 2 = power-up, so the throw moves
    // to the FIRST slot. Two different slots for the same action - kept because the toolbar build needs it,
    // even though the cooldown can't be pointed at a specific slot (see the StartCasting note in TryThrow).
    public const int ArenaToolbarSlotIndex = 0;

    // ★ RAW IMAGE id (Resources/Images/Images.txt), NOT the item definition's Icon.Id - the ability
    // toolbar's slot icons live in the same raw-image space the job kits' weapon icons do (verified: the
    // archer kit's own BowIcon 14134 is icon_item_bow_..._archer-bark-L1_32 there). The item's Icon.Id 5866
    // is an image-SET id and would draw something unrelated.
    //
    // The picture is the SNOW DAYS SNOWBALL, user-identified from the exported icon set. Two earlier picks
    // were wrong for the same reason - being *a* snowball-named asset isn't the same as being the right
    // artwork: 28401 (icon_item_mkt_snowball_creation_unit_01_64) is the marketplace listing's icon for the
    // vending machine, and 26926 (icon_item_winter_holiday_snowball_64) isn't the one either. The _32 size
    // matches what the job kits use for their own slot icons (the archer's BowIcon 14134 is a _32).
    public const int ToolIconId = 26946;             // icon_event_snowball_fights_32
    public const int ToolNameId = 134441;            // "Snowball Creation Unit"

    // FX from ActorCompositeEffectDefinitions.xml - the client's own dedicated snowball pair.
    private const int TrailFxId = 15329;   // PRJ_ice_snowflakes
    private const int ImpactFxId = 5236;   // PFX_snowball_white_cog_land

    // THROW AUDIO. The trail (15329) is particles only, which is why a throw was silent, and FR ships no
    // dedicated throw/whoosh composite.
    //
    // ★ Do NOT use the splat composite (5236) here. It was tried, and because it is PARTICLE + SOUND the
    // player saw the snowball burst TWICE - once at the hand on release, once where it landed. The throw
    // needs a SOUND-ONLY composite, and SFX_OS_IceCrack_Sm is the one snow/ice-themed entry among them (the
    // rest of that family is ambient loops - wind, clocks, a blacksmith).
    //
    // Tunable with `/snowball throwfx <id>`; 0 = silent throw.
    public static int ThrowFxId { get; set; } = 5158; // SFX_OS_IceCrack_Sm - sound only, no particles

    // ★ The carrier stays INVISIBLE (1056 invisible_cube_with_skeleton) and the trail FX is the entire
    // visual. Flying a real snowball prop was tried and looks WRONG: 1980 sg_snowball_bbe renders as a big
    // black ball in-game (live 2026-08-13), even though the model is complete in the packs (.adr + .dme +
    // .dma) - it has no skeleton, and these props are authored for a lighting/material path that an NPC
    // actor doesn't give them.
    //
    // ★★ CORRECTION (2026-08-15) - the old conclusion here ("real prop models do not work as projectile
    // carriers in this client, don't spend another round on it") WAS WRONG, and wrong in a way worth
    // recording: the common factor in all three failures was not "prop model", it was **NO SKELETON**.
    // Decompressing the .adr files shows it directly - a working carrier names a .dsk:
    //
    //   1056 invisible_cube_with_skeleton   - the carrier in use, skeleton in the name
    //   1980 sg_snowball_bbe                - mesh only, no .dsk        -> rendered as a black ball
    //   1942 giantsnowball                  - **meatball.dsk** + snowball_m_lod0.dme + snowball.dds
    //   1994 sg_cannonball_boned_bbe        - **sg_cannonball_boned_bbe.dsk** ("boned" is literal)
    //
    // So 1942 IS a real snowball mesh with a real skeleton - the "Illusion: Giant Snowball" transform -
    // and it carries its own snow trail FX (snow_white_st_sphere_med_loop_w_giant-snowball-trail). It is
    // PLAYER-SIZED, so it needs a small scale to read as a thrown snowball. 1994 is the same idea in the
    // ball family SOE boned specifically so it could be flown as an actor, but it is a cannonball.
    //
    // Try it live, no rebuild: `/snowball model 1942` then `/snowball scale 0.1` (and 1056 / 1 to revert).
    public static int ProjectileModelId { get; set; } = 1056; // invisible_cube_with_skeleton
    public static float ProjectileScale { get; set; } = 1f;

    // The wind-up: scr_goal_throw_1h_right, the client's one and only literal "throw" animation, LIVE-CONFIRMED
    // as the right one via /snowball scan.
    //
    // ★ Its AnimationTypes.xml type is 14 = Soccer, and it was written off on that basis - reasoning that a
    // Soccer-typed clip could only exist on an actor inside the soccer minigame. That was WRONG: its
    // loadType="2" is OnDemand ("will only be loaded if requested"), and requesting it through
    // SetSynchronizedAnimations does exactly that. Do not re-derive "type 14 means unusable" from the type
    // table again - the load type is what decides availability, not the category.
    //
    // Tunable live with `/snowball anim <id>`; `/snowball scan` sweeps every candidate.
    public static int ThrowAnimationId { get; set; } = 6219; // scr_goal_throw_1h_right

    // ★ PIXIES DON'T HAVE THE THROW. scr_goal_* is the soccer GOALKEEPER family and, unlike scr_ball_off_*
    // (which ships _human and _fairy variants of every kick), it has no fairy version at all - it was only
    // ever authored for the human goalie rig. So a fairy-race player asked to play 6219 plays nothing.
    //
    // Fairies fall back to an Emote-family clip, which every race carries. emo_charge is the closest
    // forward-arm motion and is what the animation sweep shortlisted before 6219 won for humans.
    public static int ThrowAnimationIdFairy { get; set; } = 3343; // emo_charge

    // Models.txt RACE_ID for the fairy/pixie player race (fairy_m.adr / fairy_f_*.adr are race 2).
    private const int FairyRaceId = 2;
    private const int IdleAnimationId = 1;
    private const int ThrowGestureMs = 900;

    // Where in the wind-up the snowball actually LEAVES THE HAND. The projectile is held at the muzzle this
    // long so it launches on the arm's forward thrust instead of at the instant the animation starts (which
    // read as the ball leaving before the throw). An estimate of the throw's release frame - tunable live
    // with `/snowball release <ms>`, and set to 0 for the old fire-immediately behavior.
    public static int ThrowReleaseMs { get; set; } = 350;

    // com_knock_down (1402) - the knockout drop. NPCs ONLY: a stunned PLAYER is knocked down by the client
    // itself off the IsStunned status bit, so sending this to a player produced a second knockdown on top of
    // the native one. com_get_up (1403) went with it for the same reason.
    private const int KnockDownAnimationId = 1402;

    // How long the knock-down is left running on an NPC before it's reset to idle - i.e. how a SINGLE play is
    // faked out of a looping base animation, the same trick QuestDialogue uses for the talking gesture. An
    // ESTIMATE of com_knock_down's real clip length, calibrated from it visibly playing TWICE across a 2500ms
    // stun: too long and it starts over, too short and it cuts off mid-fall. Deliberately NOT the stun
    // duration - the enemy is still stunned after the clip, it just isn't re-falling on a loop.
    private const int KnockDownClipMs = 1200;

    private const int StunMs = 2500;

    // ★ THE ARENA KNOCKS DOWN FOR MUCH LESS TIME, and it has to. A downed player is skipped as a target
    // (see FindTarget - so a repeat hit can't pin them), which means the knockdown length IS the window
    // where snowballs pass straight through them. At Snowhill's 2.5s that reads as "I hit him and then my
    // next few throws did nothing", and in a race to 80 hits it throttles scoring badly. Short enough to
    // stay a real interruption, short enough that the fight keeps flowing.
    //
    // Tunable live with `/snowball stun <ms>`; the overworld event keeps its own 2.5s either way.
    public static int ArenaStunMs { get; set; } = 1_000;

    // What a snowball does to an NPC: a brief hitch, not the 2.5s player knockdown. The Snowmen Invaders
    // event is built on this - snowballs SLOW the Abominable Snowman on his march without ever stopping him,
    // so the stagger has to be short enough that a crowd only delays him.
    private const int NpcStaggerMs = 900;
    private const int CooldownMs = 2000;

    // Exposed so the arena bar can run its cooldown sweep for the right length.
    public static int ThrowCooldownMs => CooldownMs;

    // Snowballs DAMAGE hostile npcs - the Snowmen Invaders event is played by throwing them ("grab some
    // snowballs from a nearby pile and help out your friends as you knock these snowmen down"), so a throw
    // that only stunned would make the event unwinnable with the tool it is designed around. Players still
    // take no damage from a snowball, only the knockdown - it is a snowball fight, not PvP.
    public const int NpcDamage = 350;

    // How far a throw carries.
    //
    // ★ There is NO aim cone, and that is deliberate. The client only streams a player's facing WHILE THEY
    // ARE MOVING - stop running and Rotation keeps whatever direction you last travelled in, which is not
    // where you are now aiming. A facing cone therefore whiffs exactly when you stand still to throw, which
    // is the "sometimes it's not accurate when I move and then stop" bug. The combat handler learned the
    // same lesson for melee auto-target and picks nearest-in-range for precisely this reason.
    //
    // Facing is still used for the FREE THROW direction when nothing is in range - that one is a miss
    // anyway, so a stale heading costs nothing.
    // 30 threw halfway across the clearing and made aiming meaningless - a snowball should be a short lob
    // you have to close in for, not a sniper shot.
    public static float ThrowRange { get; set; } = 16f;

    // Aim cone for target selection. ~35 degrees each side counts as "pointing at it"; anything past 90
    // degrees is behind the thrower and never eligible. AimAngleWeight converts an angle miss into an
    // effective distance penalty, so among several snowmen the one you are actually facing wins.
    public static float AimConeCos { get; set; } = 0.82f;      // cos 35 deg
    private const float AimBackstopCos = 0.10f;  // just inside 90 deg
    private const float AimAngleWeight = 14f;

    private const float ProjectileSpeed = 45f;

    private static readonly ConcurrentDictionary<ulong, DateTime> _cooldowns = new();

    // When each player's armful of snowballs runs out. This IS the equip state - there is no backing
    // inventory item, because there doesn't need to be one: toolbar slot 2 is dispatched BY INDEX and never
    // resolved through ActionBarItemGuids, so the slot is synthesized from the constants above (exactly how
    // PowerupSystem's held slot works). Keeping a real item would also fight the requirement - an item sits
    // in the bags forever, and these are supposed to melt.
    private static readonly ConcurrentDictionary<ulong, DateTime> _expiry = new();

    // Supersedes a pending expiry when a player tops up at another pile before the old batch runs out -
    // without it, the FIRST grab's timer would still fire and take the refill away with it.
    private static readonly ConcurrentDictionary<ulong, int> _grantTickets = new();

    // ★ INSIDE THE ARENA THE BASIC THROW IS ALWAYS AVAILABLE. Snowhill's piles hand out a temporary armful
    // because there the tool is a toy you keep going back for; in Snowball Battles the piles hand out the
    // SPECIALS instead (Power / Freezing), and the plain snowball is the always-on slot-0 attack you fight
    // with between them. Without this the arena bar's first slot would be empty and there'd be no way to
    // fill it, since no basic pile exists in there.
    public static bool IsEquipped(Player player) =>
        player.Zone is SnowballArenaZone ||
        (_expiry.TryGetValue(player.Guid, out var until) && DateTime.UtcNow < until);

    // Whether a throw would be refused purely because the last one was too recent. Only used to tell the
    // two failure cases apart when logging - a press during the cooldown is normal, and reporting it as
    // "nothing on that slot" sent me looking for a bug that wasn't there.
    // ★ THE COOLDOWN SWEEP, WITHOUT THE ANIMATION. MeleeRefresh alone sets the cooldown-end but renders
    // nothing; LaunchAndLand is what draws the sweep - and its processor a33760 branches:
    //     if (Unknown1 == 0 || no target) { ANIMATION branch - PlayerAnimationEvent 958220 }
    //     else                            { projectile launcher b84190 }
    // Sending it with Unknown1 = 0 is what left players dancing after every throw. Unknown1 = 1 takes the
    // OTHER branch, and inside b84190 the visible projectile is spawned only when 007c4710(Unknown4)
    // resolves - so Unknown4 = 0 means that branch runs and spawns nothing. Net effect: the sweep, no
    // animation, no projectile.
    //
    // Guid2/Guid3 still have to be a REAL enemy or the client rejects the target and the whole processor
    // no-ops (which is the state where nothing rendered at all). The +0x18 list stays empty - a non-zero
    // count is the documented client crash.
    //
    // ★ SEND THIS EXACTLY AS IT WAS. MeleeRefresh greys the button, LaunchAndLand draws the sweep, and
    // LaunchAndLand renders ONLY when it carries the guids + Position and nothing else. Populating its
    // other fields while hunting the sweep's LENGTH (Unknown3/+0x30 from a since-disproved note, then
    // Unknown1 to dodge the animation branch) is what stopped it rendering at all - the field meanings are
    // unresolved, so anything non-zero is a guess that can silently break the packet.
    //
    // The sweep's ~1s length is a client-side constant. That is the accepted state; do not trade a working
    // refresh for another attempt at it.
    public static bool SendCooldownSweep { get; set; } = true;

    public static void SendCooldown(Player player, int cooldownMs, ulong targetGuid)
    {
        player.SendTunneled(new AbilityPacketMeleeRefresh { CooldownMs = cooldownMs });

        if (!SendCooldownSweep)
            return;

        if (targetGuid == 0)
            targetGuid = player.Guid;

        player.SendTunneled(new AbilityPacketLaunchAndLand
        {
            Guid = player.Guid,
            Guid2 = targetGuid,
            Guid3 = targetGuid,
            Position = player.Position,
        });
    }

    public static bool IsOnCooldown(Player player) =>
        _cooldowns.TryGetValue(player.Guid, out var readyAt) && DateTime.UtcNow < readyAt;

    // The combat-toolbar slot, or null when this player has no snowballs left. Mirrors
    // PowerupSystem.MakeHeldSlot's shape (Type 3, no AbilityDefinition behind it - slot 2 is dispatched by
    // index, never resolved through the weapon kit).
    public static AbilityPacketSetDefinition.Slot? MakeToolbarSlot(Player player) =>
        IsEquipped(player)
            ? new AbilityPacketSetDefinition.Slot
            {
                Type = 3,
                ManaCost = 0,
                IconId = ToolIconId,
                NameId = ToolNameId,
                AbilityDefinitionId = 0,
            }
            : null;

    // Hands the player an armful of snowballs and refreshes their combat toolbar so the slot appears at once.
    // Safe to call repeatedly - grabbing from another pile just restarts the clock on a full batch.
    public static void Give(Player player, IResourceManager resourceManager)
    {
        _expiry[player.Guid] = DateTime.UtcNow.AddMilliseconds(ToolDurationMs);
        var ticket = _grantTickets.AddOrUpdate(player.Guid, 1, (_, previous) => previous + 1);

        JobWeaponAbilities.SendToolbarWithPowerup(player, resourceManager);
        PreloadEffects(player);

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(ToolDurationMs);

                // A later grab wins: it holds a newer ticket, and its own timer owns the expiry from then on.
                if (_grantTickets.TryGetValue(player.Guid, out var current) && current != ticket)
                    return;

                _expiry.TryRemove(player.Guid, out _);

                // Re-send the toolbar with the slot gone. IsEquipped is already false, so ApplyThirdSlot
                // simply doesn't draw it (and a held power-up, if they picked one up meanwhile, stays put).
                JobWeaponAbilities.SendToolbarWithPowerup(player, resourceManager);
            }
            catch { }
        });
    }

    // Warm the FX cache. Composite effects load ON DEMAND, so the FIRST play of one renders nothing at all -
    // which for a snowball means the very first throw is a completely invisible one. Same trick, and the same
    // reason, as JobWeaponAbilities.PreloadAbilityEffects: play each effect once far below the map so the
    // asset is resident by the time it's thrown for real.
    public static void PreloadEffects(Player player)
    {
        if (!IsEquipped(player))
            return;

        var warmPosition = new Vector4(player.Position.X, player.Position.Y - 400f, player.Position.Z, 1f);

        foreach (var effectId in new[] { TrailFxId, ImpactFxId })
        {
            player.SendTunneled(new PlayerUpdatePacketPlayCompositeEffect
            {
                Guid = 0, // world-positioned, not attached to an actor
                CompositeEffectId = effectId,
                Position = warmPosition,
            });
        }
    }

    // The "3" key was pressed and no power-up was held. Throws along the player's facing; whoever is standing
    // in the way - another PLAYER or a hostile NPC - takes it in the face and goes down. selectedGuid is the
    // client's own selected target (packet.Guid): when it's a live enemy that beats the cone search outright,
    // the same precedence the weapon abilities use, so a snowball aimed at the enemy you have targeted lands
    // on it rather than on whatever happens to be closest to your facing.
    //
    // False = nothing happened (no tool, still on cooldown, no zone), which the caller reports back as an
    // ability failure. Note that a throw that hits NOTHING still returns true: the wind-up, the projectile and
    // the splat all play, it just doesn't knock anyone down. A snowball fight is mostly misses.
    public static bool TryThrow(Player player, IResourceManager resourceManager, ulong selectedGuid = 0,
        SnowballSpecials.SpecialKind? special = null)
    {
        // A SPECIAL carries its own charge, so it doesn't need a basic armful behind it.
        if ((special is null && !IsEquipped(player)) || player.Zone is not { } zone)
            return false;

        // ★ A SPECIAL IS NOT GATED BY THE BASIC THROW'S COOLDOWN, and does not start it. They used to
        // share this one 2-second timer, which is why a special "only worked the second time you pressed
        // it": throw a plain snowball, press the special straight after, and it was silently refused by
        // the basic's cooldown. The special carries its own, much longer one - see SnowballSpecials.
        var now = DateTime.UtcNow;

        if (special is null)
        {
            if (_cooldowns.TryGetValue(player.Guid, out var readyAt) && now < readyAt)
                return false;

            _cooldowns[player.Guid] = now.AddMilliseconds(CooldownMs);
        }

        // Lag-compensate the launch point the same way the ranged jobs do: the client renders the thrower
        // ahead of the server's last-known position, so a moving player's snowball would otherwise appear
        // behind them. Chest height, then nudged out along the throw so the trail doesn't start inside them.
        var predicted = player.PredictPosition(0.1f);
        var muzzle = new Vector4(predicted.X, player.Position.Y + 1.2f, predicted.Z, 1f);

        // Forward: the client's "rotation" is the facing DIRECTION packed as (dirX, 0, dirZ, 0), not a
        // quaternion (live-verified - see the same derivation in the combat handler's free-fire path).
        var rotation = player.Rotation;
        var forwardX = rotation.X;
        var forwardZ = rotation.Z;
        var forwardLength = MathF.Sqrt(forwardX * forwardX + forwardZ * forwardZ);
        if (forwardLength > 0.0001f)
        {
            forwardX /= forwardLength;
            forwardZ /= forwardLength;
        }
        else
        {
            forwardZ = 1f; // never thrown into their own feet
        }

        var victim = FindTarget(zone, player, selectedGuid, muzzle, forwardX, forwardZ);

        // ★ SNOWBALLS STOP ON GEOMETRY. Without this a throw passes straight through the snow forts, the
        // fence and every prop, which reads as broken the moment two players duck behind opposite forts.
        // The zone already carries the collision data the mob AI navigates by - the real per-model mesh
        // when it has been built, else the placement-derived obstacle map - so the throw just asks it
        // whether the line to the target is clear.
        if (victim is not null && !HasClearShot(zone, muzzle, victim.Position))
            victim = null; // blocked: it still flies, it just splats on whatever is in the way

        // Land it ON the victim's chest when there is one, otherwise out at the end of the throw.
        // A free throw flies LEVEL - same height it left the hand. It used to aim a metre BELOW the muzzle,
        // which at the old 30-unit range was a gentle arc but at 16 is a steep dive straight into the snow.
        var aim = victim is not null
            ? new Vector4(victim.Position.X, victim.Position.Y + 1f, victim.Position.Z, 1f)
            : new Vector4(muzzle.X + forwardX * ThrowRange, muzzle.Y, muzzle.Z + forwardZ * ThrowRange, 1f);

        var aimDx = aim.X - muzzle.X;
        var aimDz = aim.Z - muzzle.Z;
        var aimLength = MathF.Sqrt(aimDx * aimDx + aimDz * aimDz);
        if (aimLength > 0.001f)
            muzzle = new Vector4(muzzle.X + aimDx / aimLength * 1.2f, muzzle.Y, muzzle.Z + aimDz / aimLength * 1.2f, 1f);

        // ★ NO AbilityPacketStartCasting ON THIS PATH - it was tried and REVERTED. It was added purely to
        // name the toolbar slot for the cooldown grey (MeleeRefresh carries no slot field), but the client
        // plays an animation off it that cannot be suppressed: Animation = -1 emoted, and passing the real
        // throw id or reordering it ahead of the throw clip did not stop it either. The throw's motion comes
        // from SetSynchronizedAnimations below and nothing else may touch it.
        var throwAnimationId = ThrowAnimationFor(player, resourceManager);

        var throwAnimation = BuildAnimation(player.Guid, throwAnimationId);

        player.SendTunneled(throwAnimation);
        foreach (var watcher in player.VisiblePlayers.Values)
            watcher.SendTunneled(throwAnimation);

        // Back to standing once the wind-up is done - the same reset StopDancing does after a boombox dance,
        // since a synchronized animation is held until something replaces it.
        player.SendTunneledToVisibleDelayed(new PlayerUpdatePacketSetAnimation
        {
            Guid = player.Guid,
            AnimationId = IdleAnimationId,
            PlayType = 1
        }, ThrowGestureMs, sendToSelf: true);

        // Grey the toolbar button and sweep it - see SendCooldown for why this can't just be the plain
        // LaunchAndLand the combat kits send.
        SendCooldown(player, special is null ? CooldownMs : SnowballSpecials.CooldownMs, victim?.Guid ?? 0);

        // The throw sound + snow puff, delayed to the release frame so it lands with the arm rather than at
        // the start of the wind-up. Sent to everyone who can see the thrower so other players hear it too.
        if (ThrowFxId > 0)
        {
            var releaseFx = new PlayerUpdatePacketPlayCompositeEffect
            {
                Guid = 0, // world-anchored at the hand - a one-shot, so nothing is left behind
                CompositeEffectId = ThrowFxId,
                Position = muzzle,
            };

            if (ThrowReleaseMs > 0)
                player.SendTunneledToVisibleDelayed(releaseFx, ThrowReleaseMs, sendToSelf: true);
            else
                player.SendTunneledToVisible(releaseFx, sendToSelf: true);
        }

        var thrower = player;
        var hit = victim;

        // ★ A snowball TRACKS whoever it was thrown at. Aiming once at where they stood is fine for a
        // long-range arrow but reads as badly thrown at snowball distances - the victim takes two steps
        // and it visibly sails past them even though the hit still lands. Opt-in per projectile, so the
        // archer/wizard shots are untouched.
        var projectile = ProjectileNpc.Fire(zone, player, muzzle, aim, hit?.Guid ?? 0,
            trailEffId: TrailFxId,
            impactEffId: ImpactFxId,
            speed: ProjectileSpeed,
            modelId: ProjectileModelId,
            scale: ProjectileScale,
            lingerMs: 800,
            onImpact: hit is null ? null : () => KnockDown(thrower, hit, special),
            launchDelayMs: ThrowReleaseMs); // leaves the hand on the throw, not before it

        if (projectile is not null)
            projectile.HomingTarget = hit;

        return true;
    }

    // Whether a thrown snowball can actually reach a point, or whether a fort/fence/prop is in the way.
    // Prefers the real per-model mesh (MeshObstacleMap) and falls back to the placement obstacle map;
    // a zone with neither (nothing built) lets everything through, which is the old behaviour.
    private static bool HasClearShot(IZone zone, Vector4 from, Vector4 to)
    {
        if (zone is not BaseZone baseZone)
            return true;

        if (baseZone.NavMesh is { } mesh)
            return mesh.IsLineWalkable(from, to);

        if (baseZone.NavObstacles is { } obstacles)
            return obstacles.IsLineWalkable(from, to);

        return true;
    }

    private static bool IsFairy(Player player, IResourceManager resourceManager) =>
        resourceManager.Models.TryGetValue(player.Model, out var model) && model.RaceId == FairyRaceId;

    // The wind-up clip for this player's RACE - see ThrowAnimationIdFairy for why fairies need their own.
    private static int ThrowAnimationFor(Player player, IResourceManager resourceManager) =>
        IsFairy(player, resourceManager) ? ThrowAnimationIdFairy : ThrowAnimationId;

    private static PlayerUpdatePacketSetSynchronizedAnimations BuildAnimation(ulong guid, int animationId)
    {
        var packet = new PlayerUpdatePacketSetSynchronizedAnimations();
        packet.Animations.Add(new PlayerUpdatePacketSetSynchronizedAnimations.Animation
        {
            Guid = guid,
            AnimationId = animationId
        });
        return packet;
    }

    // Who the snowball lands on. The client's selected enemy wins outright when it's a live hostile in range;
    // otherwise it's whoever is actually standing in the throw - the closest PLAYER or hostile NPC inside the
    // cone. Anyone already knocked down is skipped so a downed target isn't pinned indefinitely by repeat hits.
    private static IEntity? FindTarget(IZone zone, Player thrower, ulong selectedGuid, Vector4 muzzle,
        float forwardX, float forwardZ)
    {
        // ★ THE SELECTED TARGET CAN BE A PLAYER, NOT JUST AN NPC. This only ever resolved the selection
        // against npcs, which is fine in Snowhill (you throw at snowmen) but silently broke the arena: a
        // PvP throw at the enemy you had actually clicked on fell through to "nearest in range" instead,
        // so it went at whoever happened to be closest rather than who you were aiming at. That is the
        // "it doesn't throw straight" case.
        if (selectedGuid != 0 && selectedGuid != thrower.Guid)
        {
            IEntity? selected = null;

            if (zone.TryGetNpc(selectedGuid, out var selectedNpc) && IsThrowable(selectedNpc))
            {
                selected = selectedNpc;
            }
            else
            {
                foreach (var candidate in zone.Players)
                {
                    if (candidate.Guid != selectedGuid || candidate.IsDead)
                        continue;
                    if (StatusEffects.BlocksAbilities(candidate.Guid))
                        break; // already down - fall through to the nearest-target search
                    if (zone is SnowballArenaZone selectionArena && selectionArena.SameTeam(thrower, candidate))
                        break; // team-mate: friendly fire is off, so this isn't a valid pick

                    selected = candidate;
                    break;
                }
            }

            if (selected is not null)
            {
                var sdx = selected.Position.X - muzzle.X;
                var sdz = selected.Position.Z - muzzle.Z;
                if (sdx * sdx + sdz * sdz <= ThrowRange * ThrowRange)
                    return selected;
            }
        }

        // While the Snowmen Invaders battle is running, snowballs only land on SNOWMEN. Players are skipped
        // entirely for the duration so a wave is spent defending the tree rather than pelting each other;
        // the moment the battle ends, everyone is fair game again.
        var playersAreTargets = zone is not StartingZone { SnowmenBattleActive: true };

        // In the Snowball Battles arena, friendly fire is off - a snowball never picks out someone on your
        // own side, so the only players in the running are the other team's.
        var arena = zone as SnowballArenaZone;

        // ★ AIM MATTERS. This used to pick the NEAREST throwable in range and ignore which way the thrower
        // was facing, so during the Snow Days invasion - where a dozen snowmen are converging on one tree -
        // a throw at the one you were looking at snapped to whichever happened to be closest, including
        // ones behind you. Candidates are scored on ANGLE FROM FACING first and distance second.
        //
        // Two passes so aiming is rewarded without ever making a throw feel dead: anything inside the tight
        // cone wins on aim, and only if the cone is empty does it fall back to the old nearest-in-range
        // behaviour (widened, but still forward-biased).
        IEntity? best = null;
        var bestScore = float.MaxValue;
        var bestInCone = false;

        void Consider(IEntity candidate)
        {
            var dx = candidate.Position.X - muzzle.X;
            var dz = candidate.Position.Z - muzzle.Z;
            var distance = MathF.Sqrt(dx * dx + dz * dz);

            if (distance < 0.001f || distance >= ThrowRange)
                return;

            // cos of the angle between "where I'm looking" and "where they are"
            var dot = (dx / distance) * forwardX + (dz / distance) * forwardZ;

            if (dot < AimBackstopCos)
                return; // behind the thrower - never a target

            var inCone = dot >= AimConeCos;

            // A cone hit always beats a non-cone hit; within the same class, nearest wins but a better
            // angle is worth a couple of units, so the one you are actually pointing at is preferred over
            // one slightly closer off to the side.
            var score = distance + (1f - dot) * AimAngleWeight;

            if (bestInCone && !inCone)
                return;

            if (inCone && !bestInCone)
            {
                bestInCone = true;
                bestScore = score;
                best = candidate;
                return;
            }

            if (score >= bestScore)
                return;

            bestScore = score;
            best = candidate;
        }

        foreach (var candidate in playersAreTargets ? zone.Players : [])
        {
            if (candidate.Guid == thrower.Guid || candidate.IsDead)
                continue;
            if (StatusEffects.BlocksAbilities(candidate.Guid))
                continue; // already flat on their back
            if (arena is not null && arena.SameTeam(thrower, candidate))
                continue; // team-mate
            Consider(candidate);
        }

        foreach (var npc in zone.Npcs)
        {
            if (!IsThrowable(npc))
                continue;
            Consider(npc);
        }

        return best;
    }

    // Enemies only: a snowball is aimed at things you're allowed to fight, not at the quest givers standing
    // around Snowhill. IsDamageable is the same "this is a real combat target" test the weapon abilities use.
    // ★ Deliberately NOT IsDamageable, and not IsHostile either. The Snow Days snowmen carry no hitpoints
    // (MaxHealth 0 is what keeps a health bar off them) and are presented to the client as neutral so they
    // get the plain nameplate - both of which would have excluded them here and made them unhittable. What
    // actually qualifies is "a combat actor that is still up": CombatNpc covers the event snowmen and every
    // real world enemy, while props, quest givers and the snowball piles are plain Npc and stay untargetable.
    private static bool IsThrowable(Npc npc) =>
        npc is CombatNpc && npc.IsAlive && !StatusEffects.BlocksAbilities(npc.Guid);

    // Direct hit: the victim is knocked flat and can't act until they get back up. The Stun status effect is
    // the mechanical half for both kinds of victim (StatusEffects.BlocksAbilities gates every ability behind
    // it, the client's own movement controller halts on the IsStunned bit for a player, and the NPC AI
    // already honours it - it's what the Earth Shard power-up inflicts). The animations are the visual.
    private static void KnockDown(Player thrower, IEntity victim, SnowballSpecials.SpecialKind? special = null)
    {
        // showFx: false - the Stun kind's own FX is the Flabbergast Sphere's big yellow star explosion, which
        // is the right burst for the ORB that inflicts it and completely wrong for a snowball. The splat
        // (PFX_snowball_white_cog_land, played by the projectile on landing) is this hit's whole visual.
        //
        // NPCs are NOT stunned - a full stun would freeze a marching boss, and the event wants him slowed,
        // never stopped. They take a short stagger instead (see CombatNpc.Stagger).
        // ★ GUARDED: the shield eats it. The projectile has already played its splat against the bubble,
        // which is the whole visual - but no stun, no knockdown, and no point for the thrower (see
        // OnSnowballHit). FIRST, before the stun below, or a blocked snowball would still freeze them.
        if (victim is Player guarded && SnowballGuard.IsGuarding(guarded))
            return;

        if (victim is Player)
        {
            // A special replaces the plain knockdown: Power holds them down longer, Freezing freezes.
            var (kind, durationMs) = special switch
            {
                SnowballSpecials.SpecialKind.Power => (StatusEffectKind.Stun, SnowballSpecials.PowerStunMs),
                SnowballSpecials.SpecialKind.Freezing => (StatusEffectKind.Freeze, SnowballSpecials.FreezingStunMs),
                _ => (StatusEffectKind.Stun, victim.Zone is SnowballArenaZone ? ArenaStunMs : StunMs),
            };

            StatusEffects.Apply(victim, kind, durationMs, source: thrower, showFx: false);

            if (special is { } specialKind)
                SnowballSpecials.PlayImpact((Player)victim, specialKind, durationMs);
        }

        if (victim is Player hitPlayer)
        {
            // ONE knockdown, sent explicitly, for EVERY race - com_knock_down has no race variants in
            // AnimationTypes.xml, so the same clip serves human and fairy alike.
            //
            // ★ And nothing else. The earlier "stunned twice off one snowball" was NOT the client playing its
            // own knockdown on top (it turns out no race gets one automatically) - it was the FOLLOW-UPS this
            // used to send: com_get_up at the end of the stun plus an idle reset after that, which read as a
            // second fall. The client returns to its normal stance on its own once the one-shot finishes, so
            // the recovery clips were never needed.
            hitPlayer.SendTunneledToVisible(BuildAnimation(hitPlayer.Guid, KnockDownAnimationId), sendToSelf: true);

            // In the arena a connecting hit is also a POINT. Everywhere else a player snowball is still
            // just the knockdown - the overworld piles are a toy, not a scored match.
            if (thrower.Zone is SnowballArenaZone arena)
                arena.OnSnowballHit(thrower, hitPlayer);

            return;
        }

        if (victim is not Npc hitNpc)
            return;

        if (hitNpc is CombatNpc staggerable)
            staggerable.Stagger(NpcStaggerMs);

        // Damage + the floating number, then route the kill/hit through the zone exactly as a weapon ability
        // does - that is what credits quest goals, awards XP, and (for the Snowmen Invaders event) puts the
        // thrower on the reward list and rolls their Snowman Coal.
        if (thrower.Zone is not { } zone)
            return;

        // Credit the thrower for taking part in the world event (the reward list). Damage and death go down
        // the normal path below, which is what makes the snowmen's health bars actually drain.
        if (zone is StartingZone startingZone)
            startingZone.OnSnowmenDamaged(thrower, hitNpc);

        if (hitNpc.IsDamageable)
        {
            var killed = hitNpc.ApplyDamage(NpcDamage, fromSnowball: true);

            // ★ This packet CARRIES the target's max/current health, and receiving those is what makes the
            // client draw a health bar - so it is the last thing that has to be suppressed for a barless
            // enemy, after the AddNpc flag, the stat push and BroadcastHpUpdate. It is only here for the
            // floating damage number, and those are off in the overworld anyway (see
            // Player.SendInWorldCombatFlag), so skipping it costs nothing visible.
            if (hitNpc.ShowHealthBar)
                thrower.SendTunneledToVisible(new PlayerUpdatePacketHitPointModification
                {
                    Guid = thrower.Guid,
                    Guid2 = hitNpc.Guid,
                    Unknown = true,
                    Unknown2 = hitNpc.MaxHealth,
                    Unknown3 = hitNpc.Health,
                    Unknown4 = -NpcDamage,
                }, sendToSelf: true);

            if (killed)
            {
                zone.OnNpcKilled(thrower, hitNpc);
                return; // the kill path despawns it - no point animating a corpse
            }

            zone.OnNpcDamaged(thrower, hitNpc);
        }

        // ★ NPCs are the opposite case: they ONLY animate through a BASE-animation write (PlayType 1). The
        // client's "play now" path bails on anything without [entity+0x1870], which an NPC never has - so the
        // synchronized-animation packet above is a no-op on them.
        SendToWatchers(hitNpc, new PlayerUpdatePacketSetAnimation
        {
            Guid = hitNpc.Guid,
            AnimationId = KnockDownAnimationId,
            PlayType = 1
        });

        // A base animation LOOPS until something replaces it, so holding it for the whole stun re-played the
        // whole fall a second time. Reset after ONE clip instead: knocked down once, then back on its feet
        // but still unable to act for the rest of the stun.
        thrower.SendTunneledToVisibleDelayed(new PlayerUpdatePacketSetAnimation
        {
            Guid = hitNpc.Guid,
            AnimationId = IdleAnimationId,
            PlayType = 1
        }, KnockDownClipMs, sendToSelf: true);
    }

    private static void SendToWatchers(Npc npc, ISerializablePacket packet)
    {
        foreach (var watcher in npc.VisiblePlayers.Values)
            watcher.SendTunneled(packet);
    }
}
