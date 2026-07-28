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
}
