using System;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;

using Sanctuary.Core.Collections;
using Sanctuary.Core.IO;
using Sanctuary.Game.ChatCommands;
using Sanctuary.Game.Helpers;
using Sanctuary.Game.Interactions;
using Sanctuary.Game.Resources.Definitions;
using Sanctuary.Game.Resources.Definitions.Combat;
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
    public ChatCommandRole ChatCommandRole => ChatHelper.GetRoleFromFlags(IsAdmin, IsMod);
    public DateTimeOffset? MutedUntil { get; set; }

    public ClientPcProfile ActiveProfile =>
        Profiles.FirstOrDefault(x => x.Id == ActiveProfileId) ?? Profiles.First();

    public Mount? Mount { get; set; }

    public List<FriendData> Friends { get; set; } = [];
    public List<IgnoreData> Ignores { get; set; } = [];

    public ConcurrentSet<ulong> IncomingFriendRequests { get; } = [];
    public ConcurrentSet<ulong> IncomingGuildInvites { get; } = [];

    // --- Quests ---

    // Database character id, used to persist quest state (DbCharacterQuest).
    public ulong CharacterId { get; set; }

    // The NPC the player most recently interacted with, and when (gates quest offer/turn-in).
    public ulong LastInteractNpcGuid { get; set; }
    public DateTime LastInteractAt { get; set; }

    // When the player last accepted a quest; used to ignore a stray abandon fired right after accept.
    public DateTime LastQuestAcceptedAt { get; set; }

    // QuestId -> completed. Presence in the map means the quest has been accepted.
    public Dictionary<int, bool> Quests { get; } = new();

    // QuestId -> goals completed so far (goals tick off in order).
    public Dictionary<int, int> QuestGoalProgress { get; } = new();

    // QuestId -> collect count for the active Collect goal (in-memory; a relog restarts it).
    public Dictionary<int, int> QuestCollectProgress { get; } = new();

    // NPCs this player has already been credited for on a counted TalkToNpc goal.
    public HashSet<ulong> TalkedQuestNpcs { get; } = new();

    // Remaining turns of the conversation currently on screen, and the NPC speaking them.
    public Queue<QuestDialogueLine> PendingDialogue { get; } = new();
    public ulong PendingDialogueNpcGuid { get; set; }

    // The NPC currently playing its talk gesture, and a ticket so a later gesture supersedes an
    // earlier one's pending reset instead of being cut short by it.
    public ulong TalkingNpcGuid { get; set; }
    public int TalkAnimationTicket { get; set; }

    // The quest currently tracked (the objective arrow points at this quest). 0 = none.
    public int ActiveQuestId { get; set; }

    // Quest turn-in finalization, invoked once when the client confirms the end screen.
    public System.Action? PendingQuestEndAction { get; set; }

    // Sends a "+XP" popup for the active profile (visual only - this codebase has no job-leveling
    // system yet, so there's no level bar to actually advance).
    public void AwardXp(int xp)
    {
        SendTunneled(new ClientUpdatePacketUpdateProfileExperience
        {
            ProfileId = ActiveProfileId,
            XpGained = xp,
            TotalXpInLevel = 0,
            CurrentLevel = 0
        });
    }

    public ConcurrentDictionary<ChatChannel, bool> ChatChannelStatus { get; set; } = [];

    public int StationCash { get; set; }
    public List<CoinStoreTransactionRecord> CoinStoreTransactions { get; set; } = [];

    public GuildData? GuildData { get; set; }

    public int TimezoneOffset { get; set; }

    public Vector4 StartingZonePosition { get; set; }
    public Quaternion StartingZoneRotation { get; set; }

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

    public void Dismount()
    {
        if (Mount is null)
            return;

        SendTunneledToVisible(new PacketDismountResponse
        {
            RiderGuid = Guid,
            CompositeEffectId = 46
        }, sendToSelf: true);

        UpdateCharacterStats(
            CharacterStats.MaxMovementSpeed.Set(8f),
            CharacterStats.GlideEnabled.Set(0),
            CharacterStats.JumpHeight.Set(0f));

        SendTunneledToVisible(new PlayerUpdatePacketRemovePlayerGracefully
        {
            Guid = Mount.Guid,
            Animate = false,
            Delay = 0,
            EffectDelay = 0,
            CompositeEffectId = 0,
            Duration = 1000
        }, sendToSelf: true);

        Mount.Dispose();
        Mount = null;
    }

    #endregion

    #region Update

    public void UpdateEveryTick()
    {
    }

    public void UpdateEverySecond()
    {
    }

    public void UpdatePosition(Vector4 position, Quaternion rotation, bool updateZoneArea = true)
    {
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
        if (Zone == zone)
            return;

        if (Zone is StartingZone)
        {
            StartingZonePosition = Position;
            StartingZoneRotation = Rotation;
        }

        if (Mount is not null)
            Mount.TeleportToZone(zone, position, rotation);

        RemoveFromVisibleEntities(true);

        ZoneTile.Entities.Remove(Guid, out _);

        Zone.TryRemovePlayer(Guid);

        // Add to new zone/zonetile

        zone.TryAddPlayer(this);

        // Teleport to new zone

        Visible = false;

        Zone = zone;

        ZoneTile = ZoneTile.Empty;

        UpdatePosition(position, rotation);

        var packetClientBeginZoning = new PacketClientBeginZoning
        {
            Name = Zone.Name,
            Position = position,
            Rotation = rotation,
            Sky = "sky_deep_mines.xml",
            Id = Zone.Id,
            GeometryId = 214,
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
        foreach (var npc in npcs)
        {
            if (npc is Mount)
                continue;

            var playerUpdatePacketAddNpc = npc.GetAddNpcPacket();

            // AddNpc's own NotificationImageSetId also needs the per-player quest badge, not just the
            // NotificationInfo packet below.
            playerUpdatePacketAddNpc.NotificationImageSetId = GetNotificationImageId(npc);

            SendTunneled(playerUpdatePacketAddNpc);
        }

        var playerUpdatePacketNpcRelevance = new PlayerUpdatePacketNpcRelevance();

        foreach (var npc in npcs)
        {
            if (npc.CursorId == 0)
                continue;

            playerUpdatePacketNpcRelevance.Entries.Add(new PlayerUpdatePacketNpcRelevance.Entry
            {
                Guid = npc.Guid,
                HasCursor = true,
                CursorId = npc.CursorId
            });
        }

        if (playerUpdatePacketNpcRelevance.Entries.Count > 0)
            SendTunneled(playerUpdatePacketNpcRelevance);

        var playerUpdatePacketAddNotifications = new PlayerUpdatePacketAddNotifications();

        foreach (var npc in npcs)
        {
            // Per-player quest badge overrides the NPC's static Notification (e.g. a vendor badge).
            var questImageId = GetNotificationImageId(npc);
            if (questImageId != 0)
            {
                playerUpdatePacketAddNotifications.Notifications.Add(new NotificationInfo
                {
                    Guid = npc.Guid,
                    Combat = false,
                    ImageId = questImageId,
                    NameId = npc.NameId,
                    SubTextId = npc.SubTextNameId,
                });
            }
            else if (npc.Notification is not null)
            {
                playerUpdatePacketAddNotifications.Notifications.Add(npc.Notification);
            }
        }

        if (playerUpdatePacketAddNotifications.Notifications.Count > 0)
            SendTunneled(playerUpdatePacketAddNotifications);

        foreach (var npc in npcs)
            VisibleNpcs.TryAdd(npc.Guid, npc);
    }

    // Per-player quest badge: "!" if a quest here is offerable, "?" if an active quest turns in here,
    // else the NPC's static badge.
    public int GetNotificationImageId(Npc npc)
    {
        var quests = _resourceManager.Quests;

        if (quests.ByGiver.TryGetValue(npc.Guid, out var giverQuestIds))
        {
            foreach (var questId in giverQuestIds)
            {
                if (quests.TryGet(questId, out var quest) && quest.IsOfferableFor(Quests))
                    return quest.NotificationAvailable;
            }
        }

        if (quests.ByTarget.TryGetValue(npc.Guid, out var targetQuestIds))
        {
            foreach (var questId in targetQuestIds)
            {
                if (Quests.TryGetValue(questId, out var completed) && !completed && quests.TryGet(questId, out var quest))
                    return quest.NotificationActive;
            }
        }

        return npc.Notification?.ImageId ?? 0;
    }

    public void OnAddVisiblePlayers(params IEnumerable<Player> players)
    {
        foreach (var player in players)
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

        foreach (var player in players)
            VisiblePlayers.TryAdd(player.Guid, player);
    }

    public void OnRemoveVisibleNpcs(params IEnumerable<Npc> npcs)
    {
        foreach (var npc in npcs)
        {
            if (npc is Mount)
                continue;

            SendTunneled(new PlayerUpdatePacketRemovePlayer { Guid = npc.Guid });
        }

        foreach (var npc in npcs)
            VisibleNpcs.TryRemove(npc.Guid, out _);
    }

    public void OnRemoveVisibleNpcGracefully(Npc npc, bool animate, int delay, int effectDelay,
        int compositeEffectId, int duration)
    {
        if (npc is Mount)
            return;

        SendTunneled(new PlayerUpdatePacketRemovePlayerGracefully
        {
            Guid = npc.Guid,
            Animate = animate,
            Delay = delay,
            EffectDelay = effectDelay,
            CompositeEffectId = compositeEffectId,
            Duration = duration
        });

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

        if (GuildData is null && GuildInviteInteraction.CanInvite(player))
            commandPacketInteractionList.List.Interactions.Add(GuildInviteInteraction.Data);

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
        bool isReferee = IsMod || IsAdmin;
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
            IsReferee = isReferee,

            // playerUpdatePacketAddPc.TemporaryAppearance = 277;

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
        }

        if (GuildData is not null)
            packet.Guilds.Add(0, GuildData.Guid);

        return packet;
    }

    #region Combat

    private const int PrimaryWeaponSlot = 7;

    private int _energy = 100;

    public int Energy
    {
        get => _energy;
        private set
        {
            _energy = Math.Min(value, MaxEnergy);

            SendTunneled(new ClientUpdatePacketMana
            {
                CurrentMana = _energy,
                MaxMana = MaxEnergy
            });
        }
    }

    private int MaxEnergy { get; set; } = 100;

    public int GetEquippedWeaponDefinitionId()
    {
        if (!ActiveProfile.Items.TryGetValue(PrimaryWeaponSlot, out var profileItem))
            return 0;

        var clientItem = Items.FirstOrDefault(x => x.Id == profileItem.Id);

        return clientItem?.Definition ?? 0;
    }

    public bool SendToolbar()
    {
        if (!_resourceManager.CombatJobs.TryGetValue(ActiveProfileId, out var kit))
        {
            SendTunneled(new AbilityPacketSetDefinition { ProfileId = ActiveProfileId });
            return false;
        }

        var weaponDefinitionId = GetEquippedWeaponDefinitionId();
        var (basic, special) = ResolveWeaponAbilities(kit, weaponDefinitionId);

        var weaponNameId = 0;
        if (_resourceManager.ClientItemDefinitions.TryGetValue(weaponDefinitionId, out var weaponDefinition))
            weaponNameId = weaponDefinition.NameId;

        var setDefinition = new AbilityPacketSetDefinition { ProfileId = kit.ProfileId };

        if (basic is not null)
        {
            setDefinition.AbilitySet.Abilities[0] = CreateToolbarSlot(kit.BasicSlotDefId, basic.IconId, weaponNameId, manaCost: 0);
            SendAbilityDefinition(kit.BasicSlotDefId, basic);
        }

        if (special is not null)
        {
            setDefinition.AbilitySet.Abilities[1] = CreateToolbarSlot(kit.SpecialSlotDefId, special.IconId, weaponNameId, special.EnergyCost);
            SendAbilityDefinition(kit.SpecialSlotDefId, special);
        }

        SendTunneled(setDefinition);

        MaxEnergy = kit.Energy.Max;
        // Resync energy against the new max.
        Energy = _energy;

        return true;
    }

    private void SendAbilityDefinition(int abilityDefinitionId, AbilityDefinition ability)
    {
        SendTunneled(new AbilityPacketAbilityDefinition
        {
            AbilityId = abilityDefinitionId,
            NameId = ability.NameId,
            DescriptionId = ability.DescriptionId,
            IconId = ability.IconId,
            ManaCost = ability.EnergyCost
        });
    }

    private static Ability CreateToolbarSlot(int abilityDefinitionId, int iconId, int nameId, int manaCost) => new()
    {
        Type = 3,
        InstanceId = abilityDefinitionId,
        ManaCost = manaCost,
        IconId = iconId,
        NameId = nameId,
        Unknown7 = 4,
        Unknown9 = 1,
        AbilityDefinitionId = abilityDefinitionId,
        ForceDismount = true
    };

    private (AbilityDefinition? Basic, AbilityDefinition? Special) ResolveWeaponAbilities(JobKitDefinition kit, int weaponDefinitionId)
    {
        var mapping = weaponDefinitionId != 0
            ? kit.Weapons.FirstOrDefault(w => w.WeaponDefIds.Contains(weaponDefinitionId))
            : null;

        var basicId = mapping?.BasicAbilityId ?? kit.FallbackBasicAbilityId;
        var specialId = mapping?.SpecialAbilityId ?? 0;

        return (
            _resourceManager.CombatAbilities.TryGetValue(basicId, out var basic) ? basic : null,
            _resourceManager.CombatAbilities.TryGetValue(specialId, out var special) ? special : null);
    }

    #endregion

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

    private void RemoveFromVisibleEntities(bool notifySelf)
    {
        foreach (var visibleNpc in VisibleNpcs)
            visibleNpc.Value.OnRemoveVisiblePlayers([this]);

        foreach (var visiblePlayer in VisiblePlayers)
            visiblePlayer.Value.OnRemoveVisiblePlayers([this]);

        if (notifySelf)
        {
            OnRemoveVisibleNpcs(VisibleNpcs.Values);
            OnRemoveVisiblePlayers(VisiblePlayers.Values);
        }
    }

    public void Dispose()
    {
        RemoveFromVisibleEntities(false); // no need to notify self since we're DCing

        Mount?.Dispose();
        Mount = null;

        ZoneTile.Entities.Remove(Guid, out _);
        Zone.TryRemovePlayer(Guid);
    }

    // The NPC radial menu currently on this player's screen. The client answers with the id we gave
    // the option, which is meaningless without the list that produced it, so the actions are held
    // here until the reply arrives or the next menu opens.
    public sealed record InteractionMenu(ulong Guid, IReadOnlyDictionary<int, Action<Player>> Options);

    public InteractionMenu? OpenInteractionMenu { get; set; }

    // Option ids live well above IInteraction.UniqueId (a small counter over the handful of
    // registered player-to-player interactions), so a menu id can never be mistaken for one.
    private const int NpcInteractionIdBase = 1_000_000;

    public void SendInteractionMenu(Npc npc, IReadOnlyList<NpcInteractionOption> options)
    {
        var packet = new CommandPacketInteractionList();

        packet.List.Guid = npc.Guid;
        packet.List.Name = npc.Name ?? string.Empty;

        var actions = new Dictionary<int, Action<Player>>(options.Count);

        for (var i = 0; i < options.Count; i++)
        {
            var option = options[i];
            var id = NpcInteractionIdBase + i;

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
}
