using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using Sanctuary.Game;
using Sanctuary.Game.Combat;
using Sanctuary.Game.Entities;
using Sanctuary.Game.Zones;
using Sanctuary.Packet;
using Sanctuary.Packet.Common;

namespace Sanctuary.Gateway.Handlers;

// Combat-only half of AbilityPacketClientRequestStartAbilityHandler - split out for size (PR #27 review
// asked to split up this handler; the item-ability side was already split into Handlers/Abilities classes,
// but combat-ability casting is one linear pipeline (target resolution -> ability resolution -> energy/
// cooldown gating -> projectile/FX -> damage resolution), not independent categories, so it doesn't fit
// that same per-category pattern. This is a straight file split - no logic changes.
public static partial class AbilityPacketClientRequestStartAbilityHandler
{
    // Fallback projectile trails for ranged shots whose ability has no elemental trail of its own (plain
    // basics/specials). Element-specific abilities use their own CastEffectId. 15483 = PRJ_magical_green_arrow,
    // 16188 = PRJ_sparkles_purple_trail_loop (an arcane bolt).
    private const int DefaultArcherTrailFx = 15483;
    private const int DefaultWizardTrailFx = 16188;

    // StartCasting ActionTime locks the action-bar slot for the whole swing/cast so you can't fire again mid-
    // animation; DamageDelay is when the number lands (as the swing connects / the special resolves).
    private const float SpecialActionTime = 0.4f;  // slot 1 named special — a real wind-up
    private const float SpecialDamageDelay = 0.4f; // number pops at the end of the special's animation (melee/AoE only - ranged shots override this with the real projectile flight time, see ProjectileFlightSeconds below)

    // Single source of truth for projectile speed - was hardcoded 90f at both Fire() call sites below, which
    // is how it drifted out of sync with itself. Matches ProjectileNpc.Launch's own flightSec = dist/speed.
    // Slowed from 90 -> 70 (live feedback, 2026-07-27) - not retail-sourced (no real arrow-speed data
    // exists), just a play-feel tune.
    private const float ProjectileSpeed = 70f;

    private static float ProjectileFlightSeconds(Vector4 from, Vector4 to)
    {
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var dz = to.Z - from.Z;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz) / ProjectileSpeed;
    }

    // Basic attack resolves ONE swing per ANIMATION, not per key-press (the client fires faster than the clip
    // plays). Pace it to the swing animation's length so the slot locks + the damage number land in sync and you
    // can't spam faster than the swing. 660ms (sword/fist; 2014-04-01 capture median 0.662s) for every job's
    // basic attack, no per-animation exceptions.
    //
    // CORRECTED 2026-07-29 (live feedback: "attack speed should match for all combat jobs") - this used to
    // give 2-handed hammer swings (anim 1080, com_2hp_attack) their own 1150ms pace ("wind-up" flavor, not a
    // retail-sourced number - no wiki/capture citation existed for it). That silently made Brawler (EVERY
    // Brawler weapon is a 2-handed hammer) attack ~74% slower than every other job's basic attack, and made
    // Warrior slower specifically on its higher-tier weapons (Battle Hammer/Double Axe/Warlord Axe all swing
    // 1080; only the low-tier Cudgel/Axe are 1-handed) - an unintentional per-job/per-weapon-tier speed gap,
    // not a deliberate design choice. SwingMsByAnim is kept (empty) as the mechanism for if a REAL per-
    // animation pace number ever turns up, rather than deleting the pacing seam entirely.
    private const int BasicSwingMs = 660;
    private static readonly System.Collections.Generic.Dictionary<int, int> SwingMsByAnim = new();
    private static int SwingMsForAnimation(int anim) => SwingMsByAnim.GetValueOrDefault(anim, BasicSwingMs);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, long> _nextBasicSwingTicks = new();

    // Shared with CombatPacketAutoAttackTargetHandler so a basic swing paces + syncs identically no matter
    // which client input triggers it (toolbar press = op36, direct click-to-attack = op32). Before this, op32
    // applied damage instantly with no pace gate at all - a raw click-attack could out-swing and out-pace the
    // exact same weapon fired from the toolbar, which is the kind of per-input-path inconsistency that makes
    // combat feel desynced. Returns false (drop the swing, no damage) if the previous swing hasn't finished.
    internal static bool TryGateBasicSwing(Player player, int animation, out float damageDelay)
    {
        var swingMs = SwingMsForAnimation(animation);
        damageDelay = swingMs * 0.85f / 1000f;

        var now = Environment.TickCount64;
        if (_nextBasicSwingTicks.TryGetValue(player.Guid, out var next) && now < next)
            return false; // still mid-swing — ignore this extra click (no damage, no re-pace)
        _nextBasicSwingTicks[player.Guid] = now + swingMs;
        return true;
    }

    private const int SpecialEnergyCost = NinjaWeaponAbilities.SpecialEnergyCost; // 100 — shared with the toolbar's slot ManaCost (client grey-out)
    // Special cadence: a special costs the full 100 bar, so full-refill time = the effective special
    // cooldown. 10/sec => 100/10 = 10s to refill => a special every ~10 seconds (the retail pace we want).
    // (The 2014-04-01 capture value was 4/sec = 25s, which felt too slow.) Half-cost archer level abilities
    // (50) come back in ~5s, proportionally.
    private const int EnergyRegenPerSec = 10;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, bool> _regenRunning = new();

    // Real per-ability cooldowns, independent of the shared energy pool above. Before this, every non-basic
    // ability on a job drained the SAME energy bar, so jobs with more than one special (Archer: Special +
    // Sniper Shot + Rain of Arrows, all on separate toolbar slots) couldn't have two up at once even though
    // they're meant to be independently-cooldown-gated abilities, not one shared resource - firing Sniper
    // Shot blocked Rain of Arrows for the same ~5s it blocked Sniper Shot itself. Jobs with only one special
    // (everyone but Archer today) don't visibly change - there was nothing else to compete with. Duration is
    // derived from the SAME cost/regen numbers the energy pool already used (cost/EnergyRegenPerSec), so this
    // doesn't change how fast anything comes back — it only makes each ability's timer its own, instead of
    // all specials sharing one clock.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, System.Collections.Concurrent.ConcurrentDictionary<string, long>> _abilityCooldownEndTicks = new();

    private static bool TryGetAbilityCooldownRemainingMs(Player player, string abilityName, out long remainingMs)
    {
        remainingMs = 0;
        if (!_abilityCooldownEndTicks.TryGetValue(player.Guid, out var perAbility))
            return false;
        if (!perAbility.TryGetValue(abilityName, out var endTicks))
            return false;
        var now = Environment.TickCount64;
        if (now >= endTicks)
            return false;
        remainingMs = endTicks - now;
        return true;
    }

    private static void StartAbilityCooldown(Player player, string abilityName, int durationMs)
    {
        var perAbility = _abilityCooldownEndTicks.GetOrAdd(player.Guid, _ => new());
        perAbility[abilityName] = Environment.TickCount64 + durationMs;
    }

    private static int GetEnergy(Player player) => _energy.TryGetValue(player.Guid, out var e) ? e : MaxEnergy;


    // Time-based +4/sec regen loop, running only while the player's energy is below max (mirrors the real
    // server, which only streamed op38/sub13 while the bar was refilling).
    private static void StartEnergyRegen(Player player)
    {
        if (!_regenRunning.TryAdd(player.Guid, true))
            return; // already regenerating

        _ = Task.Run(async () =>
        {
            try
            {
                while (GetEnergy(player) < MaxEnergy)
                {
                    await Task.Delay(1000);
                    // Warrior High Morale (L15): energy regenerates faster.
                    var regen = EnergyRegenPerSec;
                    if (WarriorWeaponAbilities.HasTrait(player, WarriorWeaponAbilities.HighMoraleLevel))
                        regen += WarriorWeaponAbilities.HighMoraleEnergyRegenBonus;
                    var next = Math.Min(MaxEnergy, GetEnergy(player) + regen);
                    _energy[player.Guid] = next;
                    SendEnergy(player, next);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Energy regen loop failed.");
            }
            finally
            {
                _regenRunning.TryRemove(player.Guid, out _);
            }
        });
    }

    // Ability damage is tuned for a maxed job; scale it down by the caster's job rank so a fresh (level-1)
    // combat job can't one-shot everything. rank 1 -> LowRankDamageFactor of full, MaxLevel -> full.
    private const float LowRankDamageFactor = 0.10f;
    private static int ScaleDamageByRank(Player player, int baseDamage)
    {
        int rank;
        try { rank = player.ActiveProfile.Rank; }
        catch { return baseDamage; } // no active profile (shouldn't happen mid-cast) -> leave unscaled

        const int max = Sanctuary.Game.Leveling.JobLeveling.MaxLevel;
        if (rank >= max) return baseDamage;
        if (rank < 1) rank = 1;

        var t = (rank - 1f) / (max - 1f);              // 0 at rank 1, 1 at max
        // Ease-IN (t^2) rather than linear: a fresh job ramps up its damage gradually so early combat isn't a
        // 2-shot (the old linear curve gave rank 3 ~20% of full = still a 2-hit kill on a 650-HP overworld
        // enemy). Power still climbs to full by max rank, where a basic 1-shots basic enemies as retail did.
        var factor = LowRankDamageFactor + (1f - LowRankDamageFactor) * t * t;
        return Math.Max(1, (int)(baseDamage * factor));
    }

    // The ability comes from the pressed slot + equipped weapon (the job kit): slot 0 = melee, slot 1 = the
    // weapon's special. Damage / animation / hit-FX all from that table.
    public static int? DebugAnimationOverride;
    // COMBAT (combat branch): an ability-bar press — resolve the target + the equipped weapon's ability,
    // play the cast, then resolve damage. See NinjaWeaponAbilities for the slot -> ability mapping.
    private static bool HandleCombatAbility(GatewayConnection connection, AbilityPacketClientRequestStartAbility packet, ReadOnlySpan<byte> data)
    {
        // COMBAT WIP: capture the live client->server StartAbility fields so we can map
        // action-bar slots to abilities and implement real resolution. Remove/lower once mapped.
        _logger.LogInformation(
            "StartAbility: ActionBar.Id={id} Slot={slot} Target={target} Guid={guid} Pos=({px},{py},{pz},{pw}) Raw={raw}",
            packet.Data.Id, packet.Data.Slot, packet.Target, packet.Guid,
            packet.Position.X, packet.Position.Y, packet.Position.Z, packet.Position.W,
            Convert.ToHexString(data));

        var player = connection.Player;
        var zone = player.Zone;

        // The "3" key / toolbar slot index 2 = the held power-up (PowerupSystem) - pinned there on top of
        // the normal 2-slot weapon toolbar (0=basic, 1=special) whenever one is held. Never routes through
        // the normal weapon-ability resolution below.
        if (packet.Data.Slot == 2)
        {
            if (!PowerupSystem.TryUse(player, _resourceManager))
                return SendFailure(connection);
            return true;
        }

        // We DON'T enter world-combat just for pressing fire — entry is gated on actually hitting an enemy (see
        // EnterWorldCombat once a target resolves, + the re-stamp in ResolveDamageAfterCast). Swinging at air
        // animates but doesn't flag you. The killing blow keeps you in combat for the decay window so the bow
        // auto-fires at the next enemy after a kill.

        // Resolve the target: honor the client's selected-enemy guid if it sent one; otherwise hit the nearest
        // live hostile within reach (a swing at nothing whiffs — StartCasting plays, no damage). (Old code
        // grabbed the first hostile anywhere in the zone — the "random wolf across the arena gets hit" bug.)
        Npc? targetNpc = null;

        if (packet.Guid != 0 && zone.TryGetNpc(packet.Guid, out var selected) && selected.IsDamageable && selected.IsAlive)
        {
            targetNpc = selected;
        }
        else
        {
            // Auto-target for an unselected swing = nearest live hostile within range (the SOE server chose the
            // target when the client sent Target=0; "nearest in range" reconstructs it). The range cap stops the
            // "random far wolf gets hit" bug; closest (not first-in-list) hits the one on you. No facing cone —
            // the client only sends facing while moving, so a cone whiffs when you stand still. Horizontal (X/Z)
            // radius. Melee = 7u (04-01 capture: 37 hits ran 0.6–9.2, median 2.3; 7 is forgiving of tick lag
            // without grabbing far wolves — lower toward 5 if grabby). Archers use the bow range instead.
            var attackReach = JobWeaponAbilities.AutoTargetReach(player);
            var reach2 = attackReach * attackReach;
            var best2 = reach2;

            foreach (var n in zone.Npcs)
            {
                if (!n.IsHostile || !n.IsDamageable || !n.IsAlive)
                    continue;

                var dx = n.Position.X - player.Position.X;
                var dz = n.Position.Z - player.Position.Z;
                var d2 = dx * dx + dz * dz;
                if (d2 >= best2)
                    continue;

                best2 = d2;
                targetNpc = n;
            }
        }

        var targetGuid = targetNpc?.Guid ?? (packet.Guid != 0 ? packet.Guid : player.Guid);

        // Resolve the ability from the pressed slot + equipped weapon for the ACTIVE JOB's kit
        // (slot 0 = basic attack/shot, slot 1 = the weapon's named special).
        var ability = JobWeaponAbilities.ResolveAbility(player, packet.Data.Slot);

        // Pace the basic attack to its swing ANIMATION (2h hammers wind up slower than swords/fists). The slot
        // locks for the whole swing (ActionTime) and the number lands as it connects (DamageDelay ~85% in), so
        // hits sync with the animation instead of firing many per swing when you spam.
        var isBasicMelee = packet.Data.Slot <= 0;

        // Silence blocks the named SPECIAL only - a basic melee swing still works while silenced (matches
        // the standard MMO convention this system follows; see StatusEffects.IsSilenced).
        if (!isBasicMelee && StatusEffects.IsSilenced(player.Guid))
            return SendFailure(connection);
        var swingMs = isBasicMelee ? SwingMsForAnimation(ability.Animation) : 0;
        var actionTime = isBasicMelee ? swingMs / 1000f : SpecialActionTime;
        var damageDelay = isBasicMelee ? swingMs * 0.85f / 1000f : SpecialDamageDelay;

        // Server-side pace backup: drop presses that arrive before the current swing finishes, so we get one
        // swing + one damage number per animation, not one per key-press. Feedback via AbilityPacketFailed
        // (op36/1) - the same packet the item-ability path (HandleItemAbility/SendFailure) already sends on
        // its own denials; combat presses used to just go silent here with nothing telling the client why.
        if (isBasicMelee && swingMs > 0)
        {
            var now = Environment.TickCount64;
            if (_nextBasicSwingTicks.TryGetValue(player.Guid, out var next) && now < next)
                return SendFailure(connection); // still mid-swing — ignore this extra click (no cast, no number)
            _nextBasicSwingTicks[player.Guid] = now + swingMs;
        }

        // Gate (non-basic slots): the pressed ability must have both energy available AND its OWN cooldown
        // expired - two independent gates now, not one shared pool standing in for both. Energy still drains
        // and refills exactly as before (the UI grey-out feed, op38/13, doesn't change); the per-ability
        // cooldown is what actually stops Sniper Shot from blocking Rain of Arrows (or vice versa) the way a
        // single shared pool did.
        var meleeRefreshMs = BasicSwingMs;
        if (!isBasicMelee)
        {
            if (TryGetAbilityCooldownRemainingMs(player, ability.Name, out var remainingMs))
            {
                _logger.LogInformation("StartAbility: ability '{name}' blocked — {ms}ms left on its own cooldown.",
                    ability.Name, remainingMs);
                return SendFailure(connection);
            }

            var cost = ability.EnergyCost;
            var energy = GetEnergy(player);
            if (energy < cost)
            {
                _logger.LogInformation("StartAbility: ability blocked — energy {e}/{max} < {cost}.",
                    energy, MaxEnergy, cost);
                return SendFailure(connection);
            }

            var remaining = energy - cost;
            _energy[player.Guid] = remaining;
            SendEnergy(player, remaining);   // op38/sub13: bar drops by the cost
            StartEnergyRegen(player);        // begin the +4/sec refill

            // Same effective duration the shared pool used to imply (cost/regen), just tracked per-ability now.
            var cooldownMs = (int)(cost / (float)EnergyRegenPerSec * 1000);
            StartAbilityCooldown(player, ability.Name, cooldownMs);
            meleeRefreshMs = cooldownMs;
        }

        // Triage's real "healing you and your group" purpose (see MedicWeaponAbilities.cs's TriageHealAmount
        // comment) — unlike Shock Paddles' revive (a passive TRAIT effect), Triage is the weapon SPECIAL
        // itself, so it's correctly gated on actually casting it here (energy paid, cooldown started above),
        // independent of whether a hostile is in range (its damage half is separately AoE-resolved below).
        if (ability.Name == "Triage" && player.ActiveProfileId == MedicWeaponAbilities.MedicProfileId)
            player.HealSelfAndNearbyAllies(MedicWeaponAbilities.TriageHealAmount, MedicWeaponAbilities.TriageHealRadius);

        // Immunize's real "makes you and your group invincible" purpose (see MedicWeaponAbilities.cs's
        // ImmunizeDamageReductionPercent comment) — same reasoning as Triage above: this is the weapon
        // SPECIAL itself, gated on actually casting it, independent of whether a hostile is in range.
        if (ability.Name == "Immunize" && player.ActiveProfileId == MedicWeaponAbilities.MedicProfileId)
            player.ApplyDamageReductionToNearbyAllies(MedicWeaponAbilities.ImmunizeDamageReductionPercent,
                MedicWeaponAbilities.ImmunizeDurationMs, MedicWeaponAbilities.ImmunizeBuffRadius);

        var startCastingFx = ability.CastEffectId;

        // RANGED jobs (Archer/Wizard): fly a real travelling projectile from caster -> target carrying the
        // ability's OWN trail (CastEffectId - freezing/fire/lightning/arcane per weapon), instead of pinning
        // the trail on the caster. Server-authoritative (ProjectileNpc: invisible carrier + attached trail,
        // stopped + faded on hit). The impact FX (EffectId) is played ON THE VICTIM by ResolveDamageAfterCast,
        // so the projectile carries no impact here (impactEffId 0) to avoid double-playing.
        var firedProjectile = false;
        var isArcher = player.ActiveProfileId == ArcherWeaponAbilities.ArcherProfileId;
        var isWizard = player.ActiveProfileId == WizardWeaponAbilities.WizardProfileId;
        // MULTISHOT victims (beyond the selected target) - filled by the projectile block, consumed by the
        // damage-targets build below so the extra bolts actually hurt who they visually hit.
        var multishotExtras = new System.Collections.Generic.List<Npc>();
        // Single-target ranged shots fly a projectile - AT the target when there is one, or STRAIGHT AHEAD
        // along the player's facing when shooting at nothing (retail free-fire). AoE specials (area bursts)
        // keep their ground FX - a single travelling projectile doesn't fit "hits everything in a radius".
        if ((isArcher || isWizard) && ability.AoeRadius <= 0f && player.Zone is { } projectileZone)
        {
            // The trail is the ability's OWN CastEffectId when it has one (the elemental signature specials);
            // otherwise a job-appropriate default arrow/bolt so every basic + plain special still fires one.
            var trailFx = ability.CastEffectId > 0
                ? ability.CastEffectId
                : (isWizard ? DefaultWizardTrailFx : DefaultArcherTrailFx);

            // Lag-compensate the muzzle: the client renders the player ahead of the server's last-known
            // position (stale update + downlink), so a moving shooter's projectile would otherwise spawn
            // behind them. Predict the client-current X/Z; keep the launch at weapon height. Zero when standing.
            // Downlink allowance is small on purpose - 0.3s overshot ~2.4m at run speed, visibly detaching
            // the launch from the bow (screen-recorded: the projectile popped in beside/ahead of the player).
            var predicted = player.PredictPosition(0.1f);
            var muzzle = new System.Numerics.Vector4(predicted.X, player.Position.Y + 1.2f, predicted.Z, 1f);

            System.Numerics.Vector4 aim;
            ulong impactGuid;
            if (targetNpc is not null)
            {
                // Aim at the target's body center (not +1.2 above its base - that made bolts land "on top" of
                // shorter enemies instead of on them).
                aim = new System.Numerics.Vector4(targetNpc.Position.X, targetNpc.Position.Y + 0.7f, targetNpc.Position.Z, 1f);
                impactGuid = targetNpc.Guid;

                // MULTISHOT: a targeted shot fans into up to 3 projectiles - the extra bolts pick the
                // closest OTHER live hostiles within 10m of the primary target. They're also added to the
                // damage list below, so every bolt that visually lands actually hits.
                var tp = targetNpc.Position;
                multishotExtras = zone.Npcs
                    .Where(n => !ReferenceEquals(n, targetNpc) && n.IsHostile && n.IsDamageable && n.IsAlive)
                    .Select(n =>
                    {
                        var dx = n.Position.X - tp.X;
                        var dz = n.Position.Z - tp.Z;
                        return (npc: n, d2: dx * dx + dz * dz);
                    })
                    .Where(t => t.d2 <= 10f * 10f)
                    .OrderBy(t => t.d2)
                    .Take(2)
                    .Select(t => t.npc)
                    .ToList();
            }
            else
            {
                // No target: fire straight ahead along the player's facing. The client's character "rotation"
                // is NOT a quaternion - it's the facing DIRECTION VECTOR packed as (dirX, 0, dirZ, 0) (live-
                // verified: rot.X/rot.Z track the movement-velocity direction to within ~2 deg). So forward is
                // just (rot.X, rot.Z).
                var rot = player.Rotation;
                var fwdX = rot.X;
                var fwdZ = rot.Z;
                var len = System.MathF.Sqrt(fwdX * fwdX + fwdZ * fwdZ);
                if (len > 0.0001f) { fwdX /= len; fwdZ /= len; }
                const float FreeFireRange = 30f;
                aim = new System.Numerics.Vector4(muzzle.X + fwdX * FreeFireRange, muzzle.Y, muzzle.Z + fwdZ * FreeFireRange, 1f);
                impactGuid = 0; // nothing to hit
            }

            // Nudge the launch point ~1.6m out of the player's body along the aim line: the trail emitter
            // starts laying particles the instant it attaches, and starting it INSIDE the character was the
            // screen-recorded "starburst flash on me every shot" - retail bolts appear at the bow tip, not
            // in the caster's chest.
            var adx = aim.X - muzzle.X;
            var adz = aim.Z - muzzle.Z;
            var alen = System.MathF.Sqrt(adx * adx + adz * adz);
            if (alen > 0.001f)
                muzzle = new System.Numerics.Vector4(
                    muzzle.X + adx / alen * 1.6f, muzzle.Y, muzzle.Z + adz / alen * 1.6f, 1f);

            // INVISIBLE carrier (1056, has a bone so the attached trail renders) - the trail IS the visual,
            // which is the retail look. Real prop models were tried and screen-recorded reading as junk: 1982
            // renders as a big dark untextured ball, 793 as a chunky red arrow, both worse than the bare FX.
            // Speed 90 ~= retail arrow zip; at the old 55 the looping emitter laid a dense lingering particle
            // bridge across the whole flight path ("laser line" instead of a moving bolt).
            var lingerMs = ability.CastEffectStopMs > 0 ? ability.CastEffectStopMs : 1200;
            ProjectileNpc.Fire(projectileZone, player, muzzle, aim, impactGuid,
                trailEffId: trailFx, impactEffId: 0, speed: ProjectileSpeed, lingerMs: lingerMs);

            // The extra multishot bolts: same muzzle, fanning out to each extra victim.
            foreach (var extra in multishotExtras)
            {
                var extraAim = new System.Numerics.Vector4(extra.Position.X, extra.Position.Y + 0.7f, extra.Position.Z, 1f);
                ProjectileNpc.Fire(projectileZone, player, muzzle, extraAim, extra.Guid,
                    trailEffId: trailFx, impactEffId: 0, speed: ProjectileSpeed, lingerMs: lingerMs);
            }
            firedProjectile = true;
            startCastingFx = 0; // the projectile carries the trail — nothing pinned on the caster

            // Damage/hit-FX used to land on the flat SpecialDamageDelay (0.4s) regardless of how far the bolt
            // actually had to fly - at close range the number popped ~350ms before the bolt visually arrived,
            // and past ~36m (0.4s * 90u/s) it could land before the bolt was even halfway there. Tie it to the
            // same dist/speed flight time ProjectileNpc itself uses, so the hit lands when the bolt does.
            // (Multishot extras still share this one delay - they're all resolved in a single batch below - so
            // this syncs the PRIMARY bolt exactly; extra bolts at a different distance from the primary target
            // remain an approximation, same as before.)
            if (impactGuid != 0)
                damageDelay = ProjectileFlightSeconds(muzzle, aim);
        }

        // MELEE jobs: lingering cast FX (CastEffectStopMs > 0: trails/loops that never self-terminate) play as
        // an effect tag on the caster and remove after the window, so the trail flashes with the swing instead
        // of lingering. One-shot cast FX keep riding StartCasting's CompositeEffectId.
        if (!firedProjectile && startCastingFx > 0 && ability.CastEffectStopMs > 0)
        {
            startCastingFx = 0;

            var tagId = System.Threading.Interlocked.Increment(ref _castFxTagCounter);
            player.SendTunneledToVisible(new PlayerUpdatePacketAddEffectTagCompositeEffect
            {
                Guid = player.Guid,
                TagId = tagId,
                CompositeEffectId = ability.CastEffectId,
                SourceGuid = player.Guid,
            }, sendToSelf: true);
            var stopMs = ability.CastEffectStopMs;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(stopMs);
                    player.SendTunneledToVisible(new PlayerUpdatePacketRemoveEffectTagCompositeEffect
                    {
                        Guid = player.Guid,
                        TagId = tagId,
                    }, sendToSelf: true);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lingering cast-FX stop failed.");
                }
            });
        }

        // COMBAT WIP: respond to an ability press with a real StartCasting (proven to render a cast bar
        // + play the caster's animation) instead of the AbilityPacketFailed stub.
        var startCasting = new AbilityPacketStartCasting
        {
            Unknown = player.Guid,            // caster
            Unknown2 = targetGuid,            // target
            CompositeEffectId = startCastingFx, // one-shot FX on the caster during the cast
            Animation = DebugAnimationOverride ?? ability.Animation, // override via !anim for live probing
            AbilityId = packet.Data.Slot + 1, // cast identifier (not visual-critical)
            ActionTime = actionTime,
            HasActionProgress = false,        // no cast/progress bar for a basic melee swing
        };

        // Broadcast the cast to everyone who can see the caster (not just their own screen) so party members
        // see each other's moves/FX. Was caster-only, which is why teammates saw enemies die but not the moves.
        player.SendTunneledToVisible(startCasting, sendToSelf: true);

        // Cooldown sweep on the ability button (grey + spinning radial together, matching retail - live-traced
        // 2026-07-25: the client's AbilityProcessor only ever updates its cooldown-end/UI state off THIS packet;
        // nothing else observed touches it). Previously sent for the basic swing only on the (wrong) assumption
        // that specials were fully covered by the energy-bar grey-out; a live frida trace showed the special's
        // op36 traffic never included a MeleeRefresh at all, which is why only the grey ever showed for it.
        player.SendTunneled(new AbilityPacketMeleeRefresh { CooldownMs = meleeRefreshMs });

        // op36/4 LaunchAndLand - a prior RE pass (2026-07-18/19, see reference_ability_packet_formats.md)
        // proved this is what actually starts the ability slots' cooldown SWEEP client-side, but the wiring
        // was reverted before the +0x18 crash bug got fixed and never reinstated. The list field (+0x18) is
        // sent empty here, which that investigation confirmed is crash-safe. Sent for every cast (not just
        // specials) so the basic slot sweeps the same way instead of relying on MeleeRefresh alone. Sweep
        // length is a client-side ~1s constant regardless of any field sent (confirmed exhaustively back
        // then) - the real per-ability duration lives in AbilityPacketAbilityDefinition, which we still send
        // mostly zeroed, so don't expect a multi-second sweep from this alone yet.
        // Live-confirmed 2026-07-25: the sweep only renders when Guid2/Guid3 resolve to a REAL enemy target -
        // its internal target-resolution silently no-ops otherwise (targetGuid falls back to the caster's own
        // guid when nothing is targeted, which the client doesn't accept). Matches retail use anyway (cooldown
        // sweeps happen when you actually use an ability on something).
        player.SendTunneled(new AbilityPacketLaunchAndLand
        {
            Guid = player.Guid,
            Guid2 = targetGuid,
            Guid3 = targetGuid,
            Position = player.Position,
        });

        // Weapon-empowering specials (Mysticism / Mystical Blade) bind their FX to the sword (item slot 7)
        // instead of the body. SlotCompositeEffectOverride op35/sub31: Guid + slot + composite effect.
        if (ability.SwordEffectId > 0)
        {
            player.SendTunneledToVisible(new PlayerUpdatePacketSlotCompositeEffectOverride
            {
                Guid = player.Guid,
                Slot = NinjaWeaponAbilities.WeaponSlot, // 7 = the equipped weapon
                CompositeEffect = ability.SwordEffectId,
            }, sendToSelf: true);
        }

        // Any special with SummonCount>0 spawns temporary combat-capable clone NPCs around the caster, then
        // they poof away after a few seconds - generalized 2026-07-29 (see CombatCloneConfig's header
        // comment) to work in ANY zone against real nearby hostiles, not just the tutorial zone's training
        // dummy. Two abilities use this: Ninja's Shadow Army (shadow-ninja clones) and Medic's Nurse!
        // (medical-assistant clones, Nurse Naia's model) - dispatch on the ability name to pick the right
        // look/config for whichever one was actually cast.
        if (ability.SummonCount > 0 && zone is { } summonZone)
        {
            if (ability.Name == "Shadow Army")
                summonZone.SummonCombatClones(player, ability.SummonCount, NinjaWeaponAbilities.ShadowArmyLifetimeSeconds, NinjaWeaponAbilities.ShadowArmyCloneConfig);
            else if (ability.Name == "Nurse!")
                summonZone.SummonCombatClones(player, ability.SummonCount, MedicWeaponAbilities.NurseSummonLifetimeSeconds, MedicWeaponAbilities.NurseCloneConfig);
        }

        // Self-buff abilities (e.g. Ninja's Mystical Blade — see NinjaWeaponAbilities' MysticismKit
        // comment) apply a temporary % damage multiplier to the CASTER instead of resolving damage
        // against a target. The cast/animation + SwordEffectId FX above already played, so a buff needs
        // nothing further and — critically — must NOT fall into the target-resolution path below, which
        // would otherwise silently no-op the whole cast whenever no enemy happens to be in range.
        if (ability.BuffMultiplierPct > 0)
        {
            CombatBuffs.AddDamageBuff(player.Guid, ability.BuffMultiplierPct, ability.BuffDurationMs);
            return true;
        }

        // AOE specials (AoeRadius > 0) hit EVERY live hostile within the radius of the CASTER — the whole
        // pack, not just the selected target. Single-target abilities keep the resolved target.
        System.Collections.Generic.List<Npc> targets;
        if (ability.AoeRadius > 0)
        {
            var r2 = ability.AoeRadius * ability.AoeRadius;
            var c = player.Position;
            targets = zone.Npcs
                .Where(n => n.IsHostile && n.IsDamageable && n.IsAlive)
                .Where(n =>
                {
                    var dx = n.Position.X - c.X;
                    var dz = n.Position.Z - c.Z;
                    return dx * dx + dz * dz <= r2;
                })
                .ToList();
        }
        else
        {
            targets = targetNpc is null ? [] : [targetNpc];
            // Multishot victims take the hit too - every bolt that visually landed does damage.
            targets.AddRange(multishotExtras);
        }

        if (targets.Count == 0)
        {
            _logger.LogInformation("StartAbility: no damageable target found (slot {slot}, aoe {radius}).",
                packet.Data.Slot, ability.AoeRadius);
            return true;
        }

        // A real enemy is being engaged (at least one live hostile target) — NOW enter world-combat. Gating it
        // here (instead of on every key press) is what stops firing into empty air from flagging you in-combat.
        player.EnterWorldCombat();

        // Scale the ability's (max-level-tuned) damage down by the caster's job rank so a level-1 combat job
        // doesn't hit as hard as a maxed one (was one-shotting everything). Ramps from LowRankDamageFactor at
        // rank 1 to full at MaxLevel. Applies to every combat job (they share this path).
        var scaledDamage = ScaleDamageByRank(player, ability.Damage);

        _logger.LogInformation("Ability slot {slot} = '{name}' (dmg {dmg}->{scaled}, anim {anim}, fx {fx}, targets {count})",
            packet.Data.Slot, ability.Name, ability.Damage, scaledDamage, ability.Animation, ability.EffectId, targets.Count);

        ResolveDamageAfterCast(player, targets, scaledDamage, ability.EffectId, damageDelay,
            ability.CasterEndEffectId, ability.EnemyExtraEffectId,
            ability.TickCount, ability.TickIntervalMs, ability.CasterEndEffectStopMs);

        return true;
    }


    // After the cast bar completes: apply damage, play the hit FX, push each health bar, kill/respawn at 0 HP.
    // Runs off-thread so the cast time elapses first. AoE specials pass the whole in-radius pack (one
    // HitPointModification per victim in a burst, like the 04-01 capture).
    //
    // tickCount/tickIntervalMs (default 1/0 = a single pass, unchanged for every ability except Volley -
    // see WeaponAbility's header comment): repeats the whole damage+FX pass this many times, spaced
    // tickIntervalMs apart, so a "rain of arrows"-style special actually lands a few real hits instead of
    // one hit under an FX that plays forever.
    private static void ResolveDamageAfterCast(Player player, System.Collections.Generic.IReadOnlyList<Npc> targets,
        int damage, int effectId, float damageDelay, int casterEndEffectId = 0, int enemyExtraEffectId = 0,
        int tickCount = 1, int tickIntervalMs = 0, int casterEndEffectStopMs = 0)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay((int)(damageDelay * 1000));

                // Landing a hit puts you in world-combat (sub132 SetInWorldCombat + sub133 SetIsFighting),
                // which opens the client's floating-damage-number gate and job-locks while fighting (released by
                // the decay). Player owns the state machine, so getting HIT enters it too.
                player.EnterWorldCombat();

                // Caster-side end FX plays ONCE regardless of how many victims/ticks (e.g. Dragonstrike's land
                // FX). Broadcast to visible players (sendToSelf) so teammates see it too.
                //
                // CORRECTED 2026-07-27 (live feedback: Volley's rain-of-arrows FX "shouldn't last that long" -
                // it's a "_loop_" asset played as a bare one-shot trigger with no stop, so it rained forever).
                // casterEndEffectStopMs > 0 switches to the same tag-attach/remove mechanism CastEffectStopMs
                // already uses for lingering CAST fx - held for that many ms, then explicitly removed, instead
                // of firing a looping asset with nothing to ever turn it off.
                if (casterEndEffectId > 0 && casterEndEffectStopMs > 0)
                {
                    var tagId = System.Threading.Interlocked.Increment(ref _castFxTagCounter);
                    player.SendTunneledToVisible(new PlayerUpdatePacketAddEffectTagCompositeEffect
                    {
                        Guid = player.Guid,
                        TagId = tagId,
                        CompositeEffectId = casterEndEffectId,
                        SourceGuid = player.Guid,
                    }, sendToSelf: true);
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(casterEndEffectStopMs);
                            player.SendTunneledToVisible(new PlayerUpdatePacketRemoveEffectTagCompositeEffect
                            {
                                Guid = player.Guid,
                                TagId = tagId,
                            }, sendToSelf: true);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Caster-end FX stop failed.");
                        }
                    });
                }
                else if (casterEndEffectId > 0)
                {
                    player.SendTunneledToVisible(new PlayerUpdatePacketPlayCompositeEffect
                    {
                        Guid = player.Guid,
                        CompositeEffectId = casterEndEffectId,
                        Position = player.Position,
                    }, sendToSelf: true);
                }

                for (var tick = 0; tick < System.Math.Max(1, tickCount); tick++)
                {
                    if (tick > 0)
                        await Task.Delay(tickIntervalMs);

                    foreach (var target in targets)
                    {
                        if (!target.IsAlive)
                            continue; // e.g. died to an earlier hit this same tick

                        // Job crit traits (each gated to its own job, so only the active job's applies): Archer
                        // Precision/Marksmanship, Brawler Bruising Strikes/Savvy, Warrior Piercing Strikes, Wizard
                        // Genius/Arcane Flare, Medic Target Vitals/Surgical Skills/Combat Medicine/Vitamins/Shock
                        // Paddles. Rolled per hit so AoE specials can crit some targets and not others.
                        var hitDamage = MedicWeaponAbilities.ApplyTraitDamage(player,
                            WizardWeaponAbilities.ApplyTraitDamage(player,
                                WarriorWeaponAbilities.ApplyTraitDamage(player,
                                    BrawlerWeaponAbilities.ApplyTraitDamage(player, ArcherWeaponAbilities.ApplyTraitDamage(player, damage)))),
                            target);

                        // Active self-buff (e.g. Mystical Blade) multiplies the final number - applied last so
                        // it stacks with crit rolls rather than being rolled into the crit chance itself.
                        hitDamage = CombatBuffs.ApplyDamage(player.Guid, hitDamage);

                        var killed = target.ApplyDamage(hitDamage);

                        // Impact FX on the victim (the ability's EffectId). HitPointModification has no effect field,
                        // so play it explicitly (the switch away from AttackProcessed had dropped every impact FX).
                        //
                        // CORRECTED 2026-07-27 (live feedback: "i see the hit effects on the player when the enemy
                        // attacks, but it should also show on the enemy when the player attacks" - i.e. the mob->
                        // player path, CombatEncounterZone.PerformMobAttack's CombatPacketAttackProcessed, was
                        // ALWAYS rendering fine; only the player->enemy path was silent). Single-target impacts used
                        // to ride op36/14 DetonateProjectile specifically (once confirmed live with effect id 21,
                        // a generic/likely-already-cached effect) - unlike the AoE branch's PlayCompositeEffect,
                        // which was independently proven safe for a real per-ability EffectId landing on an NPC
                        // target. Since DetonateProjectile is this opcode's ONLY real-world confirmation and it's
                        // not rendering the actual per-job ids, drop the single-target/AoE split entirely and use
                        // the proven-safe packet for both - same call, just always take the position-based branch.
                        if (effectId > 0)
                        {
                            player.SendTunneledToVisible(new PlayerUpdatePacketPlayCompositeEffect
                            {
                                Guid = target.Guid,
                                CompositeEffectId = effectId,
                                Position = target.Position,
                            }, sendToSelf: true);
                        }

                        // EnemyExtraEffectId plays an ADDITIONAL effect on each victim on top of the hit FX
                        // (e.g. Soul Power's purple ring around the enemy).
                        if (enemyExtraEffectId > 0)
                        {
                            player.SendTunneledToVisible(new PlayerUpdatePacketPlayCompositeEffect
                            {
                                Guid = target.Guid,
                                CompositeEffectId = enemyExtraEffectId,
                                Position = target.Position,
                            }, sendToSelf: true);
                        }

                        // Deal the player's own hits via HitPointModification (op35/35), NOT AttackProcessed:
                        // AttackProcessed resets the action-bar melee timer when attacker == local player (the [1]
                        // cooldown bug); HitPointModification gives the number + bar + recoil without touching it.
                        // Wire (04-01): Guid=source(player), Guid2=victim, leading bool=01, i2=maxHP, i3=curHP-after,
                        // i4=-damage.
                        player.SendTunneledToVisible(new PlayerUpdatePacketHitPointModification
                        {
                            Guid = player.Guid,           // source / attacker
                            Guid2 = target.Guid,          // victim
                            Unknown = true,               // player->NPC sample had the leading bool = 01
                            Unknown2 = target.MaxHealth,  // max HP (bar denominator)
                            Unknown3 = target.Health,     // current HP AFTER the hit (bar position)
                            Unknown4 = -hitDamage,        // delta = -damage -> the floating number
                        }, sendToSelf: true);

                        // ARCHER TRAIT — Lucky Shot (L20): a landed hit sometimes restores a little energy.
                        ArcherWeaponAbilities.TryLuckyShotEnergy(player);

                        _logger.LogInformation(
                            "Ability hit {name} ({guid}) for {dmg} -> {hp}/{max} HP (killed={killed})",
                            target.Name, target.Guid, hitDamage, target.Health, target.MaxHealth, killed);

                        // Route the kill to the zone (OnNpcKilled): starting zone resets the training dummy, Frostfang
                        // advances the encounter. Non-fatal hits go to OnNpcDamaged so the zone can react to HP
                        // thresholds (the Alpha flees at low health instead of dying).
                        if (killed)
                            player.Zone.OnNpcKilled(player, target);
                        else
                            player.Zone.OnNpcDamaged(player, target);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ability damage resolution failed.");
            }
        });
    }
}
