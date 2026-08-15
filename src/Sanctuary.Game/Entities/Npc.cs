using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

using Sanctuary.Game.Interactions;
using Sanctuary.Game.Zones;
using Sanctuary.Packet;
using Sanctuary.Packet.Common;

namespace Sanctuary.Game.Entities;

public class Npc : IEntity
{
    public ulong Guid { get; init; }

    public Vector4 Position { get; private set; }
    public Quaternion Rotation { get; private set; }

    public bool Visible { get; set; }
    public ulong CurrentHouseGuid { get; set; }

    public IZone Zone { get; set; }
    public ZoneTile ZoneTile { get; protected set; } = ZoneTile.Empty;
    public ConcurrentDictionary<ulong, Npc> VisibleNpcs { get; } = [];
    public ConcurrentDictionary<ulong, Player> VisiblePlayers { get; } = [];

    public int NameId { get; set; }
    public string? Name { get; set; }

    // Nameplate text color (AddNpc.NameColor; 0 = client default). Bosses in the reference
    // video render RED names — first candidate mechanism alongside op32/sub9 EnableBossDisplay.
    public int NameColor { get; set; }

    // A looping composite effect attached to this npc, sent to each viewer right after its AddNpc.
    //
    // ★ Sent per-viewer for the same reason ClientDisposition is: doing it once at spawn only reaches players
    // who were already standing there, and anyone who walks up later would see a bare prop.
    public int AttachedEffectId { get; set; }
    public int AttachedEffectTagId { get; set; }

    // Whether this npc gets the combat NpcRelevance push (the attack cursor) when a player first sees it.
    //
    // ★ Relevance is what marks an npc as a TARGET, and ProjectileNpc already records that "a targetable NPC
    // draws the health bar". So for a damageable npc that must NOT show a bar, this is the switch - the
    // health packets were only ever half of it.
    public bool SendCombatRelevance { get; set; } = true;

    // Per-npc nameplate DISPOSITION override, applied to every viewer as they see the npc.
    //
    // ★ Needed because AddNpc's own Disposition field is DISCARDED client-side - the apply takes it from a
    // client-global flag instead - so the only way to colour one npc's plate is op35/28 AFTER its AddNpc.
    // Sending that once at spawn only reaches players who were already nearby; anyone who walks up later
    // gets the AddNpc from the tile-visibility sweep and would keep the default colour. Carrying it on the
    // npc lets OnAddVisibleNpcs send it alongside every AddNpc, whenever that happens.
    //
    // null = leave the client's default. 0 hostile (red), 1 neutral / 2 ally (both bluish 0xFF6699CC).
    public int? ClientDisposition { get; set; }
    public int SubTextNameId { get; set; }
    // HIDE the overhead nameplate. LIVE-PROVEN 2026-07-03 (builds 12 vs 13): true hides,
    // false shows — upstream's name is correct; the IDA "m_bShowNamePlate" annotation is wrong.
    public bool HideNamePlate { get; set; }

    // AddNpc ActiveProfile. MUST be non-default (we use 1) for the nameplate color to
    // resolve from disposition (red hostiles) — see the notes on Disposition and in
    // GetAddNpcPacket. 0 = client keeps the ctor-baked ally blue. LIVE-CONFIRMED 2026-07-03.
    // POLICY: leave 0 (default) on normal NPCs; set non-zero ONLY on mobs/bosses (hostiles).
    public int ActiveProfile { get; set; }
    public int NameplateImageId { get; set; }
    public float VerticalOffset { get; set; }

    // Overhead name text SCALE. RE'd 2026-07-03: ProxiedCharacter::Process @0x973200 does
    // `if (m_fNameScale != 0) Display_EliteNameScale = m_fNameScale` — so this AddNpc field directly
    // sets the name text size. 0 = client default (~normal); >1 = bigger letters (the video's boss).
    public float NameScale { get; set; }

    public int ModelId { get; set; }
    public int TerrainObjectId { get; set; }

    public string? TextureAlias { get; set; }
    public string? TintAlias { get; set; }
    public int TintId { get; set; }

    public float Scale { get; set; }

    // 0 - Hostile
    // 1 - Neutral
    // 2 - Ally
    // HOW DISPOSITION DRIVES THE NAMEPLATE COLOR (RE'd + live-proven 2026-07-03):
    // the client colors overhead names in ONE place, the resolver ProxiedCharacter::sub_966460.
    // When NameColor == 0 it picks the color from disposition: 0 (hostile) = RED (0xFFFF0000),
    // anything else = the bluish NPC default (0xFF6699CC). BUT the resolver only runs from the
    // ProxiedCharacter ctor / SetProfileId / SetIsMember — and the ctor runs BEFORE the packet's
    // disposition is applied (ctor default = 2 Ally = blue). There is no post-spawn recolor packet
    // (op35/sub28 sets disposition but never repaints), so a hostile only renders red if the
    // resolver RE-runs after the AddNpc apply writes disposition — which is what a non-default
    // ActiveProfile triggers. In short: red name = Disposition 0 + NameColor 0
    // + ActiveProfile != 0, all at spawn time.
    public int Disposition { get; set; } = 1;

    public Action<Player>? InteractAction { get; set; }

    // Contributors to this NPC's radial interaction menu. Each is asked, at interact time, for the
    // options it can offer THIS player right now - a vendor always has one (open the shop), the quest
    // manager has one per quest it could start or advance here, and either can have none.
    //
    // Registering a provider is how an NPC becomes able to do two things at once; before this, a
    // second `InteractAction = ...` silently replaced the first (a vendor who also gave a quest lost
    // its shop). NPCs with a single fixed job keep using InteractAction and are unaffected.
    public List<Func<Player, IEnumerable<NpcInteractionOption>>> InteractionProviders { get; } = [];

    // Does clicking this NPC do anything at all? Callers that used to test `InteractAction is not null`
    // must use this instead: a vendor or quest NPC now carries providers and leaves InteractAction null,
    // and testing the delegate alone would silently drop them.
    public bool HasInteraction => InteractAction is not null || InteractionProviders.Count > 0;
    public Action? UpdateEverySecondAction { get; set; }

    // Non-zero = show a combat-encounter "Battle Starter" badge over this NPC's head (op35/sub10
    // AddNotifications, Type 3 = combat category = red crossed-swords + red minimap dot). 24 is the live
    // img-24 combat-encounter badge art. Sent per-player when the NPC comes into view (OnAddVisibleNpcs).
    public virtual int CombatEncounterBadgeImageId => 0;

    // COMBAT WIP: server-side health so abilities can damage/kill this NPC.
    // MaxHealth == 0 means "not damageable" (no health bar). See docs/STATUS.md.
    public int MaxHealth { get; set; }
    public int Health { get; set; }

    // Render a nameplate health bar (maps to AddNpc.Unknown41).
    public bool ShowHealthBar { get; set; }

    // Show the red crossed-swords combat-encounter badge (img-24, Type=3) above this NPC's head + a red
    // minimap dot, whenever it becomes visible to a player - see BaseZone's reveal logic. Used for dungeon
    // entrances / "Battle Starter" encounter NPCs (matches the Frostfang Growler's SendGrowlerBadge, the
    // original one-off implementation of this same badge before it was made generic here).
    public bool ShowCombatBadge { get; set; }

    public bool IsHostile => Disposition == 0;
    public bool IsDamageable => MaxHealth > 0 && !Invulnerable;
    public bool IsAlive => MaxHealth == 0 || Health > 0;

    // Damage immunity toggle (e.g. the defeated Frostfang Alpha while he runs off —
    // the reference video shows he can't be hit once he breaks).
    public bool Invulnerable { get; set; }

    // When set, Dispose()'s remove packet is the GRACEFUL form with these live-wire params
    // (op35/sub3 RemovePlayerGracefully). GROUND TRUTH (04-01 capture): a dying pack wolf is removed
    // with (Animate=true, Delay=2000, fx 5017, Duration=1000) and NOTHING else — Animate makes the
    // client play the model's own death clip, then the composite effect + despawn after Delay ms.
    // The defeated Alpha uses Delay=10000 (he visibly runs off for 10s instead). Null = abrupt remove.
    public (bool Animate, int Delay, int EffectDelay, int EffectId, int Duration)? GracefulRemoval { get; set; }

    private readonly object _damageLock = new();

    // Apply damage; returns true if THIS hit landed the kill (exactly once). Thread-safe: an
    // archer fires ~every 150ms and each shot resolves its damage on a delayed task, so several in-flight
    // shots land almost together as the mob dies. Without the lock they would each read Health > 0, each
    // subtract, and EACH return true — firing OnNpcKilled 3-4x for one death (the level-up/XP effect
    // playing several times, and multiple graceful-removes that jam the client's combat state so a bow
    // can't re-fire). The lock guarantees a single caller crosses 0 and returns true.
    // Only a SNOWBALL can hurt this npc - swords, bows and abilities do nothing. The Snow Days snowmen are
    // built this way on purpose: the event is a snowball fight, so a passing archer can't shortcut it, and
    // wandering into it on a combat job can't accidentally trivialise the boss.
    public bool SnowballOnly { get; set; }

    // This npc belongs to a timed world EVENT whose spawner owns its entire lifecycle (the Snowmen Invaders
    // wave, its boss, its chest, its announcer). The one hard guarantee it buys: such an npc can NEVER fall
    // through to the generic world-enemy "respawn at its post" path when killed.
    //
    // It is a flag on the NPC rather than a lookup in the event's lists because that fallthrough is exactly
    // what happens when the two disagree - an npc the event has lost track of gets respawned forever at its
    // home position, as a normal hostile, with none of the event's rules. The npc always knows what it is;
    // the bookkeeping might not.
    public bool IsEventSpawn { get; set; }

    public bool ApplyDamage(int amount, bool fromSnowball = false)
    {
        // Invulnerable is honoured HERE, not just in IsDamageable. Callers that pick targets by RANGE
        // rather than by client targeting (the snowball) never consult IsDamageable, so without this an
        // npc marked un-hittable could still be killed - which is the whole point of the flag for both of
        // its users: the fleeing Frostfang Alpha, and the Abominable Snowman standing over the tree he has
        // already won, whose death at that moment would run the "you beat him" ending on top of the
        // failure in progress.
        if (Invulnerable)
            return false;

        if (SnowballOnly && !fromSnowball)
            return false;

        lock (_damageLock)
        {
            if (!IsAlive)
                return false;

            // Sleep breaks on a hit (real wiki data, freerealms.fandom.com/wiki/Sleep_Orb: "hitting your
            // target will wake them") - centralized here since every damage source (basic attacks,
            // specials, combat orbs, power-up AoE bursts) already funnels through this one method.
            if (amount > 0)
                Sanctuary.Game.Combat.StatusEffects.Clear(this, Sanctuary.Game.Combat.StatusEffectKind.Sleep);

            Health -= amount;

            if (Health <= 0)
            {
                Health = 0;
                return true;
            }

            return false;
        }
    }

    public int Animation { get; set; } = 1;

    // Locomotion animation group ids. -1 = "use the model's own clips" — the live 2014 server sends
    // -1 on EVERY NPC (370/370 AddNpc packets in the 2014-03-25 capture). 0 or a guessed id replaces
    // the model's run clip with an invalid one and the actor slides un-animated.
    public int WalkAnimId { get; set; } = -1;
    public int RunAnimId { get; set; } = -1;
    public int StandAnimId { get; set; } = -1;

    public int CompositeEffectId { get; set; }

    // World units - the interact/click distance, also sent to the client in the AddNpc packet.
    public int InteractRange { get; set; } = 5;
    public bool IsInteractable { get; set; } = true;
    public bool CollisionEnabled { get; set; }

    // MOVEMENT (client OnPlayerUpdatePosition @0x90DE90, RE'd 2026-07-02): the client applies op125
    // position updates ONLY when the actor's MovementType is 1 (CONTROLLER: ClientMovementManager
    // interpolates to the sent position at ExpectedSpeed) or 2 (PHYSICS: network-player style with
    // gravity/fall states). Type 0 = static scenery — updates are parsed then silently DROPPED
    // (that was the "wolves frozen at spawn in the treetops" bug).
    public int MovementType { get; set; }

    // Movement speed baked into AddNpc (feeds the client's ExpectedSpeed for this actor —
    // at 0 a CONTROLLER/PHYSICS actor has no speed to move with).
    public float Speed { get; set; }

    // Rider gate: OnPlayerUpdatePosition ignores actors whose rider != the invalid-guid
    // sentinel (0xFFFFFFFFFFFFFFFF). Send the sentinel for AI NPCs ("no rider").
    public ulong RiderGuid { get; set; }

    // AddNpc bool #38. GROUND TRUTH (2014-03-25 capture): set to 1 on every red-name attackable camp
    // hostile (nameId 440711/440712, disp 0, nameColor FFFF0000) and 0 on every friendly — the
    // "render as enemy" status flag that goes with the red name.
    public bool EnemyStatus { get; set; }

    public int AreaDefinitionId { get; set; }

    public int ImageSetId { get; set; }

    public byte CursorId { get; set; }

    public List<int> VendorItems { get; set; } = [];
    public List<int> VendorCosts { get; set; } = [];
    public List<int> VendorBundles { get; set; } = [];
    public IResourceManager? ResourceManager { get; set; }
    public int NotificationImageSetId { get; set; }
    public int Unknown68 { get; set; }

    public List<CharacterAttachmentData> Attachments { get; set; } = [];

    public bool Static { get; set; }

    public Npc(IZone zone)
    {
        Zone = zone;
    }

    #region Events

    public void OnInteract(Player player)
    {
        var options = BuildInteractionOptions(player);

        // ONLY a genuine choice gets the menu. One option runs straight away - putting a single-entry
        // ring on screen would make every vendor and quest giver cost an extra click.
        switch (options.Count)
        {
            case > 1:
                player.SendInteractionMenu(this, options);
                return;

            case 1:
                options[0].Invoke(player);
                return;

            default:
                InteractAction?.Invoke(player);
                return;
        }
    }

    public List<NpcInteractionOption> BuildInteractionOptions(Player player)
    {
        var options = new List<NpcInteractionOption>();

        foreach (var provider in InteractionProviders)
            options.AddRange(provider(player));

        return options;
    }

    public virtual void OnAddVisibleNpcs(params IEnumerable<Npc> npcs)
    {
        foreach (var npc in npcs)
            VisibleNpcs.TryAdd(npc.Guid, npc);
    }

    public virtual void OnAddVisiblePlayers(params IEnumerable<Player> players)
    {
        foreach (var player in players)
            VisiblePlayers.TryAdd(player.Guid, player);
    }

    // ---- Ambient greeting bubbles ----
    // Localized Global.Text ids this NPC greets with (real retail lines). Null/empty = silent, which is
    // every enemy and prop. Driven by BaseZone.UpdateAmbientChatter, not the visibility hook.
    public int[]? AmbientLineIds { get; set; }

    // "Walked up to them" distance, not tile-visibility distance.
    public const float AmbientGreetRange = 18f;
    public const float AmbientGreetRangeSquared = AmbientGreetRange * AmbientGreetRange;

    // Per-npc so an event mob can be quieter than a town greeter. The default is the town-greeter pace.
    public int AmbientGreetCooldownMs { get; set; } = 25_000;
    private long _nextAmbientGreetTicks;

    // Chance (percent) that an eligible bark actually fires. 100 = every time the cooldown is up. Lower it
    // for a crowd: sixteen snowmen all barking on the same cooldown is a wall of noise.
    public int AmbientGreetChancePercent { get; set; } = 100;

    public void TryAmbientGreet()
    {
        if (AmbientLineIds is null || AmbientLineIds.Length == 0)
            return;

        var now = Environment.TickCount64;
        if (now < _nextAmbientGreetTicks)
            return;

        _nextAmbientGreetTicks = now + AmbientGreetCooldownMs;

        if (AmbientGreetChancePercent < 100 && Random.Shared.Next(100) >= AmbientGreetChancePercent)
            return;

        // IsChatLogged=false: bubble over the head, nothing in the chat log.
        SayStringId(AmbientLineIds[Random.Shared.Next(AmbientLineIds.Length)]);
    }

    // Speak a LOCALIZED line as an overhead bubble. This is the retail path: the client's chat handler takes
    // an isChatLogged flag straight off this packet, so IsChatLogged=false gives a bubble with NO chat-log
    // line (verified: Ui.ShowChatBubble native c1ec70 draws for NPC guids just like player guids).
    public void SayStringId(int stringId, bool logged = false)
    {
        if (stringId <= 0 || VisiblePlayers.IsEmpty)
            return;

        var bubble = new ChatPacketFromStringId
        {
            SpeakerGuid = Guid,
            StringId = stringId,
            IsEmote = false,
            IsChatLogged = logged,
            OwnerGuid = Guid,
        };

        foreach (var player in VisiblePlayers.Values)
            player.SendTunneled(bubble);
    }

    public virtual void OnRemoveVisibleNpcs(params IEnumerable<Npc> npcs)
    {
        foreach (var npc in npcs)
            VisibleNpcs.TryRemove(npc.Guid, out _);
    }

    public virtual void OnRemoveVisiblePlayers(params IEnumerable<Player> players)
    {
        foreach (var player in players)
            VisiblePlayers.TryRemove(player.Guid, out _);
    }

    #endregion

    #region Update

    public virtual void UpdateEveryTick()
    {
    }

    public virtual void UpdateEverySecond()
    {
        UpdateEverySecondAction?.Invoke();
    }

    public void UpdatePosition(Vector4 position, Quaternion rotation, bool updateZoneArea = true)
    {
        Position = position;
        Rotation = rotation;

        if (Visible)
        {
            UpdateZoneTile();
        }
    }

    public virtual void TeleportToZone(IZone zone, Vector4 position, Quaternion rotation)
    {
    }

    public void UpdateZoneTile()
    {
        var newZoneTile = Zone.GetTileFromPosition(Position);

        if (newZoneTile == ZoneTile)
            return;

        Zone.UpdateEntityZoneTile(this, ZoneTile, newZoneTile);

        ZoneTile = newZoneTile;
    }

    #endregion

    public virtual PlayerUpdatePacketAddNpc GetAddNpcPacket()
    {
        var packet = new PlayerUpdatePacketAddNpc
        {
            Guid = Guid,

            NameId = NameId,

            ModelId = ModelId,

            Unknown = default,

            TextureAlias = TextureAlias,
            TintAlias = TintAlias,

            TintId = TintId,

            Scale = Scale,

            Position = Position,
            Rotation = Rotation,

            Attachments = Attachments,
            HasAttachments = Attachments.Count > 0,

            Disposition = Disposition,

            Animation = Animation,

            Unknown16 = default,
            VerticalOffset = VerticalOffset,

            CompositeEffectId = CompositeEffectId,

            WieldType = default,

            Name = Name,

            HideNamePlate = HideNamePlate,

            Unknown22 = default,
            Unknown23 = default,
            Unknown24 = default,

            TerrainObjectId = TerrainObjectId,

            Speed = Speed,

            Unknown28 = default,

            InteractRange = InteractRange,

            WalkAnimId = WalkAnimId, // Walk GroupAnimId
            RunAnimId = RunAnimId, // Sprint GroupAnimId
            StandAnimId = StandAnimId, // Idle GroupAnimId

            Unknown33 = default,
            Unknown34 = default,

            SubTextNameId = SubTextNameId,

            Unknown36 = default, // AnimationEvent
            TemporaryAppearance = default,

            // playerUpdatePacketAddNpc.EffectTags = TODO

            Unknown38 = EnemyStatus,
            Unknown39 = default,
            Unknown40 = default,
            Unknown41 = ShowHealthBar, // Health bar
            Unknown42 = CollisionEnabled,

            HasTilt = default,

            // playerUpdatePacketAddNpc.Customization = TODO

            Tilt = default,

            NameColor = NameColor,

            AreaDefinitionId = AreaDefinitionId,

            ImageSetId = ImageSetId,

            IsInteractable = IsInteractable,

            RiderGuid = RiderGuid,

            MovementType = MovementType,

            Unknown51 = default,

            Unknown52 = default,

            Unknown53 = default,

            Unknown54 = default,

            Unknown55 = default,

            Unknown56 = default,
            Unknown57 = default,
            Unknown58 = default,

            // playerUpdatePacketAddNpc.Head = TODO
            // playerUpdatePacketAddNpc.Hair = TODO
            // playerUpdatePacketAddNpc.ModelCustomization = TODO

            ReplaceTerrainObject = default,

            Unknown63 = default,
            Unknown64 = 3050,

            FlyByEffectId = default,

            // ★ THE RED-NAME KEY (user-found, 2026-07-03): ActiveProfile must be NON-DEFAULT. The
            // client's AddNpc apply calls SetProfileId(packet.ActiveProfile) AFTER writing the NPC's
            // disposition — and SetProfileId is what re-runs the nameplate COLOR RESOLVER (sub_966460).
            // But SetProfileId guards on change: ActiveProfile == the ctor default means it short-
            // circuits, the resolver never re-runs, and the name keeps the ctor-baked ALLY blue.
            // A non-default profile makes the resolver run with the REAL disposition -> hostile
            // (Disposition 0) + NameColor 0 = RED name. Order of operations, nothing more.
            ActiveProfile = ActiveProfile,

            NotificationImageSetId = NotificationImageSetId,
            Unknown68 = Unknown68,

            NameScale = NameScale,

            NameplateImageId = NameplateImageId,
        };

        return packet;
    }

    #region Equatable

    public bool Equals(IEntity? other)
    {
        return Guid == other?.Guid;
    }

    public override bool Equals([NotNullWhen(true)] object? obj)
    {
        if (obj is Npc other)
            return Equals(other);

        return false;
    }

    public override int GetHashCode()
    {
        return Guid.GetHashCode();
    }

    public static bool operator ==(Npc left, Npc right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(Npc left, Npc right)
    {
        return !(left == right);
    }

    #endregion

    public virtual void Dispose()
    {
        foreach (var visiblePlayer in VisiblePlayers)
            visiblePlayer.Value.OnRemoveVisibleNpcs([this]);

        ZoneTile.Entities.Remove(Guid, out _);

        Zone.TryRemoveNpc(Guid);
    }
}
