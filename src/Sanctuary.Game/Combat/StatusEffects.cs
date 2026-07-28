using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;

using Sanctuary.Core.IO;
using Sanctuary.Game.Entities;
using Sanctuary.Packet;

namespace Sanctuary.Game.Combat;

// Ported from the community "combat-v2" fork (CarterW24/Sanctuary), TRIMMED to the mechanical half only.
//
// The fork's version also drives a buff-bar timer ICON via ClientUpdatePacketAddEffectTag/EffectTag
// (InstanceId/EffectId/TypeId/Duration/Guid/CompositeEffectId/IconId/NameId/... named fields). That does
// NOT match our own EffectTag.cs: ours was rewritten 2026-07-25 from the client's REAL deserializer chain
// (IDA-confirmed, byte-exact 24-raw-field layout: Key/Unknown1..21/StartTimeDelta/EndTimeDelta), and the
// SEMANTIC mapping of those raw fields (which one is duration, icon, name, ...) is still unresolved -
// "needs live testing" per that investigation, never completed. The fork's named-field version is the
// same kind of pre-correction guess ours used to be. Guessing a field mapping here would send a
// structurally-wrong packet - worse than not shipping the buff-bar icon at all - so it's left out.
//
// What DOES ship, all backed by packets we've already ground-truthed elsewhere in this codebase:
//   - The real CharacterStatus bitfield (PlayerUpdatePacketUpdateCharacterState, RE'd 2026-07-03, matches
//     a real 04-01 capture) - this is what actually GATES the character (several bits halt the client's
//     own movement controller; ability-blocking is enforced server-side, see BlocksAbilities/IsSilenced).
//   - The composite-effect swirl on the affected character (AddEffectTagCompositeEffect/Remove, op35/41-42
//     - ground-truthed via the 04-01 heart-pickup capture, independent of EffectTag entirely).
//   - Poison damage-over-time ticks (HitPointModification, the same proven packet ability damage uses).
public enum StatusEffectKind
{
    Stun,
    Sleep,
    Silence,
    Root,
    Fear,
    Confuse,
    Freeze,
    Berserk,
    Poison,
    Knockback,
}

public static class StatusEffects
{
    // GROUND-TRUTHED 2026-07-27 against the wiki's own Combat page (the real "known detrimental effects"
    // list: Sleep, Silence, Stun, Knockback, Confuse, Poison - plus Root from its "orbs and spheres"
    // sentence) AND ClientItemDefinitions.json's CategoryId 14 item family (real named orbs/spheres/
    // grenades/balls - see CombatOrbAbilities). Stun/Sleep/Root/Confuse/Knockback FX ids below are REAL,
    // resolved from ActorCompositeEffectDefinitions.xml's dedicated "PFX_orb-explosion_<color>_cog_<name>"
    // family (16572-16576), matched by CombatOrbAbilities to the real sphere that inflicts each one.
    // Fear now has real supporting evidence too (CategoryId 14 has a "Scare Orb/Sphere" family) - FX still
    // unconfirmed. Silence and Berserk have NO supporting item evidence found yet (no "Silence"/"Berserk"
    // orb in the CategoryId 14 list) - kept since the wiki's broader list does name Silence, but flagged as
    // still needing its own source. Freeze has a "Frost Grenade" in the same category suggesting it's
    // real too, FX still a guess carried from the fork.
    private record Meta(CharacterStatus Flag, int FxId);

    private static readonly Dictionary<StatusEffectKind, Meta> _meta = new()
    {
        [StatusEffectKind.Stun] = new(CharacterStatus.IsStunned, 16574),      // PFX_orb-explosion_white_cog_stars-yellow (Flabbergast Sphere)
        [StatusEffectKind.Sleep] = new(CharacterStatus.IsAsleep, 16572),      // PFX_orb-explosion_blue_cog_sleeping-gas (Sleep Sphere)
        [StatusEffectKind.Silence] = new(CharacterStatus.IsSilenced, 14),     // unconfirmed - no matching orb found
        [StatusEffectKind.Root] = new(CharacterStatus.IsRooted, 16573),       // PFX_orb-explosion_green_cog_ooze (Unmoving Sphere)
        [StatusEffectKind.Fear] = new(CharacterStatus.IsAfraid, 0),           // real per "Scare Orb/Sphere" item family; FX unconfirmed
        [StatusEffectKind.Confuse] = new(CharacterStatus.IsConfused, 16576),  // PFX_orb-explosion_purple_cog_question-marks (Confusion Sphere)
        [StatusEffectKind.Freeze] = new(CharacterStatus.IsFrozen, 5337),      // real per "Frost Grenade" item; FX unconfirmed (fork's guess)
        [StatusEffectKind.Berserk] = new(CharacterStatus.IsBerserk, 0),       // unconfirmed - no matching orb found
        [StatusEffectKind.Poison] = new(CharacterStatus.None, 5220),
        [StatusEffectKind.Knockback] = new(CharacterStatus.IsKnockedBack, 16575), // PFX_orb-explosion_orange_cog_shockwave-yellow (Blast Sphere)
    };

    private const int PoisonTickMs = 2000;
    private const int PoisonTickDamage = 50;
    private const int PoisonHitFlashFxId = 15578;

    private sealed class ActiveEffect
    {
        public int TagId;
        public int Seq;
    }

    private sealed class TargetState
    {
        public CharacterStatus Baseline;
        public readonly Dictionary<StatusEffectKind, ActiveEffect> Effects = new();
    }

    private static readonly ConcurrentDictionary<ulong, TargetState> _targets = new();

    private static int _tagCounter = 900;

    public static bool TryParse(string name, out StatusEffectKind kind)
    {
        kind = name.ToLowerInvariant() switch
        {
            "stun" or "stunned" => StatusEffectKind.Stun,
            "sleep" or "asleep" => StatusEffectKind.Sleep,
            "silence" or "silenced" => StatusEffectKind.Silence,
            "root" or "rooted" or "snare" => StatusEffectKind.Root,
            "fear" or "afraid" => StatusEffectKind.Fear,
            "confuse" or "confused" => StatusEffectKind.Confuse,
            "freeze" or "frozen" or "ice" => StatusEffectKind.Freeze,
            "berserk" => StatusEffectKind.Berserk,
            "poison" or "poisoned" => StatusEffectKind.Poison,
            "knockback" or "knocked back" => StatusEffectKind.Knockback,
            _ => (StatusEffectKind)(-1),
        };
        return kind >= 0;
    }

    public static void Apply(IEntity target, StatusEffectKind kind, int durationMs,
        CharacterStatus baseline = CharacterStatus.None, Player? source = null)
    {
        var meta = _meta[kind];
        var state = _targets.GetOrAdd(target.Guid, _ => new TargetState());
        int tagId, seq;

        lock (state)
        {
            if (baseline != CharacterStatus.None)
                state.Baseline = baseline;

            if (!state.Effects.TryGetValue(kind, out var effect))
                state.Effects[kind] = effect = new ActiveEffect { TagId = ++_tagCounter };

            tagId = effect.TagId;
            seq = ++effect.Seq;
        }

        SendState(target, state);

        if (meta.FxId > 0)
        {
            Send(target, new PlayerUpdatePacketAddEffectTagCompositeEffect
            {
                Guid = target.Guid,
                TagId = tagId,
                CompositeEffectId = meta.FxId,
                SourceGuid = source?.Guid ?? target.Guid,
            });
        }

        if (kind == StatusEffectKind.Poison && target is Npc npc)
            RunPoisonTicks(npc, state, seq, durationMs, source);

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(durationMs);
                ExpireIfCurrent(target, kind, seq);
            }
            catch { }
        });
    }

    public static void Clear(IEntity target, StatusEffectKind kind)
    {
        if (_targets.TryGetValue(target.Guid, out var state))
        {
            int seq;
            lock (state)
                seq = state.Effects.TryGetValue(kind, out var e) ? e.Seq : -1;
            if (seq >= 0)
                ExpireIfCurrent(target, kind, seq);
        }
    }

    public static void ClearAll(IEntity target)
    {
        if (_targets.TryGetValue(target.Guid, out var state))
        {
            List<StatusEffectKind> kinds;
            lock (state)
                kinds = new List<StatusEffectKind>(state.Effects.Keys);
            foreach (var kind in kinds)
                Clear(target, kind);
        }
    }

    // Full action lock: can't swing/cast/use items at all.
    public static bool BlocksAbilities(ulong guid) =>
        HasAny(guid, StatusEffectKind.Stun, StatusEffectKind.Sleep, StatusEffectKind.Fear, StatusEffectKind.Freeze);

    // Spell/special lock only - a silenced character can still swing a basic melee attack.
    public static bool IsSilenced(ulong guid) => HasAny(guid, StatusEffectKind.Silence);

    public static bool IsImmobilized(ulong guid) =>
        HasAny(guid, StatusEffectKind.Stun, StatusEffectKind.Sleep, StatusEffectKind.Root, StatusEffectKind.Freeze);

    private static bool HasAny(ulong guid, params StatusEffectKind[] kinds)
    {
        if (!_targets.TryGetValue(guid, out var state))
            return false;
        lock (state)
        {
            foreach (var kind in kinds)
                if (state.Effects.ContainsKey(kind))
                    return true;
        }
        return false;
    }

    private static void ExpireIfCurrent(IEntity target, StatusEffectKind kind, int seq)
    {
        if (!_targets.TryGetValue(target.Guid, out var state))
            return;

        int tagId;
        lock (state)
        {
            if (!state.Effects.TryGetValue(kind, out var effect) || effect.Seq != seq)
                return;
            tagId = effect.TagId;
            state.Effects.Remove(kind);
        }

        SendState(target, state);

        if (_meta[kind].FxId > 0)
        {
            Send(target, new PlayerUpdatePacketRemoveEffectTagCompositeEffect
            {
                Guid = target.Guid,
                TagId = tagId,
            });
        }
    }

    private static void RunPoisonTicks(Npc npc, TargetState state, int seq, int durationMs, Player? source)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                for (var elapsed = PoisonTickMs; elapsed <= durationMs; elapsed += PoisonTickMs)
                {
                    await Task.Delay(PoisonTickMs);

                    lock (state)
                    {
                        if (!state.Effects.TryGetValue(StatusEffectKind.Poison, out var e) || e.Seq != seq)
                            return;
                    }

                    if (!npc.IsAlive || !npc.IsDamageable)
                        return;

                    npc.ApplyDamage(PoisonTickDamage);
                    foreach (var watcher in npc.VisiblePlayers.Values)
                    {
                        watcher.SendTunneled(new PlayerUpdatePacketHitPointModification
                        {
                            Guid = source?.Guid ?? npc.Guid,
                            Guid2 = npc.Guid,
                            Unknown = true,
                            Unknown2 = npc.MaxHealth,
                            Unknown3 = npc.Health,
                            Unknown4 = -PoisonTickDamage,
                        });
                        watcher.SendTunneled(new PlayerUpdatePacketPlayCompositeEffect
                        {
                            Guid = npc.Guid,
                            CompositeEffectId = PoisonHitFlashFxId,
                            Position = npc.Position,
                        });
                    }
                }
            }
            catch { }
        });
    }

    private static void SendState(IEntity target, TargetState state)
    {
        CharacterStatus flags;
        lock (state)
        {
            flags = state.Baseline;
            foreach (var kind in state.Effects.Keys)
                flags |= _meta[kind].Flag;
        }

        Send(target, new PlayerUpdatePacketUpdateCharacterState
        {
            Guid = target.Guid,
            Status = flags,
        });
    }

    private static void Send(IEntity target, ISerializablePacket packet)
    {
        if (target is Player player)
        {
            player.SendTunneled(packet);
            foreach (var watcher in player.VisiblePlayers.Values)
                watcher.SendTunneled(packet);
        }
        else
        {
            foreach (var watcher in target.VisiblePlayers.Values)
                watcher.SendTunneled(packet);
        }
    }
}
