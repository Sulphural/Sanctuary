using System;
using System.Collections.Concurrent;
using System.Numerics;
using System.Threading.Tasks;

using Sanctuary.Game.Entities;
using Sanctuary.Game.Zones;
using Sanctuary.Packet;
using Sanctuary.Packet.Common;

namespace Sanctuary.Game.Combat;

// The real retail "Power-ups" mechanic (user-supplied in-game tooltip, 2026-07-27): "items that drop off
// enemies during combat... used by pressing the number 3 key. You can only have one at a time, so use it
// if you want to be able to pick up another one." Five real kinds, verbatim from that tooltip:
//   Health   - big heart heals the whole group; little heart restores a small amount of your own health.
//   Energy   - replenishes energy.
//   Flame Wave  - instantly damages all enemies around you.
//   Earth Shard - damages all enemies in your area and stuns them for a short time.
//   Super Shield - increases your speed and makes you invulnerable for a short time.
//
// Health was ALREADY real, ground-truthed, working code before this file existed (FrostfangArenaZone /
// TormentedSpiritsArenaZone's SpawnHeart/CollectHearts - model 736 powerup_health_buff, +125 heal,
// looping heal-shower FX 15921, all from a real 04-01 capture + screen-recorded video). This file adds
// the other 4 kinds as a generic, shared system (works in ANY CombatEncounterZone, not just those two
// hand-built ones) and generalizes the drop/pickup/held-slot mechanics Health already proved out.
//
// FX ids for FlameWave/EarthShard/SuperShield are REAL - matched by name in our own
// ActorCompositeEffectDefinitions.xml against the community "combat-v2" fork's baked-in defaults
// (PFX_fire-ring_orange_cog_spiral_AOE / PFX_smoke-rocks_purple_cave-in / PFX_shield_swirl_blue_barrier_loop
// - all read as clean, plausible matches for their names). Everything else NOT sourced this way (icon ids,
// pickup model ids for the 4 non-Health kinds, drop chance, magnitudes, durations) is a flagged estimate,
// not confirmed - the fork's own animation ids for these were still runtime-tunable placeholders (never
// baked to a real value), so none of that was reusable.
public enum PowerupKind
{
    Health,
    Energy,
    FlameWave,
    EarthShard,
    SuperShield,
}

public static class PowerupSystem
{
    // CORRECTED 2026-07-27 (live feedback: "flame wave is showing the health power up model"): all 5 are
    // real, confirmed entries in our own Resources/Models.txt (was wrongly reusing 736 for all 4 non-Health
    // kinds - a flagged placeholder that turned out unnecessary, real ids existed all along).
    public const int HealthPickupModelId = 736;      // powerup_health_buff.adr
    public const int EnergyPickupModelId = 737;      // powerup_mana_buff.adr
    public const int FlameWavePickupModelId = 1949;  // powerup_flame_wave.adr
    public const int EarthShardPickupModelId = 1950; // powerup_quake.adr
    public const int SuperShieldPickupModelId = 1951; // powerup_super_shield.adr

    public const float PickupRange = 2.6f; // matches the proven Health pickup radius
    public const int PickupFxId = 15032;   // matches the proven Health pickup sparkle (op35/41-42 remove)

    // Icon/name ids for the held-slot (toolbar slot 2, the "3" key) - NOT independently verified; a
    // reasonable placeholder set, not sourced the way the FX ids below are.
    public const int HealthIconId = 26830;
    public const int EnergyIconId = 26831;
    public const int FlameWaveIconId = 26832;
    public const int EarthShardIconId = 26835;
    public const int SuperShieldIconId = 26838;
    public const int PowerupNameId = 5102385;

    // Real, name-matched against ActorCompositeEffectDefinitions.xml (see header comment).
    public const int FlameWaveFxId = 5591;   // PFX_fire-ring_orange_cog_spiral_AOE
    public const int EarthShardFxId = 5388;  // PFX_smoke-rocks_purple_cave-in
    public const int SuperShieldFxId = 5049; // PFX_shield_swirl_blue_barrier_loop

    // Magnitudes/durations/radius/drop-chance - none of these are sourced anywhere (no ability-definitions
    // numeric table exists, and the tooltip has no numbers), sized to feel like a genuine mid-fight
    // "clutch" cooldown rather than a trivial win-button. Flagged the same as every other unsourced number
    // in this codebase.
    public const int FlameWaveDamage = 800;
    public const int EarthShardDamage = 600;
    public const int EarthShardStunMs = 4000;
    public const float AoeUseRadius = 10f;
    public const int SuperShieldDurationMs = 8000;
    public const float SuperShieldSpeedMult = 1.4f;
    public const int BurstFxHoldMs = 2500; // Flame Wave/Earth Shard's attach hold - long enough for the burst clip, not a real buff
    public const int DropPercent = 12; // matches Health's own already-proven HeartDropPercent

    private static readonly (PowerupKind Kind, int Weight)[] _dropTable =
    [
        (PowerupKind.Health, 40),
        (PowerupKind.Energy, 20),
        (PowerupKind.FlameWave, 15),
        (PowerupKind.EarthShard, 15),
        (PowerupKind.SuperShield, 10),
    ];

    private static readonly ConcurrentDictionary<ulong, PowerupKind> _held = new();

    // Unique effect-tag ids for the Super Shield looping attach (op35/41-42) - start high to stay clear of
    // the zones' own heal-shower tag ranges (see FrostfangArenaZone._healTagCounter and similar).
    private static int _shieldTagCounter = 8000;

    // Cross-layer bridge: the real energy pool lives privately inside
    // AbilityPacketClientRequestStartAbilityHandler (Gateway layer, can't be referenced from here). Wired
    // once at startup so a picked-up Energy powerup can actually refill it.
    public static Action<Player>? RequestEnergyRefill;

    // Same bridge, but restores a partial amount instead of a full refill - used by trait procs (Wizard's
    // Arcane Flare, Archer's Lucky Shot) that give back a little energy on a crit/landed hit.
    public static Action<Player, int>? RestoreEnergy;

    public static PowerupKind RollDropKind()
    {
        var total = 0;
        foreach (var (_, weight) in _dropTable) total += weight;
        var roll = Random.Shared.Next(total);
        foreach (var (kind, weight) in _dropTable)
        {
            if (roll < weight)
                return kind;
            roll -= weight;
        }
        return PowerupKind.Health;
    }

    public static bool IsHolding(ulong playerGuid) => _held.ContainsKey(playerGuid);

    // Real, ground-truthed (FrostfangArenaZone.HeartHeal/HealShowerFxId/HealShowerMs - the "little heart...
    // restore a small amount of your own health" case, from a real 04-01 capture: composite 15921 =
    // PFX_magic-heal_red_head_shower_lg_loop_raised, the looping over-head heal shower + trail, attached
    // via an effect tag (op35/41) and held ~15s (op35/42 stop) - see the header comment on
    // FrostfangArenaZone.CollectHearts for the full ground truth). The "big heart... heals everyone in
    // your group" variant isn't implemented here yet - this generic drop system always grants the
    // self-only heal.
    // PERCENT-based (2026-07-27 fix, live feedback: "make sure health potions and health powerups scale
    // for all players, some players have more health than others") - the video's real "+125" number is
    // tied to whatever max HP that specific captured player had (never actually confirmed), so treat it
    // as this class of pickup's real proportion (5%) rather than a flat amount that favors low-HP players.
    public const float HealthHealFraction = 0.05f;
    public const int HealShowerFxId = 15921;
    public const int HealShowerMs = 15000;
    private static int _healTagCounter = 9000; // clear of PowerupSystem's own _shieldTagCounter range

    // Grants a powerup: Health/Energy apply INSTANTLY on pickup (matches the tooltip's own wording - only
    // Flame Wave/Earth Shard/Super Shield read as something you'd deliberately "use" later); the other 3
    // are HELD (pinned to toolbar slot 2 / the "3" key) until the player presses it or picks up nothing
    // else (only one held at a time, per the tooltip). Can't pick up a new HELD one while already holding
    // one (per the tooltip) - callers should check IsHolding before offering a held-type pickup.
    // Names for the pickup-confirmation toast (see below) - live feedback 2026-07-27: pickups were
    // completely silent, which made a real grant failure indistinguishable from nothing having dropped at
    // all ("sometimes im not receiving the powerup... some of them dont do anything"). Every successful
    // Grant now says so explicitly, whether instant (Health/Energy) or held (the other 3).
    private static string DisplayName(PowerupKind kind) => kind switch
    {
        PowerupKind.Health => "Health power-up",
        PowerupKind.Energy => "Energy power-up",
        PowerupKind.FlameWave => "Flame Wave power-up",
        PowerupKind.EarthShard => "Earth Shard power-up",
        _ => "Super Shield power-up",
    };

    private static void SendPickupText(Player player, string message) =>
        player.SendTunneled(new ChatPacketDebugChat { Message = $"<font color='#0000FF'>{message}</font>", PrintToChat = true });

    // Returns whether the powerup was actually granted - false only for the "already holding a held-type"
    // rejection (see the default case below). CORRECTED 2026-07-28 (live feedback: "not allowing me to
    // pick up powerups... some types work, others don't") - CombatEncounterZone's pickup loop used to
    // pre-check IsHolding itself and silently `continue` BEFORE ever calling Grant, so the rejection
    // message below (already written, already correct) never actually fired - the player just saw the
    // pickup do nothing. Grant is now the single source of truth for this decision; the caller uses the
    // return value instead of duplicating (and silencing) the check.
    public static bool Grant(Player player, PowerupKind kind, IResourceManager resources)
    {
        switch (kind)
        {
            case PowerupKind.Energy:
                RequestEnergyRefill?.Invoke(player);
                SendPickupText(player, "You receive an Energy power-up! Energy refilled.");
                return true;
            case PowerupKind.Health:
                // Live feedback 2026-07-27: "i dont see my health moving when ... getting health powerup"
                // - the packet below is purely the floating "+N" combat text, it never touched
                // CurrentHitpoints. Player.Heal is the real HP-bar update (same packets TakeDamage/RegenTick
                // use); healedAmount also lets the floating number reflect what actually landed near max HP.
                var healedAmount = player.HealPercent(HealthHealFraction);
                var maxHpStat = player.Stats.TryGetValue(CharacterStatId.MaxHealth, out var mh) ? mh.Int : 0;
                player.SendTunneledToVisible(new PlayerUpdatePacketHitPointModification
                {
                    Guid = player.Guid,
                    Guid2 = player.Guid,
                    Unknown = true,
                    Unknown2 = maxHpStat,
                    Unknown3 = player.CurrentHitpoints,
                    Unknown4 = healedAmount,
                }, sendToSelf: true);
                SendPickupText(player, $"You receive a Health power-up! +{healedAmount} health.");

                // The looping heal shower (see the constants' header comment) - live feedback 2026-07-27:
                // "health powerup should show the effects on player (hearts effect)". Same tag-based
                // attach/remove pattern as the proven FrostfangArenaZone.CollectHearts.
                var healTagId = System.Threading.Interlocked.Increment(ref _healTagCounter);
                player.SendTunneledToVisible(new PlayerUpdatePacketAddEffectTagCompositeEffect
                {
                    Guid = player.Guid,
                    TagId = healTagId,
                    CompositeEffectId = HealShowerFxId,
                    SourceGuid = player.Guid,
                }, sendToSelf: true);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(HealShowerMs);
                        player.SendTunneledToVisible(new PlayerUpdatePacketRemoveEffectTagCompositeEffect
                        {
                            Guid = player.Guid,
                            TagId = healTagId,
                        }, sendToSelf: true);
                    }
                    catch { }
                });
                return true;
            default:
                if (IsHolding(player.Guid))
                {
                    // Explains an otherwise-silent no-op: the tooltip's own rule ("you can only have one at
                    // a time, so use it if you want to be able to pick up another one") - without this the
                    // pickup just does nothing with no indication why.
                    SendPickupText(player, "You're already holding a power-up - use it before picking up another.");
                    return false;
                }
                _held[player.Guid] = kind;
                JobWeaponAbilities.SendToolbarWithPowerup(player, resources);
                SendPickupText(player, $"You receive a {DisplayName(kind)}! Press 3 to use it.");
                return true;
        }
    }

    public static AbilityPacketSetDefinition.Slot? MakeHeldSlot(ulong playerGuid)
    {
        if (!_held.TryGetValue(playerGuid, out var kind))
            return null;

        var iconId = kind switch
        {
            PowerupKind.FlameWave => FlameWaveIconId,
            PowerupKind.EarthShard => EarthShardIconId,
            _ => SuperShieldIconId,
        };

        return new AbilityPacketSetDefinition.Slot
        {
            Type = 3,
            ManaCost = 0,
            IconId = iconId,
            NameId = PowerupNameId,
            AbilityDefinitionId = 0,
        };
    }

    // Consumes the held powerup and applies its real effect. Returns false if nothing is held.
    public static bool TryUse(Player player, IResourceManager resources)
    {
        if (!_held.TryRemove(player.Guid, out var kind))
            return false;

        JobWeaponAbilities.SendToolbarWithPowerup(player, resources); // clear the slot-2 icon immediately

        var (fxId, damage, stunMs) = kind switch
        {
            PowerupKind.FlameWave => (FlameWaveFxId, FlameWaveDamage, 0),
            PowerupKind.EarthShard => (EarthShardFxId, EarthShardDamage, EarthShardStunMs),
            _ => (SuperShieldFxId, 0, 0),
        };

        if (kind == PowerupKind.SuperShield)
        {
            // CORRECTED 2026-07-27 (live feedback: "shield powerup effect doesn't seem to be staying on my
            // character.. just stays in one place"): PlayCompositeEffect is a ONE-SHOT effect at a fixed
            // WORLD position - it never follows anything, which is fine for Flame Wave/Earth Shard's
            // instant burst but wrong for a looping barrier meant to ride the player around for 8s. Swap to
            // the same tag-based ATTACH mechanism the proven Health heal-shower already uses (op35/41 add,
            // sourced+targeted at the player's own guid so it rides them, op35/42 remove after the duration).
            var tagId = System.Threading.Interlocked.Increment(ref _shieldTagCounter);
            player.SendTunneledToVisible(new PlayerUpdatePacketAddEffectTagCompositeEffect
            {
                Guid = player.Guid,
                TagId = tagId,
                CompositeEffectId = fxId,
                SourceGuid = player.Guid,
            }, sendToSelf: true);

            player.Invulnerable = true;

            // Real speed stat (CharacterStatId.MaxMovementSpeed - the same one Player.cs's own stat-update
            // path uses to drive PlayerUpdatePacketExpectedSpeed), sped up then reverted to the player's
            // OWN base value (not a hardcoded constant) so this doesn't fight whatever their real
            // movement speed is.
            var baseSpeed = player.Stats.TryGetValue(Sanctuary.Packet.Common.CharacterStatId.MaxMovementSpeed, out var speedStat)
                ? speedStat.Float
                : 6f;
            player.SendTunneledToVisible(new PlayerUpdatePacketExpectedSpeed
            {
                Guid = player.Guid,
                ExpectedSpeed = baseSpeed * SuperShieldSpeedMult,
            }, sendToSelf: true);

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(SuperShieldDurationMs);
                    player.Invulnerable = false;
                    player.SendTunneledToVisible(new PlayerUpdatePacketExpectedSpeed
                    {
                        Guid = player.Guid,
                        ExpectedSpeed = baseSpeed,
                    }, sendToSelf: true);
                    player.SendTunneledToVisible(new PlayerUpdatePacketRemoveEffectTagCompositeEffect
                    {
                        Guid = player.Guid,
                        TagId = tagId,
                    }, sendToSelf: true);
                }
                catch { }
            });
        }
        else
        {
            // CORRECTED 2026-07-27 (live feedback: "flame wave effects should follow the player" - same
            // bug class as the earlier Super Shield fix): PlayCompositeEffect is a ONE-SHOT effect at a
            // fixed WORLD position, so if the burst's own animation (a "fire-ring spiral"/"quake" clip)
            // takes more than an instant to play out and the player moves during it, the effect visibly
            // detaches and gets left behind. Same tag-based ATTACH mechanism as Shield, just a much
            // shorter hold (long enough for the burst animation, not a real buff duration).
            var tagId = System.Threading.Interlocked.Increment(ref _shieldTagCounter);
            player.SendTunneledToVisible(new PlayerUpdatePacketAddEffectTagCompositeEffect
            {
                Guid = player.Guid,
                TagId = tagId,
                CompositeEffectId = fxId,
                SourceGuid = player.Guid,
            }, sendToSelf: true);

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(BurstFxHoldMs);
                    player.SendTunneledToVisible(new PlayerUpdatePacketRemoveEffectTagCompositeEffect
                    {
                        Guid = player.Guid,
                        TagId = tagId,
                    }, sendToSelf: true);
                }
                catch { }
            });
        }

        if (damage > 0 && player.Zone is BaseZone zone)
        {
            foreach (var npc in zone.Npcs)
            {
                if (!npc.IsAlive || !npc.IsDamageable)
                    continue;

                var dx = npc.Position.X - player.Position.X;
                var dz = npc.Position.Z - player.Position.Z;
                if (dx * dx + dz * dz > AoeUseRadius * AoeUseRadius)
                    continue;

                var killed = npc.ApplyDamage(damage);
                player.SendTunneledToVisible(new PlayerUpdatePacketHitPointModification
                {
                    Guid = player.Guid,
                    Guid2 = npc.Guid,
                    Unknown = true,
                    Unknown2 = npc.MaxHealth,
                    Unknown3 = npc.Health,
                    Unknown4 = -damage,
                }, sendToSelf: true);

                if (stunMs > 0)
                    StatusEffects.Apply(npc, StatusEffectKind.Stun, stunMs, source: player);

                if (killed)
                    player.Zone.OnNpcKilled(player, npc);
                else
                    player.Zone.OnNpcDamaged(player, npc);
            }
        }

        return true;
    }
}
