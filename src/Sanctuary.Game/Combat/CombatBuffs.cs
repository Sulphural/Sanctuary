using System;
using System.Collections.Concurrent;

namespace Sanctuary.Game.Combat;

// Ported from the community "combat-v2" fork (CarterW24/Sanctuary) — a self-contained % damage-multiplier
// registry, no packet/wire dependency at all (pure server-side state), so nothing here carries the same
// risk as the fork's EffectTag-based buff-BAR icon (see StatusEffects.cs's header comment for why that
// part was NOT ported). Trimmed to just the damage-buff registry actually used (the fork's
// EnergyRefillRequested/IsEnergyFull hooks existed only for its Frostfang-specific HeldPowerupProbe
// minigame, which we don't have).
public static class CombatBuffs
{
    private static readonly ConcurrentDictionary<ulong, (int Pct, long UntilTicks)> _damage = new();

    public static void AddDamageBuff(ulong playerGuid, int multiplierPct, int durationMs) =>
        _damage[playerGuid] = (multiplierPct, Environment.TickCount64 + durationMs);

    public static int ApplyDamage(ulong playerGuid, int damage)
    {
        if (!_damage.TryGetValue(playerGuid, out var buff))
            return damage;

        if (Environment.TickCount64 >= buff.UntilTicks)
        {
            _damage.TryRemove(playerGuid, out _);
            return damage;
        }

        return damage * buff.Pct / 100;
    }

    // Separate registry for INCOMING damage reduction (Medic's Immunize: "makes you and your group
    // invincible") - a mirror of the outgoing-damage registry above, keyed the same way, but consumed from
    // Player.TakeDamage instead of the ability-cast damage pipeline. Kept as its own dictionary rather than
    // reusing _damage since a player can be simultaneously buffed on offense (e.g. Vitamins) and defense
    // (Immunize) with different percentages/durations.
    private static readonly ConcurrentDictionary<ulong, (int ReductionPct, long UntilTicks)> _damageTaken = new();

    public static void AddDamageReductionBuff(ulong playerGuid, int reductionPct, int durationMs) =>
        _damageTaken[playerGuid] = (reductionPct, Environment.TickCount64 + durationMs);

    public static int ReduceIncomingDamage(ulong playerGuid, int damage)
    {
        if (!_damageTaken.TryGetValue(playerGuid, out var buff))
            return damage;

        if (Environment.TickCount64 >= buff.UntilTicks)
        {
            _damageTaken.TryRemove(playerGuid, out _);
            return damage;
        }

        var reduced = damage * (100 - buff.ReductionPct) / 100;
        return Math.Max(0, reduced);
    }
}
