using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Sanctuary.Core.IO;
using Sanctuary.Database;
using Sanctuary.Database.Entities;
using Sanctuary.Game.Entities;
using Sanctuary.Game.Resources.Definitions;
using Sanctuary.Game.Zones;
using Sanctuary.Packet;
using Sanctuary.Packet.Common;

namespace Sanctuary.Game.Quests;

public sealed class QuestManager : IQuestManager
{
    private readonly IResourceManager _resourceManager;
    private readonly IDbContextFactory<DatabaseContext> _dbContextFactory;
    private readonly ILogger<QuestManager> _logger;

    public QuestManager(IResourceManager resourceManager, IDbContextFactory<DatabaseContext> dbContextFactory, ILogger<QuestManager> logger)
    {
        _resourceManager = resourceManager;
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    public bool IsQuestNpc(ulong npcGuid)
        => _resourceManager.Quests.ByGiver.ContainsKey(npcGuid) || _resourceManager.Quests.ByTarget.ContainsKey(npcGuid);

    private IEnumerable<(int QuestId, QuestDefinition Quest, int GoalIndex, QuestGoal Goal)> ActiveGoals(Player player)
    {
        foreach (var (questId, completed) in player.Quests)
        {
            if (completed || !_resourceManager.Quests.TryGet(questId, out var quest))
                continue;

            var goals = quest.EffectiveGoals;
            int done = player.QuestGoalProgress.TryGetValue(questId, out var progress) ? progress : 0;
            if (done >= goals.Count)
                continue;

            yield return (questId, quest, done, goals[done]);
        }
    }

    public void OnNpcInteract(Player player, Npc npc)
    {
        var quests = _resourceManager.Quests;

        foreach (var (_, activeQuest, done, goal) in ActiveGoals(player))
        {
            if (goal.Type == QuestGoalType.Collect)
                continue;

            if (goal.IsCountedTalk)
            {
                if (TryCreditCountedTalk(player, activeQuest, done, npc))
                    return;

                continue;
            }

            if (GoalTargetGuid(activeQuest, done) == npc.Guid)
            {
                CompleteGoal(player, activeQuest, done, npc.Guid);
                return;
            }
        }

        if (quests.ByGiver.TryGetValue(npc.Guid, out var giverQuestIds))
        {
            foreach (var questId in giverQuestIds)
            {
                if (quests.TryGet(questId, out var offerableQuest) && offerableQuest.IsOfferableFor(player.Quests))
                {
                    Offer(player, offerableQuest);
                    return;
                }
            }
        }
    }

    private const int CollectPickupEffect = 5386;

    public void OnCollectInteract(Player player, Npc npc)
    {
        if (!_resourceManager.Quests.Collectibles.TryGetValue(npc.Guid, out var loc))
            return;

        var (questId, goalIndex) = loc;
        if (!_resourceManager.Quests.TryGet(questId, out var quest))
            return;

        if (!player.Quests.TryGetValue(questId, out var completed) || completed)
            return;

        int done = player.QuestGoalProgress.TryGetValue(questId, out var progress) ? progress : 0;
        if (done != goalIndex)
            return;

        var goal = quest.EffectiveGoals[goalIndex];
        if (goal.Type != QuestGoalType.Collect)
            return;

        int required = goal.RequiredCount > 0 ? goal.RequiredCount : goal.CollectSpawns.Count;
        if (required <= 0)
            return;

        int count = (player.QuestCollectProgress.TryGetValue(questId, out var c) ? c : 0) + 1;

        player.SendTunneledToVisible(new PlayerUpdatePacketPlayCompositeEffect
        {
            Guid = npc.Guid,
            CompositeEffectId = CollectPickupEffect,
            Position = npc.Position
        }, sendToSelf: true);

        player.SendTunneled(new PlayerUpdatePacketRemovePlayer { Guid = npc.Guid });
        player.CollectedPickups.Add(npc.Guid);

        if (count >= required)
        {
            player.QuestCollectProgress.Remove(questId);
            CompleteGoal(player, quest, goalIndex);
        }
        else
        {
            player.QuestCollectProgress[questId] = count;
            player.SendTunneled(new QuestObjectiveUpdatePacket
            {
                QuestId = questId,
                ObjectiveId = goal.NameId,
                CurrentCount = count,
                CompletedPercentage = (float)count / required
            });

            PersistCollectCount(player, questId, count);

            RefreshObjectiveTarget(player);
        }
    }

    public void OnPlayerMoved(Player player)
    {
        foreach (var (questId, quest, done, goal) in ActiveGoals(player))
        {
            if (goal.Type != QuestGoalType.ReachLocation || goal.ReachPosition.Length < 3)
                continue;

            var dx = player.Position.X - goal.ReachPosition[0];
            var dz = player.Position.Z - goal.ReachPosition[2];
            var radius = goal.ReachRadius > 0 ? goal.ReachRadius : 12f;
            if (dx * dx + dz * dz > radius * radius)
                continue;

            CompleteGoal(player, quest, done);
        }
    }

    private void UpdateCharacterQuest(Player player, int questId, Action<DbCharacterQuest> update)
    {
        using var db = _dbContextFactory.CreateDbContext();
        var dbQuest = db.CharacterQuests.FirstOrDefault(x => x.QuestId == questId && x.CharacterId == player.CharacterId);
        if (dbQuest is null)
            return;

        update(dbQuest);
        db.SaveChanges();
    }

    private void PersistCollectCount(Player player, int questId, int count)
        => UpdateCharacterQuest(player, questId, q => q.GoalCount = count);

    private void PersistActiveQuest(Player player, int questId)
    {
        using var db = _dbContextFactory.CreateDbContext();
        foreach (var dbQuest in db.CharacterQuests.Where(q => q.CharacterId == player.CharacterId))
            dbQuest.IsActive = dbQuest.QuestId == questId;
        db.SaveChanges();
    }

    private void RespawnQuestCollectibles(Player player, int questId)
    {
        var relevance = new PlayerUpdatePacketNpcRelevance();

        foreach (var entry in _resourceManager.Quests.Collectibles)
        {
            if (entry.Value.QuestId != questId)
                continue;
            if (!player.Zone.TryGetNpc(entry.Key, out var npc))
                continue;

            player.CollectedPickups.Remove(entry.Key);

            player.SendTunneled(npc.GetAddNpcPacket());

            if (npc.CursorId != 0)
            {
                relevance.Entries.Add(new PlayerUpdatePacketNpcRelevance.Entry
                {
                    Guid = npc.Guid,
                    HasCursor = true,
                    CursorId = npc.CursorId,
                    Unknown2 = true
                });
            }
        }

        if (relevance.Entries.Count > 0)
            player.SendTunneled(relevance);
    }

    public void AcceptQuest(Player player, int questId)
    {
        if (!_resourceManager.Quests.TryGet(questId, out var quest) || !quest.IsOfferableFor(player.Quests))
            return;

        player.Quests[questId] = false;
        player.QuestGoalProgress.Remove(questId);
        player.QuestCollectProgress.Remove(questId);
        ClearTalkProgress(player, quest);
        player.ActiveQuestId = questId;
        player.LastQuestAcceptedAt = DateTime.UtcNow;

        using (var db = _dbContextFactory.CreateDbContext())
        {
            foreach (var existing in db.CharacterQuests.Where(q => q.CharacterId == player.CharacterId))
                existing.IsActive = false;

            db.CharacterQuests.Add(new DbCharacterQuest
            {
                QuestId = questId,
                CharacterId = player.CharacterId,
                Completed = false,
                IsActive = true
            });
            db.SaveChanges();
        }

        SendActiveState(player, quest);

        RespawnQuestCollectibles(player, questId);

        RefreshQuestNotifications(player, quest);

        player.SendTunneled(new CommandPacketQuestDialogComplete());
    }

    public void CompleteQuest(Player player, int questId)
    {
        if (!_resourceManager.Quests.TryGet(questId, out var quest))
            return;

        if (player.Quests.TryGetValue(questId, out var done) && done)
            return;

        player.Quests[questId] = true;
        player.QuestCollectProgress.Remove(questId);
        ClearTalkProgress(player, quest);
        UpdateCharacterQuest(player, questId, q => q.Completed = true);

        player.SendTunneled(new QuestCompletePacket { QuestId = questId });

        player.SendTunneled(new CompletedQuestCountUpdatePacket
        {
            Count = player.Quests.Values.Count(done => done)
        });

        SendJournalQuestStates(player);

        GrantReward(player, quest);

        RefreshQuestNotifications(player, quest);

        if (quest.NextQuestId != 0 && _resourceManager.Quests.TryGet(quest.NextQuestId, out var next))
            RefreshQuestNotification(player, next.GiverGuid);

        RefreshObjectiveTarget(player);
    }

    public void AbandonQuest(Player player, int questId)
    {
        if ((DateTime.UtcNow - player.LastQuestAcceptedAt).TotalSeconds < 3)
            return;

        if (!(player.Quests.TryGetValue(questId, out var completed) && !completed))
        {
            var active = player.Quests.Where(entry => !entry.Value).Select(entry => entry.Key).ToList();
            if (active.Count != 1)
                return;

            questId = active[0];
        }

        if (!_resourceManager.Quests.TryGet(questId, out var quest))
            return;

        player.Quests.Remove(questId);
        player.QuestCollectProgress.Remove(questId);
        ClearTalkProgress(player, quest);
        QuestDialogue.Clear(player);

        using (var db = _dbContextFactory.CreateDbContext())
        {
            var dbQuest = db.CharacterQuests.FirstOrDefault(x => x.QuestId == questId && x.CharacterId == player.CharacterId);
            if (dbQuest is not null)
            {
                db.CharacterQuests.Remove(dbQuest);
                db.SaveChanges();
            }
        }

        player.SendTunneled(new QuestAbandonedPacket { QuestId = questId });

        RefreshQuestNotifications(player, quest);

        RefreshObjectiveTarget(player);
    }

    public void SetActiveQuest(Player player, int questId)
    {
        if (!_resourceManager.Quests.TryGet(questId, out var quest))
            return;

        if (player.Quests.TryGetValue(questId, out var completed) && !completed)
        {
            player.ActiveQuestId = questId;
            PersistActiveQuest(player, questId);

            int done = player.QuestGoalProgress.TryGetValue(questId, out var progress) ? progress : 0;
            var goals = quest.EffectiveGoals;

            if (done < goals.Count)
                SendObjectiveActivated(player, questId, goals[done]);

            SendObjectiveForGoal(player, quest, done);
        }
    }

    public void RestoreJournal(Player player)
    {
        foreach (var (questId, completed) in player.Quests)
        {
            if (!completed && _resourceManager.Quests.TryGet(questId, out var quest))
                SendActiveState(player, quest);
        }

        player.SendTunneled(new CompletedQuestCountUpdatePacket
        {
            Count = player.Quests.Values.Count(done => done)
        });

        SendJournalQuestStates(player);
    }

    private void SendJournalQuestStates(Player player)
    {
        var states = new Dictionary<int, int>();
        foreach (var (questId, completed) in player.Quests)
            if (completed)
                states[questId] = 1;

        if (states.Count > 0)
            player.SendTunneled(new AdventurersJournalQuestUpdatePacket { QuestStates = states });
    }

    private void RefreshQuestNotifications(Player player, QuestDefinition quest)
    {
        RefreshQuestNotification(player, quest.GiverGuid);
        RefreshQuestNotification(player, quest.TargetGuid);

        foreach (var excludedId in quest.ExcludesQuestIds)
        {
            if (!_resourceManager.Quests.TryGet(excludedId, out var excludedQuest))
                continue;

            RefreshQuestNotification(player, excludedQuest.GiverGuid);
            RefreshQuestNotification(player, excludedQuest.TargetGuid);
        }
    }

    public void RefreshQuestNotification(Player player, ulong npcGuid)
    {
        if (npcGuid == 0 || !player.Zone.TryGetNpc(npcGuid, out var npc))
            return;

        var imageId = player.GetNotificationImageId(npc);

        player.SendTunneled(new PlayerUpdatePacketRemovePlayer { Guid = npc.Guid });

        var addNpcPacket = npc.GetAddNpcPacket();
        addNpcPacket.NotificationImageSetId = imageId;
        player.SendTunneled(addNpcPacket);

        if (npc.CursorId != 0)
        {
            var relevance = new PlayerUpdatePacketNpcRelevance();
            relevance.Entries.Add(new PlayerUpdatePacketNpcRelevance.Entry
            {
                Guid = npc.Guid,
                HasCursor = true,
                CursorId = npc.CursorId,
                Unknown2 = imageId != 0
            });
            player.SendTunneled(relevance);
        }

        if (imageId == 0)
        {
            player.SendTunneled(new PlayerUpdatePacketRemoveNotifications { Guids = { npc.Guid } });
            return;
        }

        player.SendTunneled(new PlayerUpdatePacketAddNotifications
        {
            Notifications =
            {
                new NotificationInfo
                {
                    Guid = npc.Guid,
                    Combat = false,
                    ImageId = imageId,
                    NameId = npc.NameId,
                    SubTextId = npc.SubTextNameId,
                }
            }
        });
    }

    private void Offer(Player player, QuestDefinition quest)
    {
        player.SendTunneled(new QuestInfoPacket
        {
            QuestId = quest.QuestId,
            TitleId = quest.GiverDialogueId,
            DescriptionId = quest.DescriptionId,
            HelperTextId = quest.TitleId,
            IconId = quest.IconId,
            Unknown6 = quest.ObjectiveDescriptionId,
            Unknown7 = false,
            NpcGuid = quest.GiverGuid,
            Unknown10 = 0,
            Unknown11 = false,
            Unknown12 = false,
            RewardCoins = quest.RewardCoins,
            RewardExperience = quest.RewardExperience,
            RewardItems = BuildRewardItems(quest)
        });
    }

    private List<RewardBundleItem> BuildRewardItems(QuestDefinition quest)
    {
        var items = new List<RewardBundleItem>();
        foreach (var definitionId in quest.RewardItems)
        {
            if (_resourceManager.ClientItemDefinitions.TryGetValue(definitionId, out var itemDef))
            {
                items.Add(new RewardBundleItem
                {
                    IconId = itemDef.Icon.Id,
                    NameId = itemDef.NameId,
                    Count = 1
                });
            }
        }
        return items;
    }

    private bool TryCreditCountedTalk(Player player, QuestDefinition quest, int goalIndex, Npc npc)
    {
        var goal = quest.EffectiveGoals[goalIndex];

        if (!goal.AllTalkTargetGuids().Contains(npc.Guid))
            return false;

        bool alreadyCredited = !player.TalkedQuestNpcs.Add(npc.Guid);

        if (!alreadyCredited)
            RefreshQuestNotification(player, npc.Guid);

        int required = goal.RequiredCount;
        int count = player.QuestCollectProgress.TryGetValue(quest.QuestId, out var c) ? c : 0;

        if (!alreadyCredited)
            count++;

        if (!alreadyCredited && count >= required)
        {
            player.QuestCollectProgress.Remove(quest.QuestId);
            ClearTalkProgress(player, goal);
            CompleteGoal(player, quest, goalIndex, npc.Guid);
            return true;
        }

        if (!alreadyCredited)
        {
            player.QuestCollectProgress[quest.QuestId] = count;

            player.SendTunneled(new QuestObjectiveUpdatePacket
            {
                QuestId = quest.QuestId,
                ObjectiveId = goal.NameId,
                CurrentCount = count,
                CompletedPercentage = (float)count / required
            });

            PersistCollectCount(player, quest.QuestId, count);
        }

        QuestDialogue.Begin(player, goal.ConversationFor(npc.Guid), npc.Guid);

        RefreshObjectiveTarget(player);
        return true;
    }

    private static void ClearTalkProgress(Player player, QuestGoal goal)
    {
        foreach (var guid in goal.AllTalkTargetGuids())
            player.TalkedQuestNpcs.Remove(guid);
    }

    private static void ClearTalkProgress(Player player, QuestDefinition quest)
    {
        foreach (var goal in quest.EffectiveGoals)
            if (goal.IsCountedTalk)
                ClearTalkProgress(player, goal);
    }

    private static ulong NearestUntalkedTarget(Player player, QuestGoal goal)
    {
        ulong nearest = 0;
        var best = float.MaxValue;

        foreach (var guid in goal.AllTalkTargetGuids())
        {
            if (player.TalkedQuestNpcs.Contains(guid))
                continue;

            if (!player.Zone.TryGetNpc(guid, out var npc))
                continue;

            var dx = npc.Position.X - player.Position.X;
            var dz = npc.Position.Z - player.Position.Z;
            var distance = dx * dx + dz * dz;

            if (distance < best)
            {
                best = distance;
                nearest = guid;
            }
        }

        return nearest;
    }
    private void CompleteGoal(Player player, QuestDefinition quest, int goalIndex, ulong spokenBy = 0)
    {
        var goals = quest.EffectiveGoals;

        bool isFinalGoal = goalIndex + 1 >= goals.Count;

        player.SendTunneled(new QuestObjectiveCompletePacket
        {
            QuestId = quest.QuestId,
            ObjectiveId = goals[goalIndex].NameId,
            Percent = 1f,
            Silent = isFinalGoal
        });

        int done = goalIndex + 1;
        player.QuestGoalProgress[quest.QuestId] = done;

        UpdateCharacterQuest(player, quest.QuestId, q =>
        {
            q.GoalProgress = done;
            q.GoalCount = 0;
        });

        if (done >= goals.Count)
        {
            TurnIn(player, quest);
            return;
        }

        player.SendTunneled(new QuestObjectiveAddedPacket
        {
            QuestId = quest.QuestId,
            ObjectiveNameId = goals[done].NameId,
            ObjectiveDescriptionId = goals[done].NameId,
            ObjectiveField2 = goals[done].DescriptionId != 0 ? goals[done].DescriptionId : goals[done].NameId
        });
        SendObjectiveActivated(player, quest.QuestId, goals[done]);
        SendObjectiveForGoal(player, quest, done);

        var completedGoal = goals[goalIndex];
        if (completedGoal.Type == QuestGoalType.TalkToNpc)
        {
            var speaker = spokenBy != 0 ? spokenBy : GoalTargetGuid(quest, goalIndex);

            QuestDialogue.Begin(player, completedGoal.ConversationFor(speaker), speaker);
        }
    }

    private const int YouGotItTextId = 103085;

    private const int GreenCheckImageId = 300;

    private const int GreenButtonImageSet = 17;

    private void TurnIn(Player player, QuestDefinition quest)
    {
        player.SendTunneled(new QuestEndPacket
        {
            NpcGuid = GoalTargetGuid(quest, quest.EffectiveGoals.Count - 1),
            QuestId = quest.QuestId,
            TitleId = quest.TurnInDialogueId,
            DescriptionId = quest.TitleId,
            RewardCoins = quest.RewardCoins,
            RewardExperience = quest.RewardExperience,
            RewardItems = BuildRewardItems(quest)
        });

        player.PendingQuestEndAction = () => CompleteQuest(player, quest.QuestId);
    }

    private static void SendQuestAdd(Player player, QuestDefinition quest, int helperTextId, float completedPercentage = 0f)
    {
        player.SendTunneled(new QuestAddPacket
        {
            QuestId = quest.QuestId,
            TitleId = quest.TitleId,
            DescriptionId = quest.ObjectiveDescriptionId,
            HelperTextId = helperTextId,
            MembersOnly = false,
            TimeStarted = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ProfileId = 0,
            CompletedPercentage = completedPercentage,
            IconId = quest.IconId,
            SystemQuest = false
        });
    }

    private void SendActiveState(Player player, QuestDefinition quest)
    {
        int alreadyDone = player.QuestGoalProgress.TryGetValue(quest.QuestId, out var p) ? p : 0;
        SendQuestAdd(player, quest, quest.ObjectiveDescriptionId, (float)alreadyDone / quest.EffectiveGoals.Count);

        var goals = quest.EffectiveGoals;
        int done = player.QuestGoalProgress.TryGetValue(quest.QuestId, out var progress) ? progress : 0;
        int lastVisible = System.Math.Min(done, goals.Count - 1);

        for (int i = 0; i <= lastVisible; i++)
        {
            player.SendTunneled(new QuestObjectiveAddedPacket
            {
                QuestId = quest.QuestId,
                ObjectiveNameId = goals[i].NameId,
                ObjectiveDescriptionId = goals[i].NameId,
                ObjectiveField2 = goals[i].DescriptionId != 0 ? goals[i].DescriptionId : goals[i].NameId
            });
        }

        for (int i = 0; i < done && i < goals.Count; i++)
        {
            player.SendTunneled(new QuestObjectiveCompletePacket
            {
                QuestId = quest.QuestId,
                ObjectiveId = goals[i].NameId,
                Percent = 1f,
                Silent = true
            });
        }

        if (done < goals.Count)
        {
            var activeGoal = goals[done];
            SendObjectiveActivated(player, quest.QuestId, activeGoal);

            if (activeGoal.Type == QuestGoalType.Collect
                && player.QuestCollectProgress.TryGetValue(quest.QuestId, out var collected) && collected > 0)
            {
                int req = activeGoal.RequiredCount > 0 ? activeGoal.RequiredCount : activeGoal.CollectSpawns.Count;
                player.SendTunneled(new QuestObjectiveUpdatePacket
                {
                    QuestId = quest.QuestId,
                    ObjectiveId = activeGoal.NameId,
                    CurrentCount = collected,
                    CompletedPercentage = req > 0 ? (float)collected / req : 0f
                });
            }
        }

        SendObjectiveForGoal(player, quest, done);
    }

    private static void SendObjectiveActivated(Player player, int questId, QuestGoal goal)
    {
        player.SendTunneled(new QuestObjectiveActivatedPacket
        {
            QuestId = questId,
            ObjectiveId = goal.NameId,
            RequiredCount = goal.RequiredCount,
            Unknown2 = false
        });
    }

    private static ulong GoalTargetGuid(QuestDefinition quest, int goalIndex)
    {
        var goals = quest.EffectiveGoals;
        if (goalIndex >= 0 && goalIndex < goals.Count && goals[goalIndex].TargetGuid != 0)
            return goals[goalIndex].TargetGuid;
        return quest.TargetGuid;
    }

    private ulong ResolveGoalTargetGuid(Player player, QuestDefinition quest, int goalIndex)
    {
        var goals = quest.EffectiveGoals;

        if (goalIndex >= 0 && goalIndex < goals.Count
            && goals[goalIndex].Type == QuestGoalType.Collect)
        {
            var nearest = NearestUncollectedPickup(player, quest.QuestId, goalIndex);
            if (nearest is not null)
                return nearest.Guid;
        }

        if (goalIndex >= 0 && goalIndex < goals.Count && goals[goalIndex].IsCountedTalk)
        {
            var untalked = NearestUntalkedTarget(player, goals[goalIndex]);
            if (untalked != 0)
                return untalked;
        }

        return GoalTargetGuid(quest, goalIndex);
    }

    private Npc? NearestUncollectedPickup(Player player, int questId, int goalIndex)
    {
        Npc? nearest = null;
        var best = float.MaxValue;
        foreach (var (guid, loc) in _resourceManager.Quests.Collectibles)
        {
            if (loc.QuestId != questId || loc.GoalIndex != goalIndex)
                continue;
            if (player.CollectedPickups.Contains(guid))
                continue;
            if (!player.Zone.TryGetNpc(guid, out var pickup))
                continue;
            var dx = pickup.Position.X - player.Position.X;
            var dz = pickup.Position.Z - player.Position.Z;
            var d2 = dx * dx + dz * dz;
            if (d2 < best)
            {
                best = d2;
                nearest = pickup;
            }
        }
        return nearest;
    }

    private void SendObjectiveForGoal(Player player, QuestDefinition quest, int goalIndex)
    {
        var goals = quest.EffectiveGoals;

        if (goalIndex >= 0 && goalIndex < goals.Count
            && goals[goalIndex].Type == QuestGoalType.ReachLocation
            && goals[goalIndex].ReachPosition.Length >= 3)
        {
            var rp = goals[goalIndex].ReachPosition;
            var reachPos = new Vector4(rp[0], rp[1], rp[2], 1f);
            var reachZoneId = player.Zone is StartingZone reachZone
                ? reachZone.GetZoneAreaId(reachPos)
                : player.Zone.Id;

            player.SendTunneled(new ObjectiveTargetUpdatePacket
            {
                Active = true,
                LocationX = reachPos.X,
                LocationZ = reachPos.Z,
                ZoneId = reachZoneId,
                Guid = 0,
                NameId = goals[goalIndex].NameId,
                PositionX = reachPos.X,
                PositionY = reachPos.Y,
                PositionZ = reachPos.Z,
                PositionW = 1f
            });
            return;
        }

        SendObjectiveTarget(player, ResolveGoalTargetGuid(player, quest, goalIndex));
    }

    private void SendObjectiveTarget(Player player, ulong targetGuid)
    {
        if (targetGuid == 0 || !player.Zone.TryGetNpc(targetGuid, out var target))
            return;

        var pos = target.Position;
        var zoneAreaId = player.Zone is StartingZone startingZone
            ? startingZone.GetZoneAreaId(pos)
            : player.Zone.Id;

        player.SendTunneled(new ObjectiveTargetUpdatePacket
        {
            Active = true,
            LocationX = pos.X,
            LocationZ = pos.Z,
            ZoneId = zoneAreaId,
            Guid = targetGuid,
            NameId = target.NameId,
            PositionX = pos.X,
            PositionY = pos.Y,
            PositionZ = pos.Z,
            PositionW = 1f
        });
    }

    public void RefreshObjectiveTarget(Player player)
    {
        if (TryGetTrackedGoal(player, out var quest, out var goalIndex))
            SendObjectiveForGoal(player, quest, goalIndex);
        else
            player.SendTunneled(new ObjectiveTargetUpdatePacket { Active = false });
    }

    public bool TryGetActiveObjectiveTarget(Player player, out Vector3 targetPosition)
    {
        if (TryGetTrackedGoal(player, out var quest, out var goalIndex))
        {
            var goals = quest.EffectiveGoals;

            var onGoal = goalIndex >= 0 && goalIndex < goals.Count;

            if (onGoal && goals[goalIndex].Type == QuestGoalType.ReachLocation
                && goals[goalIndex].ReachPosition.Length >= 3)
            {
                var rp = goals[goalIndex].ReachPosition;
                targetPosition = new Vector3(rp[0], rp[1], rp[2]);
                return true;
            }

            var guid = ResolveGoalTargetGuid(player, quest, goalIndex);
            if (guid != 0 && player.Zone.TryGetNpc(guid, out var target))
            {
                targetPosition = new Vector3(target.Position.X, target.Position.Y, target.Position.Z);
                return true;
            }
        }

        targetPosition = default;
        return false;
    }

    private bool TryGetTrackedGoal(Player player, out QuestDefinition quest, out int goalIndex)
    {
        if (player.ActiveQuestId != 0
            && player.Quests.TryGetValue(player.ActiveQuestId, out var activeCompleted) && !activeCompleted
            && TryGetTrackableGoal(player, player.ActiveQuestId, out quest, out goalIndex))
        {
            return true;
        }

        foreach (var (questId, completed) in player.Quests)
        {
            if (completed)
                continue;
            if (TryGetTrackableGoal(player, questId, out quest, out goalIndex))
                return true;
        }

        quest = null!;
        goalIndex = -1;
        return false;
    }

    private bool TryGetTrackableGoal(Player player, int questId, out QuestDefinition quest, out int goalIndex)
    {
        quest = null!;
        goalIndex = -1;
        if (!_resourceManager.Quests.TryGet(questId, out var q))
            return false;

        int done = player.QuestGoalProgress.TryGetValue(questId, out var progress) ? progress : 0;
        var goals = q.EffectiveGoals;

        if (done >= 0 && done < goals.Count
            && goals[done].Type == QuestGoalType.ReachLocation
            && goals[done].ReachPosition.Length >= 3)
        {
            quest = q;
            goalIndex = done;
            return true;
        }

        ulong guid = ResolveGoalTargetGuid(player, q, done);
        if (guid != 0 && player.Zone.TryGetNpc(guid, out _))
        {
            quest = q;
            goalIndex = done;
            return true;
        }
        return false;
    }

    private void GrantReward(Player player, QuestDefinition quest)
    {
        var coins = quest.RewardCoins;
        if (coins > 0)
        {
            int newTotal;
            using (var db = _dbContextFactory.CreateDbContext())
            {
                var dbCharacter = db.Characters.FirstOrDefault(c => c.Id == player.CharacterId);
                if (dbCharacter is null)
                    return;

                dbCharacter.Coins += coins;
                db.SaveChanges();
                newTotal = dbCharacter.Coins;
            }

            player.Coins = newTotal;
            player.SendTunneled(new ClientUpdatePacketCoinCount { Coins = newTotal });
        }

        var experience = quest.RewardExperience;
        if (experience > 0)
            player.AwardXp(experience);

        if (coins > 0 || experience > 0)
            player.SendTunneled(new QuestRewardBundlePacket { Coins = coins, Xp = experience });

        foreach (var itemDefinitionId in quest.RewardItems)
        {
            GrantItem(player, itemDefinitionId);

            player.SendTunneled(new RewardNonBundledItemPacket { ItemDefinitionId = itemDefinitionId, Quantity = 1 });
        }
    }

    private void GrantItem(Player player, int definitionId)
    {
        if (!_resourceManager.ClientItemDefinitions.TryGetValue(definitionId, out var itemDef))
            return;

        int tint = itemDef.IsTintable ? 0 : itemDef.Icon.TintId;

        int itemId, count;
        using (var db = _dbContextFactory.CreateDbContext())
        {
            var row = db.Characters
                .Where(c => c.Id == player.CharacterId)
                .Select(c => new
                {
                    Character = c,
                    Item = c.Items.FirstOrDefault(i => i.Definition == definitionId && i.Tint == tint),
                    NextId = c.Items.Max(i => (int?)i.Id) ?? 0
                })
                .FirstOrDefault();

            if (row is null)
                return;

            if (row.Item is not null)
            {
                row.Item.Count += 1;
                itemId = row.Item.Id;
                count = row.Item.Count;
            }
            else
            {
                var dbItem = new DbItem { Id = row.NextId + 1, Definition = definitionId, Tint = tint, Count = 1 };
                row.Character.Items.Add(dbItem);
                itemId = dbItem.Id;
                count = 1;
            }

            db.SaveChanges();
        }

        var clientItem = player.Items.FirstOrDefault(x => x.Definition == definitionId && x.Tint == tint);
        if (clientItem is not null)
        {
            clientItem.Count = count;
            player.SendTunneled(new ClientUpdatePacketItemUpdate { ItemGuid = clientItem.Id, Count = clientItem.Count });
        }
        else
        {
            clientItem = new ClientItem { Id = itemId, Tint = tint, Count = count, Definition = definitionId };
            player.Items.Add(clientItem);

            using var writer = new PacketWriter();
            clientItem.Serialize(writer);
            itemDef.Serialize(writer);
            player.SendTunneled(new ClientUpdatePacketItemAdd { Payload = writer.Buffer });
        }
    }
}
