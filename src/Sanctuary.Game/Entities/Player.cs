using System;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;

using Sanctuary.Core.Collections;
using Sanctuary.Core.IO;
using Sanctuary.Game.Combat;
using Sanctuary.Game.Interactions;
using Sanctuary.Game.Leveling;
using Sanctuary.Game.Zones;
using Sanctuary.Packet;
using Sanctuary.Packet.Common;
using Sanctuary.Packet.Common.Chat;
using Sanctuary.UdpLibrary;
using Sanctuary.UdpLibrary.Enumerations;

namespace Sanctuary.Game.Entities;

public sealed class Player : ClientPcData, IEntity
{
    private readonly UdpConnection _connection;
    private readonly IResourceManager _resourceManager;

    public bool Visible { get; set; }
    public DateTime? SpawnedAt { get; set; }
    public ulong LastInteractNpcGuid { get; set; }
    public DateTime LastInteractAt { get; set; }

    // When the player last accepted a quest. Used to ignore a spurious CommandPacketQuestAbandon
    // (26/23) that the client can fire in the moments right after accepting - without this guard
    // that stray packet would immediately drop the quest the player just took.
    public DateTime LastQuestAcceptedAt { get; set; }

    // PARTY: last time a group-invite C2S was acted on. The client re-sends GroupInvite
    // ~6x/sec while the invite UI is up (like FreeInteractionNpc), so the handler debounces on this
    // to fire the invite once per burst.
    public DateTime LastPartyInviteAt { get; set; }

    // True once the login-only zone-in burst (Welcome screen etc.) has been sent this
    // session. The overworld's OnClientIsReady runs on EVERY zone-in — including the return from a
    // combat instance — and re-sending PacketLoadWelcomeScreen there re-opens the client's Welcome
    // popup (Main.wndWelcomeHandler) ON TOP of the encounter's victory screen (live bug 2026-07-04).
    public bool LoginBurstSent { get; set; }

    // Set once the Hero's Journal has been repopulated this session. The client keeps the
    // journal across a re-zone (e.g. the Frostfang arena round-trip), so re-sending QuestAdd on every
    // overworld entry APPENDS duplicate rows the client never dedupes - and completion can only clear
    // one, leaving finished quests stuck in the helper. Gate the restore to login only.
    public bool JournalRestored { get; set; }

    // LOOT WHEEL: the prize the victory wheel was told to land on (set when the encounter
    // sends MiniGameLootWheelSetItemToLandOn; consumed by the C2S LootWheelOnRotationStopped handler,
    // which grants it). Null = no spin pending. A null prize with PendingWheelCoins > 0 = the
    // COINS slice.
    public Sanctuary.Packet.RewardEntry? PendingWheelPrize { get; set; }
    public int PendingWheelCoins { get; set; }

    // XP already granted (AwardXp runs immediately at win) but whose GRANT BANNER is held until the wheel
    // stops, so it appears together with the coins/item banner instead of firing as a separate, easy-to-miss
    // toast the instant you win (before the score/reward card is even on screen). See EncounterArenaZone.
    // WinEncounter + BaseMiniGamePacketHandler.HandleLootWheelStopped.
    public int PendingWheelXp { get; set; }

    // DAILY WHEEL ("Spin For The Win!", the game_wheel.gfx widget - unrelated to the loot wheel above):
    // the slice the wheel was told to stop on when the player pressed spin, paid out when the widget
    // reports the wheel stopped. -1 = no spin in flight. See DailyWheelGame.
    public int PendingDailyWheelSlot { get; set; } = -1;
    public int PendingDailyWheelId { get; set; }

    // Where the exit door returns the player after a combat instance: the overworld spot
    // they stood on when GO! teleported them out (set by the entrance handler, consumed + cleared by
    // the arena's ReturnHome). Null = fall back to the zone spawn.
    public System.Numerics.Vector4? EncounterReturnPosition { get; set; }

    // "Take Me There" auto-walk session — see ClientPathBasePacketHandler. Set true on a real button
    // click (Mode 2); while true, EVERY path refresh (including the passive ones the client sends
    // automatically as the player moves) re-sends the auto-walk command too, so the character keeps
    // re-committing to the current best path instead of drifting off the one segment it was told to
    // walk once. Cleared on arrival or when the tracked objective's destination changes.
    public bool TakeMeThereActive { get; set; }
    public System.Numerics.Vector4 TakeMeThereDestination { get; set; }

    public IZone Zone { get; set; }
    public ZoneTile ZoneTile { get; private set; } = ZoneTile.Empty;
    public ConcurrentDictionary<ulong, Npc> VisibleNpcs { get; } = [];
    public ConcurrentDictionary<ulong, Player> VisiblePlayers { get; } = [];

    private int ZoneAreaId { get; set; }

    public int ChatBubbleForegroundColor { get; set; }
    public int ChatBubbleBackgroundColor { get; set; }
    public int ChatBubbleSize { get; set; }

    public bool IsAdmin { get; set; }
    public bool IsMod { get; set; }

    // What chat commands this player may run - see ChatCommandManager.
    public Sanctuary.Game.ChatCommands.ChatCommandRole ChatCommandRole =>
        Sanctuary.Game.Helpers.ChatHelper.GetRoleFromFlags(IsAdmin, IsMod);
    public DateTimeOffset? MutedUntil { get; set; }

    public ClientPcProfile ActiveProfile =>
        Profiles.FirstOrDefault(x => x.Id == ActiveProfileId) ?? Profiles.First();

    public Mount? Mount { get; set; }

    public List<FriendData> Friends { get; set; } = [];
    public List<IgnoreData> Ignores { get; set; } = [];

    public ConcurrentDictionary<ChatChannel, bool> ChatChannelStatus { get; set; } = [];

    public int StationCash { get; set; }
    public List<CoinStoreTransactionRecord> CoinStoreTransactions { get; set; } = [];

    public int TimezoneOffset { get; set; }

    public Dictionary<int, Dictionary<int, int>> ActionBarItemGuids { get; set; } = new();

    public new int TemporaryAppearance { get; set; }
    public DateTimeOffset? TemporaryAppearanceExpiresAt { get; set; }
    private int _temporaryAppearanceEffectId;


    public bool IsDead { get; set; }

    // Where the player fell (set on Knockout) — the "Revive here" respawn option returns them here.
    public System.Numerics.Vector4 DeathPosition { get; set; }
    public int CurrentHitpoints { get; set; } = 2500;
    public int CurrentMana { get; set; } = 100;

    public ConcurrentSet<ulong> IncomingFriendRequests { get; } = [];

    public ulong CharacterId { get; set; }
    public bool InCombat { get; set; }
    public ulong CombatTargetGuid { get; set; }
    public ulong ActiveMerchantGuid { get; set; }
    public DateTime LastCombatTime { get; set; }

    public Pet? Pet { get; set; }

    // QuestId -> Completed. Presence in the dictionary means the quest has been accepted.
    public Dictionary<int, bool> Quests { get; } = new();

    // QuestId -> number of goals completed (goals tick off in order). The active goal is at this index.
    // Absent = 0 goals done. Persisted alongside the quest so multi-goal progress survives relog.
    public Dictionary<int, int> QuestGoalProgress { get; } = new();

    // QuestId -> current collect count for the quest's ACTIVE Collect goal (how many pickups gathered so
    // far, 0..RequiredCount). In-memory only: a relog restarts the in-progress collect goal from 0 (the
    // shared collectibles respawn), while completed goals persist via QuestGoalProgress.
    // Cleared when the collect goal ticks off.
    public Dictionary<int, int> QuestCollectProgress { get; } = new();

    // Guids of individual Collect pickups THIS player has already gathered (pickups are shared world
    // objects, hidden client-side per-player on collect). Lets the objective marker point at the nearest
    // pickup the player hasn't taken yet. Cleared when the pickups are re-spawned (relog / re-accept).
    public HashSet<ulong> CollectedPickups { get; } = new();

    // Guids of the NPCs THIS player has already talked to for an active COUNTED TalkToNpc goal
    // ("Talk to Freewheelers - 0/3"), so re-talking to the same one can't credit the counter twice and
    // the objective marker can point at the next one they haven't reached. The talk-goal twin of
    // CollectedPickups, and in-memory for the same reason: cleared on accept/abandon and when the goal
    // ticks off. NB: like CollectedPickups this resets on relog while the COUNT itself is restored from
    // DbCharacterQuest.GoalCount, so a relog mid-step lets an already-credited NPC be re-talked - the
    // same leniency the collect goals have, never a loss of progress.
    public HashSet<ulong> TalkedQuestNpcs { get; } = new();

    // NPCs this player has scared (/scare, QuickChat id 219) and not yet collected from. A counted talk
    // goal with RequiresScare only credits an NPC that is in here, which is how trick-or-treating works:
    // scare first, then talk for the candy. Cleared alongside TalkedQuestNpcs on accept/abandon.
    public HashSet<ulong> ScaredNpcs { get; } = new();

    // Turns of an NPC conversation still to play after the one on screen (see QuestDialogue): each
    // response-button click pops one. Empty = the bubble currently up is the last, so the click ends the
    // conversation and restores the camera.
    public Queue<Resources.Definitions.QuestDialogueLine> PendingDialogue { get; } = new();

    // A one-shot action owned by whatever opened the CURRENT dialog, for dialogs that aren't quest
    // conversations (the Abominable's Treasure chest). Consumed by the 26/6 response handler before it falls
    // through to the quest flow, and cleared as it fires so a stale click can't re-trigger it.
    public Func<bool>? PendingDialogAction { get; set; }

    // The NPC doing the talking in that conversation - every turn stays framed on them.
    public ulong PendingDialogueNpcGuid { get; set; }

    // The NPC currently running the talking loop for this player, or 0. Tracked because that loop is a
    // BASE animation (op35/8 PlayType 1) that runs until it is replaced - see QuestDialogue - so every
    // path out of a conversation has to know which NPC to put back to idle.
    public ulong TalkingNpcGuid { get; set; }

    // Bumped by every start/stop of that loop. A delayed reset carries the ticket it was scheduled
    // under and only fires while it is still current, so a newer gesture is never cut short by an
    // older one's timer.
    public int TalkAnimationTicket { get; set; }

    // COMBAT TUTORIAL: the index of the tutorial step the player is currently on (-1 = not in the
    // tutorial). Each step arms a client-detected objective (look-at / first-movement / kill / etc.);
    // the client reports completion via op45/7 and the zone advances to the next step. Globe/barrier
    // prop guids the tutorial spawned for this player are tracked so they can be cleaned up on finish.
    public int TutorialStep { get; set; } = -1;
    public List<ulong> TutorialPropGuids { get; } = new();
    // Pre-computed world positions for the tutorial's look-at spheres (captured from the player's
    // facing at start). Spheres spawn ONE AT A TIME from these - the next appears as the current
    // one is looked at and despawns.
    public List<System.Numerics.Vector4> TutorialGlobePositions { get; } = new();

    // The quest the player currently has selected/tracked in the quest helper (set on accept and when
    // they pick one in the journal). The tracker arrow and the "Take Me There" breadcrumb point at THIS
    // quest's objective, not just the first active quest. 0 = none selected.
    public int ActiveQuestId { get; set; }

    // Deferred quest turn-in finalization: set when a quest end screen is shown, invoked (once)
    // when the client sends QuestEndReplyPacket (the player clicked "Complete").
    public System.Action? PendingQuestEndAction { get; set; }


    private readonly ConcurrentQueue<(DateTimeOffset SendAt, ISerializablePacket Packet, bool SendToSelf)> _delayedPackets = new();

    public Vector4 StartingZonePosition { get; set; }
    public Quaternion StartingZoneRotation { get; set; }
    private ulong _currentHouseGuid;
    public ulong CurrentHouseGuid
    {
        get => _currentHouseGuid;
        set
        {
            if (_currentHouseGuid == value)
                return;

            _currentHouseGuid = value;
            RemovePlayersFromOtherInstances();
            RemoveNpcsFromOtherInstances();
        }
    }

    public Player(BaseZone zone, UdpConnection connection, IResourceManager resourceManager)
    {
        Zone = zone;

        _connection = connection;
        _resourceManager = resourceManager;
    }

    #region Connection

    public void Send(ISerializablePacket packet)
    {
        var data = packet.Serialize();

        _connection.Send(UdpChannel.Reliable1, data);
    }

    public void SendToVisible(ISerializablePacket packet, bool sendToSelf = false)
    {
        var visiblePlayers = VisiblePlayers.ToFrozenDictionary();

        foreach (var visiblePlayer in visiblePlayers)
            visiblePlayer.Value.Send(packet);

        if (sendToSelf)
            Send(packet);
    }

    public void SendTunneled(ISerializablePacket packet)
    {
        var packetTunneled = new PacketTunneledClientPacket
        {
            Payload = packet.Serialize()
        };

        Send(packetTunneled);
    }

    [Obsolete]
    public void SendTunneled(byte[] buffer)
    {
        var packetTunneled = new PacketTunneledClientPacket
        {
            Payload = buffer
        };

        Send(packetTunneled);
    }

    public void SendTunneledToVisible(ISerializablePacket packet, bool sendToSelf = false)
    {
        var visiblePlayers = VisiblePlayers.ToFrozenDictionary();

        foreach (var visiblePlayer in visiblePlayers)
            visiblePlayer.Value.SendTunneled(packet);

        if (sendToSelf)
            SendTunneled(packet);
    }

    public void SendTunneledToVisibleDelayed(ISerializablePacket packet, int delayMs, bool sendToSelf = false)
    {
        _delayedPackets.Enqueue((DateTimeOffset.UtcNow.AddMilliseconds(delayMs), packet, sendToSelf));
    }

    // One scheduled personal-UI packet per action bar slot (the cooldown re-enable) - keyed, not queued,
    // so a slot that gets emptied before its cooldown naturally expires (last item consumed) can cancel
    // its own pending re-enable instead of it firing later and silently un-deleting the slot.
    private readonly ConcurrentDictionary<(int, int), (DateTimeOffset SendAt, ISerializablePacket Packet)> _delayedSlotPackets = new();

    public void ScheduleSlotPacket(int actionBarId, int slotIndex, ISerializablePacket packet, int delayMs)
    {
        _delayedSlotPackets[(actionBarId, slotIndex)] = (DateTimeOffset.UtcNow.AddMilliseconds(delayMs), packet);
    }

    public void CancelScheduledSlotPacket(int actionBarId, int slotIndex) => _delayedSlotPackets.TryRemove((actionBarId, slotIndex), out _);

    public bool IsMuted()
    {
        DateTimeOffset currentTime = DateTimeOffset.UtcNow;
        DateTimeOffset? mutedUntil = MutedUntil;
        return mutedUntil.HasValue && mutedUntil > currentTime;
    }

    public void Disconnect()
    {
        _connection.Disconnect();
    }

    #endregion

    #region Update

    public void UpdateEveryTick()
    {
        // Drive the overworld in-combat state here (10 Hz, single thread) so the op41 enter/exit packets are
        // never sent from the async auto-fire tasks — which raced and latched the client "in combat" (menu
        // stuck). See WorldCombatStateTick.
        WorldCombatStateTick();

        LevelUpBurstTick();

        if (TemporaryAppearanceExpiresAt.HasValue &&
            TemporaryAppearanceExpiresAt.Value <= DateTimeOffset.UtcNow)
        {
            RemoveTemporaryAppearance();
        }

        while (_delayedPackets.TryPeek(out var delayed) && delayed.SendAt <= DateTimeOffset.UtcNow)
        {
            if (_delayedPackets.TryDequeue(out delayed))
                SendTunneledToVisible(delayed.Packet, delayed.SendToSelf);
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var (key, scheduled) in _delayedSlotPackets)
        {
            if (scheduled.SendAt > now)
                continue;

            if (_delayedSlotPackets.TryRemove(key, out var removed))
                SendTunneled(removed.Packet);
        }
    }

    public void UpdateEverySecond()
    {
        RegenTick();
        MedicSkillsTick();
    }

    // Out-of-combat window (seconds): HP won't regen until this long after the last hit taken.
    // Matches the ability handler's world-combat decay so "in combat" means the same thing on both sides.
    private const int OutOfCombatSeconds = 6;

    // When the player last took combat damage — gates HP regen so it doesn't fight incoming hits.
    public DateTime LastCombatDamageAt { get; set; } = DateTime.MinValue;

    // Regenerates HP (and, for non-combat jobs, mana) toward their maximums.
    private void RegenTick()
    {
        if (IsDead)
            return;

        if (!Stats.TryGetValue(CharacterStatId.MaxHealth, out var maxHpStat) ||
            !Stats.TryGetValue(CharacterStatId.MaxMana, out var maxManaStat))
            return; // stats not initialized yet

        int maxHp = maxHpStat.Int;
        int maxMana = maxManaStat.Int;

        // COMBAT: don't regen HP while actively fighting (a hit within the out-of-combat window). The old
        // behavior raced incoming enemy damage and made the health bar visibly jitter up and down mid-fight.
        bool inCombat = DateTime.UtcNow - LastCombatDamageAt < TimeSpan.FromSeconds(OutOfCombatSeconds);

        // DUNGEONS/ENCOUNTERS: no passive HP regen at all in here (live feedback 2026-07-27) - healing only
        // comes from power-ups/potions inside a combat instance. CombatEncounterZone covers every dungeon/
        // encounter (the data-driven EncounterArenaZone plus FrostfangArenaZone/TormentedSpiritsArenaZone);
        // regular overworld zones are untouched and keep passive regen.
        bool inCombatInstance = Zone is CombatEncounterZone;

        bool hpChanged = false;
        if (!inCombat && !inCombatInstance && CurrentHitpoints < maxHp)
        {
            int regen = Stats.TryGetValue(CharacterStatId.HitPointRegen, out var hr) ? hr.Int : 25;
            CurrentHitpoints = Math.Min(maxHp, CurrentHitpoints + Math.Max(1, regen));
            hpChanged = true;
        }

        // STAMINA: combat jobs' stamina bar is owned ENTIRELY by the ability handler's energy system
        // (0-100, drains on specials, +4/sec). RegenTick must NOT also drive it with the level-scaled
        // CurrentMana, or the two systems fight over the same bar — that flicker was the "stamina bar
        // glitching" AND it re-enabled the special slot client-side mid-cooldown (the "ability #2 spam").
        bool usesCombatEnergy = Combat.JobKits.Active(this)?.UsesCombatEnergy ?? false;

        bool manaChanged = false;
        if (!usesCombatEnergy && CurrentMana < maxMana)
        {
            int regen = Stats.TryGetValue(CharacterStatId.ManaRegen, out var mr) ? mr.Int : 4;
            CurrentMana = Math.Min(maxMana, CurrentMana + Math.Max(1, regen));
            manaChanged = true;
        }

        if (hpChanged)
        {
            SendTunneled(new ClientUpdatePacketHitpoints { CurrentHitpoints = CurrentHitpoints, MaxHitpoints = maxHp });
            SendTunneledToVisible(new PlayerUpdatePacketUpdateHitpoints
            {
                Guid = Guid,
                Hitpoints = CurrentHitpoints,
                MaxHitpoints = maxHp
            }, sendToSelf: true);
        }

        if (manaChanged)
        {
            SendTunneled(new ClientUpdatePacketMana { CurrentMana = CurrentMana, MaxMana = maxMana });
            SendTunneledToVisible(new PlayerUpdatePacketUpdateMana
            {
                Guid = Guid,
                CurrentMana = CurrentMana,
                MaxMana = maxMana
            }, sendToSelf: true);
        }
    }

    // Live-confirmed (2026-07-31): the client animates the cooldown sweep itself from TotalRefreshTime -
    // no per-second resend needed for that. But it does NOT re-enable the slot for input on its own once
    // the sweep finishes ("sweep animates but after the sweep I cannot use the ability again") - that
    // needs one explicit packet once the cooldown is actually over. So: one packet now, one packet
    // scheduled for later - not the old repeating-every-second loop, and not silence either.
    public void StartActionBarCooldown(int actionBarId, int slotIndex, int iconId, int nameId, int count, int cooldownMs, int iconTintId = 0)
    {
        SendTunneled(BuildActionBarSlotPacket(actionBarId, slotIndex, iconId, iconTintId, nameId, count, cooldownMs, enabled: false, elapsed: 0));
        ScheduleSlotPacket(actionBarId, slotIndex, BuildActionBarSlotPacket(actionBarId, slotIndex, iconId, iconTintId, nameId, count, cooldownMs, enabled: true, elapsed: cooldownMs), cooldownMs);
    }

    private static ClientUpdatePacketUpdateActionBarSlot BuildActionBarSlotPacket(int actionBarId, int slotIndex, int iconId, int iconTintId, int nameId, int count, int cooldownMs, bool enabled, int elapsed)
    {
        var packet = new ClientUpdatePacketUpdateActionBarSlot { Data = { Id = actionBarId, Slot = slotIndex } };
        packet.Slot.IsEmpty = false;
        packet.Slot.IconId = iconId;
        packet.Slot.IconTintId = iconTintId;
        packet.Slot.NameId = nameId;
        packet.Slot.Unknown5 = 1;
        packet.Slot.Unknown6 = 4;
        packet.Slot.Unknown7 = 15;
        packet.Slot.Enabled = enabled;
        packet.Slot.Unknown10 = elapsed;
        packet.Slot.TotalRefreshTime = cooldownMs;
        packet.Slot.Unknown12 = elapsed;
        packet.Slot.Quantity = count;
        packet.Slot.ForceDismount = true;
        packet.Slot.Unknown15 = elapsed;
        return packet;
    }

    // Knockout visual (this client renders NOTHING on its own at 0 HP): a hit-poof so the player
    // and nearby people see the moment of defeat. Tunable.
    private const int KnockoutEffectId = 5017; // PFX death poof (same one dying NPCs use)

    // Send a System-channel chat line to this player (the death/revive feedback, since there's no
    // native death UI to show it).
    public void SendSystemMessage(string text)
    {
        SendTunneled(new PacketChat
        {
            Channel = Sanctuary.Packet.Common.Chat.ChatChannel.System,
            FromGuid = Guid,
            FromName = Name ?? new(),
            Message = text
        });
    }

    // Revive burst played on respawn — a big flashy particle burst (the level-up FX), far
    // flashier than a plain poof.
    private const int ReviveEffectId = 15117; // PFX_levelup_big (~2s one-shot burst)

    // Resurrect/get-up animation played on revive (0 = none — the knocked-out state clear already
    // stands the player up; set to a real resurrect clip id once confirmed).
    private const int ResurrectAnimId = 0;

    public void Respawn()
    {
        IsDead = false;

        var maxHp = Stats[CharacterStatId.MaxHealth].Int;
        CurrentHitpoints = maxHp;

        SendTunneled(new ClientUpdatePacketHitpoints
        {
            CurrentHitpoints = maxHp,
            MaxHitpoints = maxHp
        });

        // Clear the knocked-out/rooted state (stand up + movement restored).
        SendTunneledToVisible(new PlayerUpdatePacketUpdateCharacterState
        {
            Guid = Guid,
            Status = CharacterStatus.None,
        }, sendToSelf: true);

        // Clear the overworld "in combat" flags the knockout flow raised for the respawn window, and reset the
        // combat-state machine so it doesn't immediately re-enter combat on revive (the pre-death timestamp
        // could still be within the out-of-combat window). Otherwise the player stays wedged "in combat" after
        // reviving. WorldCombatStateTick resumes once alive.
        _worldCombatActive = false;
        _lastWorldCombatTicks = 0;
        SendWorldCombatState(false); // forced edge - the client-side appliers ignore a no-change value

        // Resurrect animation + revive FX at the player (visible to nearby players too).
        if (ResurrectAnimId > 0)
        {
            SendTunneledToVisible(new PlayerUpdatePacketSetAnimation
            {
                Guid = Guid,
                AnimationId = ResurrectAnimId,
            }, sendToSelf: true);
        }
        SendTunneledToVisible(new PlayerUpdatePacketPlayCompositeEffect
        {
            Guid = Guid,
            CompositeEffectId = ReviveEffectId,
            Position = Position,
        }, sendToSelf: true);

        SendSystemMessage("You have been revived!");
    }

    public void TakeDamage(int amount, CombatNpc source) => TakeDamage(amount);

    // Apply combat damage from any source (world CombatNpc, arena claw, etc.): drop HP, push the
    // HP bar, and knock out at 0. No-op while already knocked out.
    public void TakeDamage(int amount)
    {
        if (IsDead)
            return;

        // Medic's Immunize ("makes you and your group invincible") - see MedicWeaponAbilities.cs's
        // ImmunizeDamageReductionPercent comment. Single central choke point for all player-taken damage, so
        // hooking it here covers every source (world CombatNpc, arena claw, etc.), same as the rest of TakeDamage.
        amount = Combat.CombatBuffs.ReduceIncomingDamage(Guid, amount);

        LastCombatDamageAt = DateTime.UtcNow; // gates HP regen so the bar doesn't jitter mid-fight

        CurrentHitpoints = Math.Max(0, CurrentHitpoints - amount);

        var hpPacket = new ClientUpdatePacketHitpoints
        {
            CurrentHitpoints = CurrentHitpoints,
            MaxHitpoints = Stats[CharacterStatId.MaxHealth].Int
        };
        SendTunneled(hpPacket);

        if (CurrentHitpoints <= 0)
            Knockout();
        else
            EnterWorldCombat(); // taking a hit puts you in combat too (weapon drawn, HP bars, damage text)
    }

    // Apply a real heal: raises CurrentHitpoints (clamped to max) and pushes the same hp-update packets
    // TakeDamage/RegenTick use, so the health bar actually moves. Callers (potions, power-ups, heart
    // pickups) also send their own cosmetic floating "+N" PlayerUpdatePacketHitPointModification alongside
    // this — that packet alone never touched CurrentHitpoints, which is why the bar didn't move once
    // passive regen was turned off inside dungeons/encounters. Returns the amount actually healed (may be
    // less than requested near max) so callers can size the floating number to what really landed.
    public int Heal(int amount)
    {
        if (IsDead || amount <= 0)
            return 0;

        if (!Stats.TryGetValue(CharacterStatId.MaxHealth, out var maxHpStat))
            return 0;

        int maxHp = maxHpStat.Int;
        int healed = Math.Min(amount, maxHp - CurrentHitpoints);
        if (healed <= 0)
            return 0;

        CurrentHitpoints += healed;

        SendTunneled(new ClientUpdatePacketHitpoints { CurrentHitpoints = CurrentHitpoints, MaxHitpoints = maxHp });
        SendTunneledToVisible(new PlayerUpdatePacketUpdateHitpoints
        {
            Guid = Guid,
            Hitpoints = CurrentHitpoints,
            MaxHitpoints = maxHp
        }, sendToSelf: true);

        return healed;
    }

    private DateTime _lastMedicTickAt = DateTime.MinValue;

    // Medic Skills: First Aid (L1) - "Heals yourself and any ally near you for 250 health" - Vitamins (L10) -
    // "increasing critical hit chance by 1% and critical hit damage by 10% for 5 seconds" (damage-% half only,
    // see MedicWeaponAbilities.cs) - and Shock Paddles (L15), whose real client tutorial dialogue adds a
    // "...reviving your allies" purpose alongside its combat splash (the splash half is trait-gated on-hit
    // damage, see AbilityPacketClientRequestStartAbilityHandler.ApplyMedicTraitDamage; the revive half belongs
    // here since it's a passive support effect, not a combat proc). CORRECTED 2026-07-29: the revive was
    // initially wired to firing only when the equipped weapon's SPECIAL happens to be named "Shock Paddles" -
    // wrong, since Shock Paddles is a job TRAIT (like First Aid/Vitamins), not a per-weapon gate, so most L15+
    // Medics on a different-named weapon special would never trigger it. Moved to this same trait-gated
    // per-second cadence instead, exactly like First Aid/Vitamins. None of the 3 wiki entries give a trigger
    // condition, so all three ride this shared cadence (called alongside RegenTick from UpdateEverySecond)
    // rather than a bespoke loop - interval/radius/heal amount are ours to tune.
    private void MedicSkillsTick()
    {
        if (IsDead || ActiveProfileId != Combat.MedicWeaponAbilities.MedicProfileId)
            return;

        if (DateTime.UtcNow - _lastMedicTickAt < TimeSpan.FromMilliseconds(Combat.MedicWeaponAbilities.FirstAidTickIntervalMs))
            return;
        _lastMedicTickAt = DateTime.UtcNow;

        if (Combat.MedicWeaponAbilities.HasTrait(this, Combat.MedicWeaponAbilities.FirstAidLevel))
            HealSelfAndNearbyAllies(Combat.MedicWeaponAbilities.FirstAidHealAmount, Combat.MedicWeaponAbilities.FirstAidHealRadius);

        if (Combat.MedicWeaponAbilities.HasTrait(this, Combat.MedicWeaponAbilities.VitaminsLevel))
        {
            var pct = 100 + (int)(Combat.MedicWeaponAbilities.VitaminsCritDamageBonusPercent * 100);
            Combat.CombatBuffs.AddDamageBuff(Guid, pct, Combat.MedicWeaponAbilities.VitaminsDurationMs);
        }

        if (Combat.MedicWeaponAbilities.HasTrait(this, Combat.MedicWeaponAbilities.ShockPaddlesLevel))
            ReviveOrHealNearbyAllies(Combat.MedicWeaponAbilities.ShockPaddlesAllyHealAmount, Combat.MedicWeaponAbilities.ShockPaddlesReviveRadius);
    }

    // Shared by First Aid's periodic tick.
    public void HealSelfAndNearbyAllies(int amount, float radius)
    {
        Heal(amount);

        var radiusSq = radius * radius;
        foreach (var ally in VisiblePlayers.Values)
        {
            var dx = ally.Position.X - Position.X;
            var dz = ally.Position.Z - Position.Z;
            if (dx * dx + dz * dz <= radiusSq)
                ally.Heal(amount);
        }
    }

    // Shared by Shock Paddles' periodic tick - unlike First Aid, this does NOT heal self (you can't be both
    // dead/wounded and casting; "reviving your allies" is ally-only per the sourced dialogue line). Downed
    // allies are fully revived; wounded-but-alive ones are topped up by a flat amount.
    public void ReviveOrHealNearbyAllies(int healAmount, float radius)
    {
        var radiusSq = radius * radius;
        foreach (var ally in VisiblePlayers.Values)
        {
            var dx = ally.Position.X - Position.X;
            var dz = ally.Position.Z - Position.Z;
            if (dx * dx + dz * dz > radiusSq)
                continue;

            if (ally.IsDead)
                ally.Respawn();
            else
                ally.Heal(healAmount);
        }
    }

    // Shared by Immunize's on-cast handler - applies an incoming-damage-reduction buff (CombatBuffs.
    // AddDamageReductionBuff) to the caster AND nearby allies, matching the "AoE (Buff)" scope of the real
    // wiki description ("makes you and your group invincible"). Same nearby-ally pattern as
    // HealSelfAndNearbyAllies, just calling a buff API instead of a heal.
    public void ApplyDamageReductionToNearbyAllies(int reductionPct, int durationMs, float radius)
    {
        Combat.CombatBuffs.AddDamageReductionBuff(Guid, reductionPct, durationMs);

        var radiusSq = radius * radius;
        foreach (var ally in VisiblePlayers.Values)
        {
            var dx = ally.Position.X - Position.X;
            var dz = ally.Position.Z - Position.Z;
            if (dx * dx + dz * dz <= radiusSq)
                Combat.CombatBuffs.AddDamageReductionBuff(ally.Guid, reductionPct, durationMs);
        }
    }

    // Heal a FRACTION of max HP rather than a flat number (live feedback 2026-07-27: "make sure health
    // potions and health powerups scale for all players, some players have more health than others" - a
    // flat +400 is trivial for a high-level job's much larger HP pool and huge for a level 1's). Used by
    // PotionAbilities/PowerupSystem's Health effects; the dungeons' real video-captured "+125" heart heal
    // is left as a flat number since that one has actual retail provenance, unlike these estimated amounts.
    public int HealPercent(float fraction)
    {
        if (IsDead || fraction <= 0)
            return 0;

        if (!Stats.TryGetValue(CharacterStatId.MaxHealth, out var maxHpStat))
            return 0;

        int maxHp = maxHpStat.Int;
        int amount = Math.Max(1, (int)(maxHp * fraction));
        int healed = Math.Min(amount, maxHp - CurrentHitpoints);
        if (healed <= 0)
            return 0;

        CurrentHitpoints += healed;

        SendTunneled(new ClientUpdatePacketHitpoints { CurrentHitpoints = CurrentHitpoints, MaxHitpoints = maxHp });
        SendTunneledToVisible(new PlayerUpdatePacketUpdateHitpoints
        {
            Guid = Guid,
            Hitpoints = CurrentHitpoints,
            MaxHitpoints = maxHp
        }, sendToSelf: true);

        return healed;
    }

    // --- DODGE (avoidance) ---------------------------------------------------------------------------
    // com_dodge — the sidestep clip (AnimationGroups.xml id 1406). Played on a successful dodge.
    public const int DodgeAnimId = 1406;

    // Base dodge chance every player has, before job bonuses.
    public const int BaseDodgePercent = 5;

    // Total chance (%) to dodge an incoming enemy attack: the base avoidance plus any job bonus
    // (Archer's Reflexes trait adds its dodge % at rank 15+).
    public int DodgePercent()
    {
        var pct = BaseDodgePercent;
        if (Combat.ArcherWeaponAbilities.HasTrait(this, Combat.ArcherWeaponAbilities.ReflexesLevel))
            pct += Combat.ArcherWeaponAbilities.ReflexesDodgePercent;
        return pct;
    }

    // Reduce an incoming enemy hit for defensive traits (Ninja's Shrouded Armor, Brawler's Toughness), and
    // apply Brawler Resilience (heal-on-hit) as a side effect. At least 1 damage still lands.
    public int ReduceIncomingDamage(int damage)
    {
        if (Combat.NinjaWeaponAbilities.HasTrait(this, Combat.NinjaWeaponAbilities.ShroudedArmorLevel))
            damage = (int)(damage * (1f - Combat.NinjaWeaponAbilities.ShroudedArmorDamageReduction));

        // Brawler Toughness: withstand more damage before being knocked out.
        if (Combat.BrawlerWeaponAbilities.HasTrait(this, Combat.BrawlerWeaponAbilities.ToughnessLevel))
            damage = (int)(damage * (1f - Combat.BrawlerWeaponAbilities.ToughnessDamageReduction));

        // Wizard Magical Shielding: magical shielding prevents some incoming damage.
        if (Combat.WizardWeaponAbilities.HasTrait(this, Combat.WizardWeaponAbilities.MagicalShieldingLevel))
            damage = (int)(damage * (1f - Combat.WizardWeaponAbilities.MagicalShieldingDamageReduction));

        // Brawler Resilience: gain a little health each time you're hit (capped at max, applied before the
        // caller subtracts the reduced damage, so a hit can be partly — even fully — offset).
        if (Combat.BrawlerWeaponAbilities.HasTrait(this, Combat.BrawlerWeaponAbilities.ResilienceLevel)
            && Stats.TryGetValue(CharacterStatId.MaxHealth, out var maxHpStat))
            CurrentHitpoints = Math.Min(maxHpStat.Int, CurrentHitpoints + Combat.BrawlerWeaponAbilities.ResilienceHealPerHit);

        return Math.Max(1, damage);
    }

    // Roll to dodge an incoming enemy attack from attackerGuid. On a dodge, sends the
    // client's dedicated AttackTargetDodged packet (op32/6) so it renders the floating "Dodge" text over this
    // player and lets the attacker play its swing, then layers the sidestep (com_dodge) clip on top so the evade
    // reads clearly. Returns true so the caller deals no damage.
    // DEV: when set (via !dodge on), every incoming attack is dodged — lets us test the op32/6 "Dodge"
    // text in real combat, where its client-side gate may pass (a synthetic send shows nothing).
    public bool ForceDodgeDebug;

    // Super Shield power-up (PowerupSystem.TryUse): "makes you invulnerable for a short time" - reuses
    // this EXISTING dodge machinery (real op32/6 dodge-text + sidestep animation, "you evaded this hit")
    // instead of building a separate no-damage packet path, since that's already the proven mechanism for
    // "an incoming attack lands 0 damage" on a player.
    public bool Invulnerable;

    public bool TryDodgeIncomingAttack(ulong attackerGuid)
    {
        if (IsDead)
            return false;
        if (Invulnerable)
            return true;
        if (!ForceDodgeDebug && Random.Shared.Next(100) >= DodgePercent())
            return false;

        // The dedicated op32/6 "Dodge" text is gated on a client-side combat-text map entry (hit-type key 123)
        // our client build doesn't have, so it renders NOTHING server-side (reversed 2026-07-15). op32/5 "Miss"
        // uses no such map and reliably shows green floating text — the working avoidance indicator. Layer the
        // com_dodge sidestep on top so the evade still reads as a dodge, not a whiffed enemy swing.
        SendTunneledToVisible(new CombatPacketAttackAttackerMissed { AttackerGuid = attackerGuid, TargetGuid = Guid }, sendToSelf: true);
        SendTunneledToVisible(new PlayerUpdatePacketSetAnimation { Guid = Guid, AnimationId = DodgeAnimId }, sendToSelf: true);
        return true;
    }

    // --- Overworld "in combat" state (client op41 sub132 SetInWorldCombat + sub133 SetIsFighting) ---
    // These flags draw the weapon, show enemy HP bars + floating damage numbers, and put the client in its
    // combat mode. We enter on ANY overworld combat action — dealing damage, TAKING damage, or pressing an
    // attack — and drop out OutOfCombatSeconds after the last one.
    //
    // CRITICAL THREADING: the op41 enter/exit packets are sent ONLY from WorldCombatStateTick, which runs on
    // the single UpdateEveryTick loop. EnterWorldCombat (called from the async auto-fire tasks + the NPC-attack
    // tick, i.e. arbitrary threads) merely STAMPS a timestamp. Earlier we sent op41 straight from those async
    // callers, so an "enter" (true) from a fire task could land AFTER the decay's "exit" (false) and latch the
    // client ON forever — the client's combat mode locks the main menu, and once latched it never released
    // ("can't press anything even out of combat"). Funneling every send through one thread makes enter/exit a
    // clean, ordered state machine that can't cross. Instanced arenas own their own fighting-state, so this
    // no-ops there (a stale flag reconciles on return to the overworld: want=false -> exit sent).
    private long _lastWorldCombatTicks;
    private volatile bool _worldCombatActive;

    // Stamp that a combat action just happened. Callable from ANY thread — it only writes the
    // timestamp; the actual op41 packets are driven by WorldCombatStateTick on the tick thread
    // so enter and exit can never race (see the threading note above).
    public void EnterWorldCombat()
    {
        if (Zone is not StartingZone)
            return; // arenas own their combat-state lifecycle
        _lastWorldCombatTicks = Environment.TickCount64;
    }

    // Single-threaded combat-state driver (UpdateEveryTick, 10 Hz). Compares "had a combat action
    // within OutOfCombatSeconds" against the current client state and sends the op41 enter/exit packets only
    // on a transition. Every op41 send happens here on one thread, so the client can never get latched "in
    // combat" with the menu stuck. Arenas drive their own.
    //
    // ★ ROOT CAUSE of "every npc has a health bar" (traced 2026-08-13). sub132 is an ENCOUNTER packet -
    // battle-instance state. Inside a dungeon every actor in the world IS a combatant, so the client putting
    // a bar on all of them is correct there. Sending it in the OVERWORLD imports that whole arena HUD, and
    // arena-style bars on quest givers, props and projectile carriers are what that looks like.
    //
    // ★ DECISION (user, 2026-08-13): OFF. No health bars anywhere, at the cost of the floating damage
    // numbers. The two are inseparable in this client and the bars were the bigger annoyance. Flipping this
    // back to true restores the numbers and the bars together - there is no third option, see below.
    //
    // It is sent to unlock floating damage numbers, and LIVE-TESTED 2026-08-13 it is genuinely required for
    // them out here. The combat-text gate (FUN_008bb0b0) suppresses text unless one of four conditions
    // breaks, and only two are reachable from a server:
    //   * `BaseClient+0x80 != 0`  <- this flag. The one that works.
    //   * `BaseClient+0x788 == 3` <- +0x788 is the client's GAME MODE and 3 maps to the literal string
    //     "StartingZone" (FUN_0090e8a0). Tried as an escape hatch on the theory that our overworld is that
    //     mode; it is NOT - "StartingZone" is the client's own tutorial zone, our world reports a different
    //     mode, and with 132 off the numbers vanished. Do not retry this.
    // The other two are a client OPTION byte (+0x778 -> +10) and being inside a type-4 activity - neither is
    // server-settable.
    //
    // The cost is that +0x80 also raises the ENCOUNTER combat HUD. Traced to the native nameplate updater
    // (FUN_009d08f0): each plate ELEMENT carries a required detail level and is shown when a GLOBAL threshold
    // at client+0x488 clears it - combat raises that threshold, so the health-bar element switches on for
    // every plate at once. It is a global switch, not a per-actor decision, which is why no AddNpc field or
    // status bit could ever have suppressed one actor's bar. (The client's own
    // NamePlateSettings/ShowAllNamePlates user option is the only other lever, and it hides names too.)
    //
    // Enemy bars do NOT depend on this: every damageable npc gets SendNpcRelevance + SendNpcHealth when it
    // becomes visible (OnAddVisibleNpcs), which is the per-npc path.
    public static bool SendInWorldCombatFlag { get; set; }          // op41/132 EncounterOverworldCombatPacket
    public static bool SendIsFightingFlag { get; set; } = true;     // op41/133 EncounterPacketIsFighting

    private void WorldCombatStateTick()
    {
        if (Zone is not StartingZone)
            return; // arenas drive their own combat-state; a stale flag reconciles on return (want=false -> exit)

        // While knocked out, the death flow owns op41: OnPlayerKnockedOut raises it so the pay/safe respawn
        // WINDOW shows its buttons, and Respawn clears it (and resets _worldCombatActive). Don't let the decay
        // tear the window's combat-state down from under it.
        if (IsDead)
            return;

        bool want = _lastWorldCombatTicks != 0
            && Environment.TickCount64 - _lastWorldCombatTicks < OutOfCombatSeconds * 1000L;

        if (want == _worldCombatActive)
            return; // no transition

        _worldCombatActive = want;
        SendWorldCombatState(want);

    }

    // ★ Both client-side appliers are EDGE-GUARDED (traced 2026-08-13): FUN_009058d0 (op41/132 -> +0x80) and
    // FUN_00905a50 (op41/133 -> +0x81) both open with `if (param_2 != current)` and do NOTHING otherwise. The
    // UI event that actually drives the npc health bars, "CallHandler:SetInWorldCombat", is fired from inside
    // that guard - so sending a value the client already holds is silently a no-op.
    //
    // That is the "bars stay up after combat ends" bug: any disagreement between our _worldCombatActive and
    // the client's real +0x80/+0x81 (a crash, a mid-fight zone change, a server restart while flagged, a
    // dropped packet) makes every later "combat off" unrepresentable, and the bars only clear when something
    // else rebuilds the HUD - which is exactly why switching jobs cleared them.
    //
    // Turning OFF therefore forces a real edge: set true, then false. The client is already showing the bars
    // at that point, so the intermediate value costs nothing visually, and the false transition is guaranteed
    // to fire the handler no matter what state the client was actually in.
    public void SendWorldCombatState(bool active)
    {
        if (active)
        {
            // Raising the state is what the flags gate. With SendInWorldCombatFlag off we simply never put
            // the client into the encounter HUD, which is the whole point.
            if (SendInWorldCombatFlag)
                SendTunneled(new EncounterOverworldCombatPacket { Unknown3 = true });
            if (SendIsFightingFlag)
                SendTunneled(new EncounterPacketIsFighting { InWorldCombat = true });
            return;
        }

        // ★ CLEARING is NEVER gated by the flags. Other paths raise 132 directly and legitimately - the
        // knockout flow does it so the respawn window renders its pay/safe buttons (BaseZone.OnPlayerKnockedOut)
        // - so if the clear respected SendInWorldCombatFlag, a player who died would revive with the encounter
        // HUD latched on and nothing able to take it back down.
        //
        // The pre-pulse handles the other half: both appliers are edge-guarded (`if (param_2 != current)`),
        // so a client that is already false ignores a false. Sending true first guarantees the false
        // transition actually fires. Only done when the flag is on - with it off the client should never have
        // been raised in the first place, and pulsing true would briefly show the very bars we are avoiding.
        if (SendInWorldCombatFlag)
        {
            SendTunneled(new EncounterOverworldCombatPacket { Unknown3 = true });
            SendTunneled(new EncounterPacketIsFighting { InWorldCombat = true });
        }

        SendTunneled(new EncounterOverworldCombatPacket { Unknown3 = false });
        SendTunneled(new EncounterPacketIsFighting { InWorldCombat = false });
    }

    // DEATH: the player's HP reached 0 — they're knocked out. Marks them dead (blocks further
    // damage + their own abilities), pins HP at 0, and hands off to the zone: the overworld leaves the
    // client's knockout UI up for a respawn-in-place; a combat instance counts the KO and fails the
    // encounter at the limit. The client shows its own knockout state when it receives 0 HP.
    public void Knockout()
    {
        if (IsDead)
            return;

        IsDead = true;
        CurrentHitpoints = 0;
        DeathPosition = Position; // where "Revive here" brings the player back

        SendTunneled(new ClientUpdatePacketHitpoints
        {
            CurrentHitpoints = 0,
            MaxHitpoints = Stats[CharacterStatId.MaxHealth].Int
        });

        // Put the actor into the KNOCKED-OUT + ROOTED state: the client plays its knockdown animation and
        // (IsRooted) stops the player from running around while down. Cleared on Respawn.
        SendTunneledToVisible(new PlayerUpdatePacketUpdateCharacterState
        {
            Guid = Guid,
            Status = CharacterStatus.IsKnockedOut | CharacterStatus.IsRooted,
        }, sendToSelf: true);

        // Also a death poof + message (belt-and-suspenders feedback).
        SendTunneledToVisible(new PlayerUpdatePacketPlayCompositeEffect
        {
            Guid = Guid,
            CompositeEffectId = KnockoutEffectId,
            Position = Position,
        }, sendToSelf: true);

        SendSystemMessage("You have been knocked out!");

        Zone.OnPlayerKnockedOut(this);
    }

    // Grants XP to the active job: accrues into the current level, levels up (and rescales stats +
    // refills HP/mana) when the curve threshold is crossed, notifies the client, and updates the star
    // meter. Persistence happens on the normal save path (DbProfile.Level / LevelXP).
    public void AwardXp(int xp)
    {
        if (xp <= 0)
            return;

        if (ActiveProfile.Rank >= JobLeveling.MaxLevel)
            return; // already max level - no more XP

        // Award XP per kill (immediately). This used to be deferred to combat-drop as a workaround for the
        // ranged-fire wedge — but the real culprits (the op41 combat-state flags + the ActivateProfile in the
        // XP flush) are now removed, so the XP feedback packets are safe to send on the kill.
        ApplyXp(xp);
    }

    // Accrue XP, level up, and send the client-facing feedback.
    private void ApplyXp(int xp)
    {
        var profile = ActiveProfile;
        if (profile.Rank >= JobLeveling.MaxLevel)
            return;

        int startLevel = profile.Rank;
        profile.LevelXpRaw += xp;

        while (profile.Rank < JobLeveling.MaxLevel && profile.LevelXpRaw >= JobLeveling.XpForLevel(profile.Rank))
        {
            profile.LevelXpRaw -= JobLeveling.XpForLevel(profile.Rank);
            profile.Rank++;
            profile.StarsEarned++;   // one star per level
        }

        if (profile.Rank >= JobLeveling.MaxLevel)
            profile.LevelXpRaw = 0;

        profile.RankPercent = JobLeveling.RankPercent(profile.Rank, profile.LevelXpRaw);

        bool leveled = profile.Rank != startLevel;

        // Floating "+XP" feedback.
        SendTunneled(new ClientUpdatePacketUpdateProfileExperience
        {
            ProfileId = profile.Id,
            XpGained = xp,
            TotalXpInLevel = profile.LevelXpRaw,
            CurrentLevel = profile.Rank
        });

        // Native job XP bar + level-up: the ability-set experience (opcode 36/8). The client renders the
        // on-screen job XP bar from Progress/TotalForLevel and fires JobLevelUp when Level increases.
        SendTunneled(new AbilityPacketUpdateAbilityExperience { Experience = BuildJobAbilityExperience() });

        if (leveled)
        {
            SendTunneled(new ClientUpdatePacketUpdateProfileRank
            {
                ProfileId = profile.Id,
                NewRank = profile.Rank,
                ProfileIconId = profile.Icon,
                ProfileNameId = profile.NameId
            });
        }

        // Real-time job XP bar + level-up — on every kill, even mid-combat (retail shows XP as you earn it and
        // levels you up on the spot). The bar only redraws on a full profile re-send: on a level-up,
        // ApplyLevelUpEffects does that re-send via the JobLevelUp presentation (stat rescale + celebration +
        // full-screen UI) and moves the bar to the new level; otherwise a plain silent ActivateProfile moves
        // it. The incremental experience packets above move the +XP text/level number but NOT the bar — the
        // client outright ignores ClientUpdatePacketUpdateProfileExperience (op38/14: its handler has no case
        // for that sub-opcode). EITHER re-send clears the client's ability toolbar, so RestoreWeaponToolbar()
        // re-sends it right after — that restore is what keeps the ranged auto-fire alive across the re-send.
        // Both the per-kill AND the post-level-up firing wedges were a profile re-send with no toolbar restore.
        if (leveled)
            ApplyLevelUpEffects();
        else
            RefreshActiveProfile();

        RestoreWeaponToolbar();
    }

    // Re-send the active job's weapon toolbar (op36/5 SetDefinition). A profile re-send
    // (ActivateProfile / JobLevelUp) clears the client's ability slots, so this must follow any such re-send
    // or the ranged auto-fire has no ability to repeat and wedges — the same toolbar restore a job-swap does.
    private void RestoreWeaponToolbar()
    {
        var toolbar = JobWeaponAbilities.BuildToolbar(this, _resourceManager);
        if (toolbar is not null)
            SendTunneled(toolbar);
    }

    // The level-up presentation — stat rescale + HP/mana refill, the particle celebration, and the
    // full-screen JobLevelUp UI. Runs on the spot when a kill levels you (retail behavior). It re-sends the
    // profile, which clears the client's ability toolbar, so callers MUST follow it with
    // RestoreWeaponToolbar or the ranged auto-fire wedges ("after leveling up I can't refire").
    private void ApplyLevelUpEffects()
    {
        RecalculateStats(refill: true);

        // Refresh the trait list to the NEW rank so a level-up that crosses a trait's unlock level flips its
        // padlock on the spot — otherwise the traits carry their login-rank state and only update on relog.
        RefreshTraits();

        // Full-screen job level-up UI (levelup_<job>.gfx) via the "JobLevelUp" client event — ClientUpdate
        // 38/15: the client reads one length-prefixed payload and parses it as the active profile.
        using var jluWriter = new PacketWriter();
        ActiveProfile.Serialize(jluWriter);
        SendTunneled(new ClientUpdatePacketJobLevelUp { Payload = jluWriter.Buffer });

        // Fire the particle burst a few ticks LATER instead of in this same batch. Sent alongside the
        // JobLevelUp packet, the burst raced the full-screen presentation's scene setup and was sometimes
        // wiped before it rendered ("effects sometimes won't show when leveling up"). Deferring it onto the
        // tick loop lands it cleanly on the character every time — still one clean burst (no 3-4 repeats).
        _levelUpBurstAtTicks = Environment.TickCount64 + LevelUpBurstDelayMs;
        _levelUpBurstDeadlineTicks = Environment.TickCount64 + LevelUpBurstMaxWaitMs;
    }

    // Rebuild the active profile's Traits list to the current rank so newly-unlocked traits show after a
    // level-up / job-swap, not just on relog. No-op for jobs without trait data. Call before any profile re-send.
    public void RefreshTraits()
    {
        var traits = Combat.JobKits.Active(this)?.BuildTraitEntries(ActiveProfile.Rank);
        if (traits is not null)
            ActiveProfile.AbilityExperiences = traits;
    }

    // Builds the active job's ability-set experience entry (drives the native job XP bar / level-up).
    private AbilityExperience BuildJobAbilityExperience()
    {
        var p = ActiveProfile;
        return new AbilityExperience
        {
            Present = 1,                 // non-zero = a present/valid entry (0 terminates the profile list)
            NameId = p.NameId,
            DescriptionId = p.DescriptionId,
            IconId = p.Icon,
            Level = p.Rank,
            Progress = p.LevelXpRaw,
            TotalForLevel = JobLeveling.XpForLevel(p.Rank),
        };
    }

    private const int LevelUpCompositeEffect = 15117; // PFX_levelup_big (retail level-up particle burst)

    // Re-sends the active job's serialized profile (ClientUpdatePacketActivateProfile) so the client
    // refreshes the Jobs panel level + XP bar from the authoritative Rank/RankPercent. An optional
    // composite effect plays on the player (used for the level-up celebration).
    public void RefreshActiveProfile(int compositeEffect = 0)
    {
        using var writer = new PacketWriter();
        ActiveProfile.Serialize(writer);

        SendTunneled(new ClientUpdatePacketActivateProfile
        {
            Payload = writer.Buffer,
            Attachments = GetAttachments(),
            Animation = 0,
            CompositeEffect = compositeEffect
        });
    }

    // Re-send the currently-equipped weapon (slot 7) — the ClientUpdatePacketEquipItem +
    // PlayerUpdatePacketEquipItemChange pair the inventory equip flow sends. Manually re-equipping the bow
    // is what players found un-freezes the ranged auto-fire after a kill: the weapon re-attach (WieldType)
    // resets the client's wield/combat state WITHOUT the profile re-activation that itself froze firing.
    public void ResendEquippedWeapon()
    {
        if (!ActiveProfile.Items.TryGetValue(7, out var weaponProfileItem))
            return;

        var item = Items.SingleOrDefault(x => x.Id == weaponProfileItem.Id);
        if (item is null || !_resourceManager.ClientItemDefinitions.TryGetValue(item.Definition, out var def))
            return;
        if (!_resourceManager.ItemClasses.TryGetValue(def.Class, out var itemClass))
            return;

        var equip = new ClientUpdatePacketEquipItem
        {
            Guid = item.Id,
            ProfileId = ActiveProfileId,
            Equip = true,
        };
        equip.Attachment.ModelName = def.ModelName;
        equip.Attachment.TextureAlias = def.TextureAlias;
        equip.Attachment.TintAlias = def.TintAlias;
        equip.Attachment.TintId = item.Tint == 0 ? def.Icon.TintId : item.Tint;
        equip.Attachment.CompositeEffectId = def.CompositeEffectId;
        equip.Attachment.Slot = 7;
        SendTunneled(equip);

        SendTunneledToVisible(new PlayerUpdatePacketEquipItemChange
        {
            Guid = Guid,
            Id = item.Id,
            Attachment = equip.Attachment,
            ProfileId = ActiveProfileId,
            WieldType = itemClass.WieldType,
        }, sendToSelf: true);
    }

    // Recomputes level-scaled character stats from the active job's Rank, pushes them to the client and
    // caches them in Stats. When refill is set (login,
    // level-up) current HP/mana are topped to the new maximum; otherwise they're only clamped down.
    public void RecalculateStats(bool refill = false)
    {
        int level = ActiveProfile.Rank;
        int maxHealth = JobLeveling.MaxHealth(level);
        int maxMana = JobLeveling.MaxMana(level);

        // Run-speed traits: Archer Reflexes (L15) and Ninja's Grace (L10). (Reflexes' dodge half is rolled on
        // the mob's attack; Ninja's Grace regen rides the normal HitPointRegen.)
        float moveSpeed = 8f;
        if (Combat.ArcherWeaponAbilities.HasTrait(this, Combat.ArcherWeaponAbilities.ReflexesLevel))
            moveSpeed *= Combat.ArcherWeaponAbilities.ReflexesSpeedMultiplier;
        if (Combat.NinjaWeaponAbilities.HasTrait(this, Combat.NinjaWeaponAbilities.NinjasGraceLevel))
            moveSpeed *= Combat.NinjaWeaponAbilities.NinjasGraceSpeedMultiplier;

        // Crit chance/multiplier: mirrors the exact per-job trait logic in
        // AbilityPacketClientRequestStartAbilityHandler's ApplyXTraitDamage functions (Sanctuary.Gateway) -
        // kept in sync here (not just computed inline there) for two reasons: (a) so the client's own
        // displayed stat reflects a trait unlock instead of staying at whatever ClientPcData set at login
        // forever, and (b) CombatPacketAutoAttackTargetHandler's direct click-to-attack path reads THESE
        // Stats values for its own crit roll (unlike the ability-bar path, which recomputes the chain fresh
        // per hit) - before this, MeleeCriticalHitChance/Multiplier were only ever initialized to 0 at
        // character load and never touched again, so a basic attack triggered by clicking an enemy directly
        // could never crit at all, trait or no trait. Only one job is ever active, so only one branch fires.
        var meleeCritChance = 0;
        var meleeCritMultiplier = 1f;
        if (Combat.ArcherWeaponAbilities.HasTrait(this, Combat.ArcherWeaponAbilities.PrecisionLevel))
        {
            meleeCritChance = Combat.ArcherWeaponAbilities.BaseCritChancePercent + Combat.ArcherWeaponAbilities.PrecisionCritChanceBonus;
            meleeCritMultiplier = Combat.ArcherWeaponAbilities.BaseCritMultiplier;
            if (Combat.ArcherWeaponAbilities.HasTrait(this, Combat.ArcherWeaponAbilities.MarksmanshipLevel))
                meleeCritMultiplier += Combat.ArcherWeaponAbilities.MarksmanshipCritBonus;
        }
        else if (Combat.BrawlerWeaponAbilities.HasTrait(this, Combat.BrawlerWeaponAbilities.BruisingStrikesLevel))
        {
            meleeCritChance = Combat.BrawlerWeaponAbilities.BaseCritChancePercent + Combat.BrawlerWeaponAbilities.BruisingStrikesCritChanceBonus;
            meleeCritMultiplier = Combat.BrawlerWeaponAbilities.BaseCritMultiplier;
            if (Combat.BrawlerWeaponAbilities.HasTrait(this, Combat.BrawlerWeaponAbilities.SavvyLevel))
                meleeCritMultiplier += Combat.BrawlerWeaponAbilities.SavvyCritBonus;
        }
        else if (Combat.WarriorWeaponAbilities.HasTrait(this, Combat.WarriorWeaponAbilities.PiercingStrikesLevel))
        {
            meleeCritChance = Combat.WarriorWeaponAbilities.BaseCritChancePercent + Combat.WarriorWeaponAbilities.PiercingStrikesCritChanceBonus;
            meleeCritMultiplier = Combat.WarriorWeaponAbilities.BaseCritMultiplier;
        }
        else if (Combat.WizardWeaponAbilities.HasTrait(this, Combat.WizardWeaponAbilities.GeniusLevel))
        {
            meleeCritChance = Combat.WizardWeaponAbilities.BaseCritChancePercent + Combat.WizardWeaponAbilities.GeniusCritChanceBonus;
            meleeCritMultiplier = Combat.WizardWeaponAbilities.BaseCritMultiplier;
        }
        // Medic has no baseline "unlocks crit chance" trait (none of its real 4 traits are a crit-chance
        // grant the way Warrior/Archer/Wizard/Brawler's are - see MedicWeaponAbilities.cs) - meleeCritChance
        // stays 0 for Medic.

        UpdateCharacterStats(
            new CharacterStat(CharacterStatId.MaxHealth, maxHealth),
            new CharacterStat(CharacterStatId.MaxMovementSpeed, moveSpeed),
            new CharacterStat(CharacterStatId.WeaponRange, 5f),
            new CharacterStat(CharacterStatId.HitPointRegen, JobLeveling.HitPointRegen(level)),
            new CharacterStat(CharacterStatId.MaxMana, maxMana),
            new CharacterStat(CharacterStatId.ManaRegen, JobLeveling.ManaRegen(level)),
            new CharacterStat(CharacterStatId.MeleeChanceToHit, 100),
            new CharacterStat(CharacterStatId.MeleeWeaponDamageMultiplier, 1f),
            new CharacterStat(CharacterStatId.MeleeHandToHandDamage, 1),
            new CharacterStat(CharacterStatId.EquippedMeleeWeaponDamage, 1),
            new CharacterStat(CharacterStatId.MeleeAttackIntervalMs, 2000),
            new CharacterStat(CharacterStatId.MeleeCriticalHitChance, meleeCritChance),
            new CharacterStat(CharacterStatId.MeleeCriticalHitMultiplier, meleeCritMultiplier),
            new CharacterStat(CharacterStatId.AbilityCriticalHitChance, meleeCritChance),
            new CharacterStat(CharacterStatId.DamageMultiplier, 1f),
            new CharacterStat(CharacterStatId.HealingMultiplier, 1f),
            new CharacterStat(CharacterStatId.AbilityCriticalHitMultiplier, meleeCritMultiplier),
            new CharacterStat(CharacterStatId.HeadInflationPercent, 100),
            new CharacterStat(CharacterStatId.RangeMultiplier, 1f),
            new CharacterStat(CharacterStatId.FactoryProductionModifier, 1f),
            new CharacterStat(CharacterStatId.FactoryYieldModifier, 1f),
            new CharacterStat(CharacterStatId.InCombatHitPointRegen, 6),
            new CharacterStat(CharacterStatId.InCombatManaRegen, 4));

        if (refill || CurrentHitpoints > maxHealth || CurrentHitpoints <= 0)
            CurrentHitpoints = maxHealth;
        if (refill || CurrentMana > maxMana)
            CurrentMana = maxMana;

        SendHealthMana();
    }

    // Pushes current HP and mana (with their level-scaled maximums) to the client. Sends both the
    // self-HUD packets (ClientUpdate 38/1 hitpoints, 38/13 mana) AND the over-head bar packets
    // (PlayerUpdate 35/5 hitpoints, 35/9 mana) so both the HUD and the bar over the character update.
    public void SendHealthMana()
    {
        int maxHealth = Stats.TryGetValue(CharacterStatId.MaxHealth, out var mh) ? mh.Int : CurrentHitpoints;
        int maxMana = Stats.TryGetValue(CharacterStatId.MaxMana, out var mm) ? mm.Int : CurrentMana;

        // Self HUD.
        SendTunneled(new ClientUpdatePacketHitpoints { CurrentHitpoints = CurrentHitpoints, MaxHitpoints = maxHealth });
        SendTunneled(new ClientUpdatePacketMana { CurrentMana = CurrentMana, MaxMana = maxMana });

        // Over-head bars, visible to self + nearby players.
        SendTunneledToVisible(new PlayerUpdatePacketUpdateHitpoints
        {
            Guid = Guid,
            Hitpoints = CurrentHitpoints,
            MaxHitpoints = maxHealth
        }, sendToSelf: true);

        SendTunneledToVisible(new PlayerUpdatePacketUpdateMana
        {
            Guid = Guid,
            CurrentMana = CurrentMana,
            MaxMana = maxMana
        }, sendToSelf: true);
    }

    // Delay (ms) between the JobLevelUp full-screen UI and the particle burst — a few ticks, long
    // enough that the burst isn't in the presentation's setup frame (where it was getting wiped) but short
    // enough to still read as part of the level-up moment.
    private const int LevelUpBurstDelayMs = 300;

    // How long the deferred burst keeps retrying while the player isn't renderable (mid zone-transfer) before
    // giving up — long enough to outlast a dungeon-return teleport, short enough to never fire "much later".
    private const int LevelUpBurstMaxWaitMs = 8000;

    // Wall-clock deadline (ticks) for the retry above; set alongside _levelUpBurstAtTicks.
    private long _levelUpBurstDeadlineTicks;

    // When to fire the deferred level-up burst (Environment.TickCount64), or 0 for none. Set by
    // ApplyLevelUpEffects, consumed by LevelUpBurstTick on the tick loop.
    private long _levelUpBurstAtTicks;

    // Tick-loop driver for the deferred level-up burst (a single PFX_levelup_big ~2s burst). Firing
    // it off the tick instead of in the JobLevelUp packet's batch is what makes it show reliably.
    private void LevelUpBurstTick()
    {
        if (_levelUpBurstAtTicks == 0 || Environment.TickCount64 < _levelUpBurstAtTicks)
            return;

        // Not renderable yet (mid zone-transfer — e.g. leveling on the last dungeon kill just before the return
        // teleport)? Retry on later ticks instead of dropping the burst, up to a deadline. Dropping it here is
        // why the level-up effect "sometimes" didn't play — the deferred burst landed during a transfer with
        // Visible == false and was lost.
        if (!Visible && Environment.TickCount64 < _levelUpBurstDeadlineTicks)
        {
            _levelUpBurstAtTicks = Environment.TickCount64 + LevelUpBurstDelayMs;
            return;
        }

        _levelUpBurstAtTicks = 0;
        FireLevelUpBurst();
    }

    // One level-up particle burst at the player's current position (guarded against post-logout sends).
    private void FireLevelUpBurst()
    {
        if (!Visible)
            return;

        SendTunneledToVisible(new PlayerUpdatePacketPlayCompositeEffect
        {
            Guid = Guid,
            CompositeEffectId = LevelUpCompositeEffect,
            Position = Position
        }, sendToSelf: true);
    }

    // Rough world-space velocity (units/sec) estimated from consecutive client position updates. Used to
    // lag-compensate projectile muzzle origins so a moving-and-shooting player's shots come out from where
    // the client actually renders them, not from the ~1-RTT-stale last-known position. Zeroed while standing.
    public Vector3 EstimatedVelocity { get; private set; }
    private Vector4 _lastVelPosition;
    private DateTime _lastVelTime = DateTime.MinValue;

    // Best estimate of where the client is rendering the player RIGHT NOW: the last-known position plus the
    // motion since that update arrived (staleness) plus a short downlink allowance for how long our reply
    // takes to reach the client. Used to spawn projectiles at the player even while they're running.
    public Vector4 PredictPosition(float downlinkSeconds)
    {
        var v = EstimatedVelocity;
        if (v == Vector3.Zero)
            return Position;

        var stale = _lastVelTime == DateTime.MinValue ? 0f : (float)(DateTime.UtcNow - _lastVelTime).TotalSeconds;
        if (stale < 0f || stale > 1f) stale = 0f; // ignore gaps (stood still / just spawned)
        var t = stale + downlinkSeconds;
        return new Vector4(Position.X + v.X * t, Position.Y + v.Y * t, Position.Z + v.Z * t, 1f);
    }

    public void UpdatePosition(Vector4 position, Quaternion rotation, bool updateZoneArea = true)
    {
        var now = DateTime.UtcNow;
        if (_lastVelTime != DateTime.MinValue)
        {
            var dt = (float)(now - _lastVelTime).TotalSeconds;
            if (dt > 0.001f && dt < 0.5f)
            {
                var v = new Vector3(
                    (position.X - _lastVelPosition.X) / dt,
                    (position.Y - _lastVelPosition.Y) / dt,
                    (position.Z - _lastVelPosition.Z) / dt);
                // Cap to plausible run speed so teleports/warps don't produce a huge spurious velocity.
                EstimatedVelocity = v.Length() < 20f ? v : Vector3.Zero;
            }
            else
            {
                EstimatedVelocity = Vector3.Zero;
            }
        }
        _lastVelPosition = position;
        _lastVelTime = now;

        Position = position;
        Rotation = rotation;

        Mount?.UpdatePosition(position, rotation, updateZoneArea);

        if (Visible)
        {
            UpdateZoneTile();

            if (updateZoneArea)
                UpdateZoneArea();
        }
    }

    private void UpdateZoneTile()
    {
        var newZoneTile = Zone.GetTileFromPosition(Position);

        if (newZoneTile == ZoneTile)
            return;

        Zone.UpdateEntityZoneTile(this, ZoneTile, newZoneTile);

        ZoneTile = newZoneTile;
    }

    public void TeleportToZone(IZone zone, Vector4 position, Quaternion rotation)
    {
        // Preserve the original hardcoded values for existing (deep-mines test) callers.
        TeleportToZone(zone, position, rotation, "sky_deep_mines.xml", 214);
    }

    // INSTANCE (Frostfang Fury): overload with explicit sky/geometry so real zone transfers (e.g. the
    // sg_random_encounter_clearing arena) can use the destination world's own sky (null) instead of the
    // deep-mines test values. This is the PROPER server-side zone handoff — tiles/visibility rebuilt,
    // OverrideUpdateRadius=true (the client's case-31 handler feeds this to ActorManager::SetOverrideUpdateRadius;
    // without it NPCs in the new world get distance-culled -> the "invisible wolves" bug).
    public void TeleportToZone(IZone zone, Vector4 position, Quaternion rotation, string? sky, int geometryId)
    {
        // Leaving a house is a same-zone move on paper, but the house instance has to be torn down
        // properly, so it takes the full path rather than the reposition shortcut.
        if (Zone == zone && CurrentHouseGuid == 0)
        {
            // Same-zone teleport: skip zone membership changes, just reset visibility and reposition.
            foreach (var visiblePlayer in VisiblePlayers)
                visiblePlayer.Value.OnRemoveVisiblePlayers([this]);

            OnRemoveVisibleNpcs(VisibleNpcs.Values);
            OnRemoveVisiblePlayers(VisiblePlayers.Values);

            ZoneTile.Entities.Remove(Guid, out _);
            ZoneTile = ZoneTile.Empty;

            Visible = false;

            UpdatePosition(position, rotation);

            var sameZonePacket = new PacketClientBeginZoning
            {
                Name = Zone.Name,
                Position = position,
                Rotation = rotation,
                Sky = sky,               // honor the caller's sky (was hardcoded deep-mines) — the 3-arg
                Id = Zone.Id,            // overload still passes the deep-mines values for its old callers
                GeometryId = geometryId, // (was hardcoded 214)
                OverrideUpdateRadius = true
            };

            SendTunneled(sameZonePacket);
            return;
        }

        if (Zone is StartingZone && CurrentHouseGuid == 0)
        {
            StartingZonePosition = Position;
            StartingZoneRotation = Rotation;
        }

        if (Mount is not null)
            Mount.TeleportToZone(zone, position, rotation);

        // Alert/Remove visible entities
        foreach (var visiblePlayer in VisiblePlayers)
            visiblePlayer.Value.OnRemoveVisiblePlayers([this]);

        OnRemoveVisibleNpcs(VisibleNpcs.Values);
        OnRemoveVisiblePlayers(VisiblePlayers.Values);

        ZoneTile.Entities.Remove(Guid, out _);

        Zone.TryRemovePlayer(Guid);

        // Add to new zone/zonetile

        zone.TryAddPlayer(this);

        // Teleport to new zone

        Visible = false;

        Zone = zone;

        ZoneTile = ZoneTile.Empty;

        UpdatePosition(position, rotation);

        // An explicit sky from the caller wins; null means "use the destination world's own sky", which is
        // what the zone-definition lookup resolves (falling back to seaside for worlds that declare none).
        var resolvedSky = sky ?? (_resourceManager.Zones.TryGetValue(zone.Id, out var zoneDefinition)
            ? zoneDefinition.Sky ?? "sky_seaside24.xml"
            : "sky_seaside24.xml");

        var packetClientBeginZoning = new PacketClientBeginZoning
        {
            Name = Zone.Name,
            Position = position,
            Rotation = rotation,
            Sky = resolvedSky,
            Id = Zone.Id,
            GeometryId = geometryId,
            OverrideUpdateRadius = true
        };

        SendTunneled(packetClientBeginZoning);
    }

    private void UpdateZoneArea()
    {
        if (Zone is not StartingZone startingZone)
            return;

        var zoneAreaId = startingZone.GetZoneAreaId(Position);

        if (ZoneAreaId == zoneAreaId)
            return;

        ZoneAreaId = zoneAreaId;

        var packetPOIChangeMessage = new PacketPOIChangeMessage
        {
            ZoneId = zoneAreaId
        };

        SendTunneled(packetPOIChangeMessage);
    }

    public void UpdateCharacterStats(params CharacterStat[] characterStats)
    {
        var clientUpdatePacketUpdateStat = new ClientUpdatePacketUpdateStat
        {
            Guid = Guid
        };

        clientUpdatePacketUpdateStat.Stats.AddRange(characterStats);

        SendTunneled(clientUpdatePacketUpdateStat);

        foreach (var characterStat in characterStats)
        {
            Stats[characterStat.Id] = characterStat;

            if (characterStat.Id == CharacterStatId.MaxMovementSpeed)
            {
                var playerUpdatePacketExpectedSpeed = new PlayerUpdatePacketExpectedSpeed
                {
                    Guid = Guid,
                    ExpectedSpeed = characterStat.Float
                };

                SendTunneledToVisible(playerUpdatePacketExpectedSpeed);
            }
        }
    }

    #endregion

    #region Events

    public void OnAddVisibleNpcs(params IEnumerable<Npc> npcs)
    {
        var compatibleNpcs = npcs
            .Where(CanSeeNpc)
            .GroupBy(npc => npc.Guid)
            .Select(group => group.First())
            .ToList();

        foreach (var npc in compatibleNpcs)
        {
            if (npc is Mount)
                continue;

            // A quest's pickups are world entities shared by everyone, but they belong to whoever is on
            // the quest: don't send them to players who aren't. Otherwise the Gifting Tree's presents sit
            // under the tree for the whole server whether anyone is trick-or-treating or not.
            if (!CanSeeQuestCollectible(npc.Guid))
                continue;

            var playerUpdatePacketAddNpc = npc.GetAddNpcPacket();

            // Vendors bake a static badge into the AddNpc packet itself (npc.NotificationImageSetId).
            // Quest badges are per-player, so override that field per-recipient here - this is likely
            // the primary mechanism the client uses for the badge, not just the separate NotificationInfo packet.
            playerUpdatePacketAddNpc.NotificationImageSetId = GetNotificationImageId(npc);

            // EXPERIMENT: Unknown68 sits immediately next to NotificationImageSetId in the wire
            // format - testing whether it's the "quest this NPC offers" id, since that's the only
            // unexplored field adjacent to a field we've already confirmed matters.
            playerUpdatePacketAddNpc.Unknown68 = GetOfferedQuestId(npc);

            SendTunneled(playerUpdatePacketAddNpc);

            // Per-npc plate colour, which has to follow the AddNpc for EVERY viewer - see Npc.ClientDisposition.
            if (npc.ClientDisposition is { } clientDisposition)
                SendTunneled(new PlayerUpdatePacketUpdateDisposition { Guid = npc.Guid, Disposition = clientDisposition });

            // Same story for a permanent attached effect (the snowball piles' sparkle).
            if (npc.AttachedEffectId > 0)
                SendTunneled(new PlayerUpdatePacketAddEffectTagCompositeEffect
                {
                    Guid = npc.Guid,
                    TagId = npc.AttachedEffectTagId,
                    CompositeEffectId = npc.AttachedEffectId,
                    SourceGuid = npc.Guid,
                });

            // Damageable hostiles (quest kill targets, world combat NPCs) need their attack cursor
            // (NpcRelevance) + health bar as soon as they come into view, not just at zone load.
            if (npc.IsDamageable && npc.SendCombatRelevance)
            {
                Zone.SendNpcRelevance(this, npc);

                // Only push health stats for enemies that are SUPPOSED to show a bar. Sending them is what
                // makes the client draw one, so a damageable-but-barless enemy (the Snowman Invaders) has to
                // be skipped here rather than just flagged.
                if (npc.ShowHealthBar)
                    Zone.SendNpcHealth(this, npc);
            }
        }

        var playerUpdatePacketNpcRelevance = new PlayerUpdatePacketNpcRelevance();

        foreach (var npc in compatibleNpcs)
        {
            if (npc.CursorId == 0)
                continue;

            // HasCursor is what makes the NPC selectable client-side. Badge-bearing NPCs (quest givers,
            // vendors) answer on their badge, which already tracks whether they have anything to say.
            // Everything driven by a plain InteractAction - quest collectibles, gathering nodes, dungeon
            // entrances - carries NO badge, so keying the cursor purely off the badge left them
            // permanently un-clickable: a quest's sparkling present rendered, and the tracker pointed
            // straight at it, but every click was refused.
            var hasCursor = GetNotificationImageId(npc) != 0 || npc.InteractAction is not null;

            playerUpdatePacketNpcRelevance.Entries.Add(new PlayerUpdatePacketNpcRelevance.Entry
            {
                Guid = npc.Guid,
                Unknown = true,
                CursorId = npc.CursorId,
                HasCursor = hasCursor
            });
        }

        if (playerUpdatePacketNpcRelevance.Entries.Count > 0)
            SendTunneled(playerUpdatePacketNpcRelevance);

        var notifications = new PlayerUpdatePacketAddNotifications();

        foreach (var npc in compatibleNpcs)
        {
            // Combat-encounter "Battle Starter" badge (img-24): red crossed-swords over the head + red minimap
            // dot. Type 3 = combat category; Unknown3=7 / Unknown10=1 are the live 2014 combat-badge values.
            if (npc.CombatEncounterBadgeImageId != 0)
            {
                notifications.Notifications.Add(new NotificationInfo
                {
                    Guid = npc.Guid,
                    ImageId = npc.CombatEncounterBadgeImageId,
                    Type = 3,
                    Unknown3 = 7,
                    Unknown10 = true,
                });
                continue;
            }

            var imageId = GetNotificationImageId(npc);

            if (imageId == 0)
                continue;

            notifications.Notifications.Add(new NotificationInfo
            {
                Guid = npc.Guid,
                Combat = false,
                ImageId = imageId,
                NameId = npc.NameId,
                SubTextId = npc.SubTextNameId,
            });
        }

        if (notifications.Notifications.Count > 0)
            SendTunneled(notifications);

        foreach (var npc in compatibleNpcs)
            VisibleNpcs.TryAdd(npc.Guid, npc);
    }

    // Quest badges are per-player (unlike vendor badges, which are static on the Npc entity),
    // since they depend on this player's own quest progress.
    // True unless this NPC is a quest pickup the player has no business seeing. Non-collectibles are
    // always visible. A pickup shows only while its quest is active AND its own goal is the one being
    // worked on, so they appear on accept and vanish once the goal is ticked off or the quest ends.
    public bool CanSeeQuestCollectible(ulong npcGuid)
    {
        if (!_resourceManager.Quests.Collectibles.TryGetValue(npcGuid, out var location))
            return true;

        if (!Quests.TryGetValue(location.QuestId, out var completed) || completed)
            return false;

        int goalIndex = QuestGoalProgress.TryGetValue(location.QuestId, out var progress) ? progress : 0;
        return goalIndex == location.GoalIndex;
    }

    public int GetNotificationImageId(Npc npc)
    {
        var quests = _resourceManager.Quests;

        // Giver: "!" if this NPC gives a quest the player can currently take.
        if (quests.ByGiver.TryGetValue(npc.Guid, out var giverQuestIds))
        {
            foreach (var questId in giverQuestIds)
            {
                if (quests.TryGet(questId, out var quest) && quest.IsOfferableFor(Quests))
                    return quest.NotificationAvailable;
            }
        }

        // Target: "?" if the player has an active (accepted, not completed) quest that turns in here.
        if (quests.ByTarget.TryGetValue(npc.Guid, out var targetQuestIds))
        {
            foreach (var questId in targetQuestIds)
            {
                if (Quests.TryGetValue(questId, out var completed) && !completed && quests.TryGet(questId, out var quest))
                    return quest.NotificationActive;
            }
        }

        // Daily quest already done today: the giver wears the greyed "repeatable, not right now" badge
        // instead of going bare, so it still reads as a quest NPC you should come back to.
        if (quests.ByGiver.TryGetValue(npc.Guid, out var dailyQuestIds))
        {
            foreach (var questId in dailyQuestIds)
            {
                if (!Quests.TryGetValue(questId, out var isDone) || !isDone)
                    continue;

                if (quests.TryGet(questId, out var doneQuest) && doneQuest.IsDaily && doneQuest.NotificationCompleted != 0)
                    return doneQuest.NotificationCompleted;
            }
        }

        return npc.NotificationImageSetId;
    }

    // AddNpc.Unknown68 sits next to NotificationImageSetId; used to carry the "quest this NPC offers"
    // id. Returns the first currently-offerable quest this NPC gives, else 0.
    public int GetOfferedQuestId(Npc npc)
    {
        var quests = _resourceManager.Quests;

        if (quests.ByGiver.TryGetValue(npc.Guid, out var giverQuestIds))
        {
            foreach (var questId in giverQuestIds)
            {
                if (quests.TryGet(questId, out var quest) && quest.IsOfferableFor(Quests))
                    return questId;
            }
        }

        return 0;
    }

    public void OnAddVisiblePlayers(params IEnumerable<Player> players)
    {
        var compatiblePlayers = players
            .Where(player => player.Guid != Guid && CanShareInstance(this, player))
            .GroupBy(player => player.Guid)
            .Select(group => group.First())
            .ToList();

        foreach (var player in compatiblePlayers)
        {
            if (player.Mount is not null)
            {
                var addPc = player.GetAddPcPacket();
                addPc.MountGuid = 0;
                addPc.MountSeat = -1;
                addPc.MountQueuePosition = -1;
                addPc.NameVerticalOffset = 0;

                SendTunneled(addPc);
                SendTunneled(player.Mount.GetAddNpcPacket());
                SendTunneled(player.Mount.GetMountResponsePacket());
            }
            else
                SendTunneled(player.GetAddPcPacket());
        }

        foreach (var player in compatiblePlayers)
            VisiblePlayers.TryAdd(player.Guid, player);
    }

    public static bool CanShareInstance(Player left, Player right)
    {
        return left.CurrentHouseGuid == right.CurrentHouseGuid;
    }

    public bool CanSeeNpc(Npc npc)
    {
        return npc.CurrentHouseGuid == CurrentHouseGuid;
    }

    private void RemovePlayersFromOtherInstances()
    {
        var incompatiblePlayers = VisiblePlayers.Values
            .Where(player => !CanShareInstance(this, player))
            .ToList();

        foreach (var player in incompatiblePlayers)
        {
            player.OnRemoveVisiblePlayers([this]);
            OnRemoveVisiblePlayers([player]);
        }
    }

    private void RemoveNpcsFromOtherInstances()
    {
        var incompatibleNpcs = VisibleNpcs.Values
            .Where(npc => !CanSeeNpc(npc))
            .ToList();

        if (incompatibleNpcs.Count > 0)
            OnRemoveVisibleNpcs(incompatibleNpcs);
    }

    public void OnRemoveVisibleNpcs(params IEnumerable<Npc> npcs)
    {
        foreach (var npc in npcs)
        {
            if (npc is Mount mount)
            {
                var playerUpdatePacketRemovePlayerGracefully = new PlayerUpdatePacketRemovePlayerGracefully();

                playerUpdatePacketRemovePlayerGracefully.Guid = npc.Guid;

                playerUpdatePacketRemovePlayerGracefully.Animate = false;
                playerUpdatePacketRemovePlayerGracefully.Delay = 0;
                playerUpdatePacketRemovePlayerGracefully.EffectDelay = 0;
                playerUpdatePacketRemovePlayerGracefully.CompositeEffectId = 46;
                playerUpdatePacketRemovePlayerGracefully.Duration = 1000;

                SendTunneled(playerUpdatePacketRemovePlayerGracefully);
            }
            else if (npc.GracefulRemoval is { } graceful)
            {
                // Live-server despawn (04-01 capture): the ONE graceful-remove packet carries the whole
                // death presentation — Animate=true plays the model's own death clip client-side, the
                // composite effect (5017 poof) fires and the actor despawns after Delay ms. No separate
                // SetAnimation / PlayCompositeEffect packets are needed (the real server sends none).
                var packet = new PlayerUpdatePacketRemovePlayerGracefully();

                packet.Guid = npc.Guid;

                packet.Animate = graceful.Animate;
                packet.Delay = graceful.Delay;
                packet.EffectDelay = graceful.EffectDelay;
                packet.CompositeEffectId = graceful.EffectId;
                packet.Duration = graceful.Duration;

                SendTunneled(packet);
            }
            else
            {
                var playerUpdatePacketRemovePlayer = new PlayerUpdatePacketRemovePlayer();

                playerUpdatePacketRemovePlayer.Guid = npc.Guid;

                SendTunneled(playerUpdatePacketRemovePlayer);
            }
        }

        foreach (var npc in npcs)
            VisibleNpcs.TryRemove(npc.Guid, out _);
    }

    public void OnRemoveVisiblePlayers(params IEnumerable<Player> players)
    {
        foreach (var player in players)
        {
            SendTunneled(new PlayerUpdatePacketRemovePlayer { Guid = player.Guid });

            if (player.Mount is not null)
                SendTunneled(new PlayerUpdatePacketRemovePlayer { Guid = player.Mount.Guid });
        }

        foreach (var player in players)
            VisiblePlayers.TryRemove(player.Guid, out _);
    }

    // The NPC radial menu currently on this player's screen. The client answers a menu with the id we
    // gave the option (op26/10 CommandPacketInteractionSelect), which is meaningless without the list
    // that produced it, so the actions are held here until the reply arrives or the next menu opens.
    public sealed record InteractionMenu(ulong Guid, IReadOnlyDictionary<int, Action<Player>> Options);

    public InteractionMenu? OpenInteractionMenu { get; set; }

    // Option ids live well above IInteraction.UniqueId (a small counter over the handful of registered
    // player-to-player interactions), so a menu id can never be mistaken for a registered one.
    private const int NpcInteractionIdBase = 1_000_000;

    public void SendInteractionMenu(Npc npc, IReadOnlyList<NpcInteractionOption> options)
    {
        var packet = new CommandPacketInteractionList();

        packet.List.Guid = npc.Guid;
        packet.List.Name = npc.Name ?? string.Empty;

        var actions = new Dictionary<int, Action<Player>>(options.Count);

        for (int i = 0; i < options.Count; i++)
        {
            var option = options[i];
            int id = NpcInteractionIdBase + i;

            packet.List.Interactions.Add(new InteractionData
            {
                Id = id,
                IconId = option.IconId,
                ButtonText = option.ButtonTextId,
                TooltipId = option.TooltipId
            });

            actions[id] = option.Invoke;
        }

        OpenInteractionMenu = new InteractionMenu(npc.Guid, actions);

        SendTunneled(packet);
    }

    public void OnInteract(Player player)
    {
        var commandPacketInteractionList = new CommandPacketInteractionList();

        commandPacketInteractionList.List.Guid = Guid;

        commandPacketInteractionList.List.Interactions.Add(InspectInteraction.Data);

        if (Friends.Any(x => x.Guid == player.Guid))
        {
            commandPacketInteractionList.List.Interactions.Add(RemoveFriendInteraction.Data);
        }
        else
        {
            commandPacketInteractionList.List.Interactions.Add(AddFriendInteraction.Data);
        }

        if (player.Ignores.Any(x => x.Guid == Guid))
        {
            commandPacketInteractionList.List.Interactions.Add(StopIgnoringInteraction.Data);
        }
        else
        {
            commandPacketInteractionList.List.Interactions.Add(IgnoreInteraction.Data);
        }

        player.SendTunneled(commandPacketInteractionList);
    }

    #endregion

    public int GetFlairShardCompositeEffect()
    {
        const int FlairShardSlot = 13;

        if (ActiveProfile.Items.TryGetValue(FlairShardSlot, out var profileItem))
        {
            var clientItem = Items.FirstOrDefault(x => x.Id == profileItem.Id);

            if (clientItem is not null)
            {
                if (_resourceManager.ClientItemDefinitions.TryGetValue(clientItem.Definition, out var clientItemDefinition))
                    return clientItemDefinition.CompositeEffectId;
            }
        }

        return 0;
    }

    public List<CharacterAttachmentData> GetAttachments()
    {
        var list = new List<CharacterAttachmentData>();

        foreach (var profileItem in ActiveProfile.Items)
        {
            var attachment = GetAttachment(profileItem.Key);

            if (attachment is null)
                continue;

            list.Add(attachment);
        }

        return list;
    }

    // COMBAT WIP: the item-definition id of the weapon currently equipped in the weapon slot (7), or 0 if
    // none. Used to drive the ability toolbar off the equipped weapon (see Combat/NinjaWeaponAbilities).
    public int GetEquippedWeaponDefinitionId()
    {
        if (!ActiveProfile.Items.TryGetValue(7, out var profileItem))
            return 0;

        var clientItem = Items.FirstOrDefault(x => x.Id == profileItem.Id);

        return clientItem?.Definition ?? 0;
    }

    public CharacterAttachmentData? GetAttachment(int slot)
    {
        if (!ActiveProfile.Items.TryGetValue(slot, out var profileItem))
            return null;

        var clientItem = Items.FirstOrDefault(x => x.Id == profileItem.Id);

        if (clientItem is null)
            return null;

        if (!_resourceManager.ClientItemDefinitions.TryGetValue(clientItem.Definition, out var clientItemDefinition))
            return null;

        var compositeEffectId = clientItemDefinition.CompositeEffectId;

        // Update the Weapon composite effect if we have a Flair Shard equipped.
        if (slot == 7)
        {
            var flairShardcompositeEffectId = GetFlairShardCompositeEffect();

            if (flairShardcompositeEffectId > 0)
                compositeEffectId = flairShardcompositeEffectId;
        }

        return new CharacterAttachmentData
        {
            ModelName = clientItemDefinition.ModelName,
            TextureAlias = clientItemDefinition.TextureAlias,
            TintAlias = clientItemDefinition.TintAlias,
            TintId = clientItem.Tint,
            CompositeEffectId = compositeEffectId,
            Slot = clientItemDefinition.Slot
        };
    }

    public PlayerUpdatePacketAddPc GetAddPcPacket()
    {
        var packet = new PlayerUpdatePacketAddPc
        {
            Guid = Guid,

            Name = Name,

            Model = Model,

            ChatBubbleForegroundColor = ChatBubbleForegroundColor,
            ChatBubbleBackgroundColor = ChatBubbleBackgroundColor,
            ChatBubbleSize = ChatBubbleSize,

            Position = Position,
            Rotation = Rotation,

            Attachments = GetAttachments(),

            Head = Head,
            Hair = Hair,

            HairColor = HairColor,
            EyeColor = EyeColor,

            SkinTone = SkinTone,

            FacePaint = FacePaint,
            ModelCustomization = ModelCustomization,

            MaxMovementSpeed = Stats[CharacterStatId.MaxMovementSpeed],

            IsUnderage = Age < 18,
            IsMember = MembershipStatus != 0,

            TemporaryAppearance = TemporaryAppearance,

            ActiveProfileId = ActiveProfileId,

            MountQueuePosition = -1,
            MountSeat = -1,
        };

        var activeTitle = Titles.FirstOrDefault(x => x.Id == ActiveTitle);

        if (activeTitle is not null)
            packet.Title = activeTitle;

        if (Mount is not null)
        {
            packet.MountGuid = Mount.Guid;
            packet.MountSeat = Mount.Seat;
            packet.MountQueuePosition = Mount.QueuePosition;

            packet.NameVerticalOffset = Mount.Definition.NameVerticalOffset;

            Debug.WriteLine($"AddPc: {Name} {Guid} | {Mount.Guid} {Mount.Seat} {Mount.QueuePosition}");
        }

        return packet;
    }


    public void ApplyTemporaryAppearance(int modelId, int durationMs, int effectId = 0)
    {
        TemporaryAppearance = modelId;
        _temporaryAppearanceEffectId = effectId;

        if (durationMs > 0)
            TemporaryAppearanceExpiresAt = DateTimeOffset.UtcNow.AddMilliseconds(durationMs);

        if (effectId != 0)
            SendTunneledToVisible(new PlayerUpdatePacketPlayCompositeEffect { Guid = Guid, CompositeEffectId = effectId, Position = Position, Clear = false }, true);

        SendTunneledToVisible(new PlayerUpdatePacketUpdateTemporaryAppearance { Guid = Guid, TemporaryAppearance = modelId }, true);
    }

    public void RemoveTemporaryAppearance()
    {
        TemporaryAppearance = 0;
        TemporaryAppearanceExpiresAt = null;

        if (_temporaryAppearanceEffectId != 0)
        {
            SendTunneledToVisible(new PlayerUpdatePacketPlayCompositeEffect { Guid = Guid, CompositeEffectId = _temporaryAppearanceEffectId, Position = Position, Clear = false }, true);
            _temporaryAppearanceEffectId = 0;
        }

        SendTunneledToVisible(new PlayerUpdatePacketRemoveTemporaryAppearance { Guid = Guid }, true);
    }

    #region Equatable

    public bool Equals(IEntity? other)
    {
        return Guid == other?.Guid;
    }

    public override bool Equals([NotNullWhen(true)] object? obj)
    {
        if (obj is Player other)
            return Equals(other);

        return false;
    }

    public override int GetHashCode()
    {
        return Guid.GetHashCode();
    }

    public static bool operator ==(Player left, Player right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(Player left, Player right)
    {
        return !(left == right);
    }

    #endregion

    public void Dispose()
    {
        foreach (var visiblePlayer in VisiblePlayers)
            visiblePlayer.Value.OnRemoveVisiblePlayers([this]);

        if (Mount is not null)
        {
            Mount.ZoneTile.Entities.Remove(Mount.Guid, out _);

            Zone.TryRemoveNpc(Mount.Guid);
            Mount = null;
        }

        ZoneTile.Entities.Remove(Guid, out _);

        ZoneTile.Entities.Remove(Guid, out _);
        Zone.TryRemovePlayer(Guid);
    }
}
