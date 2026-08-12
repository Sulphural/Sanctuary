using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using Microsoft.EntityFrameworkCore;

using Sanctuary.Core.IO;
using Sanctuary.Database;
using Sanctuary.Database.Entities;
using Sanctuary.Game.Entities;
using Sanctuary.Game.Resources.Definitions;
using Sanctuary.Game.Zones;
using Sanctuary.Packet;
using Sanctuary.Packet.Common;

namespace Sanctuary.Game.Quests;

// Data-driven implementation of IQuestManager. Every packet sequence here is the one
// the previously-hardcoded "Introduce Yourself" flow used (verified in-game); only the source of the
// values changed - they now come from the QuestDefinition instead of constants.
public sealed class QuestManager : IQuestManager
{
    private readonly IResourceManager _resourceManager;
    private readonly IDbContextFactory<DatabaseContext> _dbContextFactory;

    public QuestManager(IResourceManager resourceManager, IDbContextFactory<DatabaseContext> dbContextFactory)
    {
        _resourceManager = resourceManager;
        _dbContextFactory = dbContextFactory;
    }

    public bool IsQuestNpc(ulong npcGuid)
        => _resourceManager.Quests.ByGiver.ContainsKey(npcGuid) || _resourceManager.Quests.ByTarget.ContainsKey(npcGuid);

    public void OnNpcInteract(Player player, Npc npc)
    {
        var quests = _resourceManager.Quests;

        // 1. Goal progression / turn-in: is this NPC the target of the ACTIVE goal of a quest the player
        // has active (accepted, not yet completed)? Talking to it ticks that goal off; the last goal hands
        // the quest in (end screen). Multi-goal quests can point intermediate goals at different NPCs, so we
        // check each active quest's current goal rather than only the quest's turn-in NPC.
        foreach (var (questId, completed) in player.Quests)
        {
            if (completed || !quests.TryGet(questId, out var activeQuest))
                continue;

            var goals = activeQuest.EffectiveGoals;
            int done = player.QuestGoalProgress.TryGetValue(questId, out var progress) ? progress : 0;
            if (done >= goals.Count)
                continue; // all goals already done (turn-in fires on the last goal, so this shouldn't linger)

            // Collect/Kill/EncounterComplete goals advance only by their own events (OnCollectInteract /
            // OnNpcKilled / OnEncounterComplete). Since they have no NPC target, GoalTargetGuid would fall
            // back to the quest's turn-in NPC - talking to it must NOT tick the goal off (that would bypass
            // the objective), so skip them here.
            if (goals[done].Type is QuestGoalType.Collect or QuestGoalType.Kill or QuestGoalType.EncounterComplete)
                continue;

            // A COUNTED talk goal ("Talk to Freewheelers - 0/3") is ticked up by any of several NPCs
            // instead of completed outright by one, so it takes its own crediting path.
            if (goals[done].IsCountedTalk)
            {
                if (TryCreditCountedTalk(player, activeQuest, done, npc))
                    return;

                continue; // this NPC isn't one of that goal's targets - it may still serve another quest
            }

            if (GoalTargetGuid(activeQuest, done) == npc.Guid)
            {
                CompleteGoal(player, activeQuest, done);
                return;
            }
        }

        // 2. Offer: is this NPC the giver of a quest the player can currently take?
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

    // Credits one NPC toward a COUNTED TalkToNpc goal - the "talk to N of these interchangeable NPCs"
    // shape retail authors as a single plural tracker row ("Talk to Freewheelers - 0/3"), which can't be
    // modelled as one goal per NPC because there's only one goal string to name the rows with.
    // Mirrors OnCollectInteract: same per-quest counter, same tracker animation, same persistence, and the
    // goal ticks off (advancing to the return step) on the Nth distinct NPC.
    // Returns true when this NPC belonged to the goal, so the caller stops scanning the player's quests.
    private bool TryCreditCountedTalk(Player player, QuestDefinition quest, int goalIndex, Npc npc)
    {
        var goal = quest.EffectiveGoals[goalIndex];
        if (!goal.AllTalkTargetGuids().Contains(npc.Guid))
            return false;

        // Each NPC counts once: talking to the same Freewheeler three times must not finish the goal.
        // Their line still replays (below) so a re-talk isn't a silent no-op.
        bool alreadyCredited = !player.TalkedQuestNpcs.Add(npc.Guid);

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

            // Animate the tracker's "current/required" counter, exactly as a collect/kill tick does.
            player.SendTunneled(new QuestObjectiveUpdatePacket
            {
                QuestId = quest.QuestId,
                ObjectiveId = goal.NameId,
                CurrentCount = count,
                CompletedPercentage = (float)count / required
            });

            PersistCollectCount(player, quest.QuestId, count);
        }

        // This NPC still speaks their own line even though the goal isn't done - otherwise the first two
        // Freewheelers would say nothing at all when you reach them.
        QuestDialogue.Begin(player, goal.ConversationFor(npc.Guid), npc.Guid);

        // Re-point the marker/breadcrumb at the nearest target the player hasn't reached yet.
        RefreshObjectiveTarget(player);
        return true;
    }

    // Forgets which of a counted talk goal's NPCs this player has spoken to, so the step starts clean on
    // accept/abandon and can't leak credit into a later re-run of the same quest.
    private static void ClearTalkProgress(Player player, QuestGoal goal)
    {
        foreach (var guid in goal.AllTalkTargetGuids())
            player.TalkedQuestNpcs.Remove(guid);
    }

    // Forgets the talked-to NPCs of every counted talk goal in a quest.
    private static void ClearTalkProgress(Player player, QuestDefinition quest)
    {
        foreach (var goal in quest.EffectiveGoals)
            if (goal.IsCountedTalk)
                ClearTalkProgress(player, goal);
    }

    // Composite effect played on a collectible when picked up (PFX_sparkles-swirl_gold_treasure-reward).
    private const int CollectPickupEffect = 5386;

    // A collectible pickup was clicked. Credits the quest's active Collect goal (one per distinct pickup),
    // hides the pickup for this player, animates the tracker counter, and completes the goal - advancing to
    // the return step - once RequiredCount is reached.
    public void OnCollectInteract(Player player, Npc npc)
    {
        if (!_resourceManager.Quests.Collectibles.TryGetValue(npc.Guid, out var loc))
            return;

        var (questId, goalIndex) = loc;
        if (!_resourceManager.Quests.TryGet(questId, out var quest))
            return;

        // Must have this quest active (accepted, not completed) and be ON this goal (earlier goals done).
        if (!player.Quests.TryGetValue(questId, out var completed) || completed)
            return;

        int done = player.QuestGoalProgress.TryGetValue(questId, out var progress) ? progress : 0;
        if (done != goalIndex)
            return; // not the active goal yet (a prior goal is pending) or already collected past it

        var goal = quest.EffectiveGoals[goalIndex];
        if (goal.Type != QuestGoalType.Collect)
            return;

        int required = goal.RequiredCount > 0 ? goal.RequiredCount : goal.CollectSpawns.Count;
        if (required <= 0)
            return;

        int count = (player.QuestCollectProgress.TryGetValue(questId, out var c) ? c : 0) + 1;

        // Gold sparkle "reward" burst where the pickup is - immediate visual feedback that the collect
        // registered (plays before the removal so the effect's source actor still exists).
        player.SendTunneledToVisible(new PlayerUpdatePacketPlayCompositeEffect
        {
            Guid = npc.Guid,
            CompositeEffectId = CollectPickupEffect,
            Position = npc.Position
        }, sendToSelf: true);

        // Hide this pickup for the collecting player so it can't be re-clicked. Collectibles are shared, so
        // other players still see it; a relog re-adds them all and restarts this goal's (in-memory) count.
        player.SendTunneled(new PlayerUpdatePacketRemovePlayer { Guid = npc.Guid });
        player.CollectedPickups.Add(npc.Guid); // so the marker skips it and points at the next tool

        if (count >= required)
        {
            player.QuestCollectProgress.Remove(questId);
            // Final pickup -> tick the goal's checkmark and advance to the return goal (or turn in). Reuses
            // the same completion path as talk-to-NPC goals.
            CompleteGoal(player, quest, goalIndex);
        }
        else
        {
            player.QuestCollectProgress[questId] = count;
            // Animate the tracker's "current/required" counter (the client stores CurrentCount at the
            // objective's row+0xd4 and re-renders "count/required").
            player.SendTunneled(new QuestObjectiveUpdatePacket
            {
                QuestId = questId,
                ObjectiveId = goal.NameId,
                CurrentCount = count,
                CompletedPercentage = (float)count / required
            });

            // Persist so a relog mid-collect resumes at this count (done after the visual so the DB write
            // doesn't delay the on-screen feedback).
            PersistCollectCount(player, questId, count);

            // Re-point the marker/breadcrumb at the NEXT nearest uncollected pickup.
            RefreshObjectiveTarget(player);
        }
    }

    // An NPC died at the player's hands. Credits the active Kill goal (Type=3) of any in-progress quest
    // whose KillNpcNameId matches the victim's NameId, animating the tracker's
    // "current/required" counter and completing the goal at RequiredCount.
    // Mirrors OnCollectInteract (same per-quest count storage + persistence).
    public void OnNpcKilled(Player player, Npc npc)
    {
        if (npc.NameId == 0)
            return;

        foreach (var (questId, completed) in player.Quests)
        {
            if (completed || !_resourceManager.Quests.TryGet(questId, out var quest))
                continue;

            var goals = quest.EffectiveGoals;
            int done = player.QuestGoalProgress.TryGetValue(questId, out var progress) ? progress : 0;
            if (done >= goals.Count)
                continue;

            var goal = goals[done];
            if (goal.Type != QuestGoalType.Kill || !goal.AllKillNameIds().Contains(npc.NameId))
                continue;

            int required = goal.RequiredCount > 0 ? goal.RequiredCount : 1;
            int count = (player.QuestCollectProgress.TryGetValue(questId, out var c) ? c : 0) + 1;

            if (count >= required)
            {
                player.QuestCollectProgress.Remove(questId);
                // Final kill -> tick the goal's checkmark and advance to the return step. Same completion
                // path as talk-to-NPC and collect goals.
                CompleteGoal(player, quest, done);
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

                // Persist so a relog mid-hunt resumes at this count.
                PersistCollectCount(player, questId, count);

                // Re-aim the arrow/breadcrumb at the NEAREST remaining kill target â€” without this it
                // stays pinned on the NPC that just died.
                RefreshObjectiveTarget(player);
            }

            return; // one kill credits one goal
        }
    }

    // The player moved. Completes the active ReachLocation goal (Type=1) of any in-progress quest
    // when the player is within the goal's radius (2D X/Z). Runs on every client position update
    // (~10-20 Hz), so it early-outs everything that isn't an active reach goal.
    public void OnPlayerMoved(Player player)
    {
        foreach (var (questId, completed) in player.Quests)
        {
            if (completed || !_resourceManager.Quests.TryGet(questId, out var quest))
                continue;

            var goals = quest.EffectiveGoals;
            int done = player.QuestGoalProgress.TryGetValue(questId, out var progress) ? progress : 0;
            if (done >= goals.Count)
                continue;

            var goal = goals[done];
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

    // The player won a battle-instance encounter. Completes the active EncounterComplete goal (Type=4)
    // of any in-progress quest whose EncounterId matches - i.e. the dungeon was
    // this quest's objective. Advances to the next goal (usually "return to the giver").
    public void OnEncounterComplete(Player player, int encounterId)
    {
        foreach (var (questId, completed) in player.Quests)
        {
            if (completed || !_resourceManager.Quests.TryGet(questId, out var quest))
                continue;

            var goals = quest.EffectiveGoals;
            int done = player.QuestGoalProgress.TryGetValue(questId, out var progress) ? progress : 0;
            if (done >= goals.Count)
                continue;

            var goal = goals[done];
            if (goal.Type != QuestGoalType.EncounterComplete || goal.EncounterId != encounterId)
                continue;

            CompleteGoal(player, quest, done);
            return; // one win credits one goal
        }
    }

    // Loads a player's DbCharacterQuest row, applies the mutation, and saves - the shared shape behind
    // every quest progress write (collect count, goal progress, completion, tracked flag).
    private void UpdateCharacterQuest(Player player, int questId, Action<DbCharacterQuest> update)
    {
        using var db = _dbContextFactory.CreateDbContext();
        var dbQuest = db.CharacterQuests.FirstOrDefault(x => x.QuestId == questId && x.CharacterId == player.CharacterId);
        if (dbQuest is null)
            return;

        update(dbQuest);
        db.SaveChanges();
    }

    // Persists the active Collect goal's in-progress count (DbCharacterQuest.GoalCount).
    private void PersistCollectCount(Player player, int questId, int count)
        => UpdateCharacterQuest(player, questId, q => q.GoalCount = count);

    // Persists the tracked quest (DbCharacterQuest.IsActive) so a relog restores the player's chosen
    // quest instead of silently resetting ActiveQuestId to whichever active quest happens to load first -
    // which would re-point the tracker arrow and "Take Me There" at the wrong objective.
    private void PersistTrackedQuest(Player player, int questId)
    {
        using var db = _dbContextFactory.CreateDbContext();

        // Exactly one row per character may be flagged - clear the old tracked quest in the same save.
        foreach (var row in db.CharacterQuests.Where(x => x.CharacterId == player.CharacterId))
            row.IsActive = row.QuestId == questId;

        db.SaveChanges();
    }

    // Re-sends this quest's collectible pickups to the player so any hidden in a prior attempt reappear and
    // are clickable again: AddNpc (re-adds the model; a no-op for one still showing) PLUS an NpcRelevance
    // entry - that relevance packet, not just AddNpc's IsInteractable flag, is what registers a pickup as
    // interactable client-side (this is how zone-entry wires them up). NB: no RemovePlayer first - a
    // remove+re-add of the same guid races and can leave the pickup gone.
    private void RespawnQuestCollectibles(Player player, int questId)
    {
        var relevance = new PlayerUpdatePacketNpcRelevance();

        foreach (var entry in _resourceManager.Quests.Collectibles)
        {
            if (entry.Value.QuestId != questId)
                continue;
            if (!player.Zone.TryGetNpc(entry.Key, out var npc))
                continue;

            // Re-showing every pickup: forget which ones were "collected" so the marker treats them all
            // as available again (matches the in-memory count reset that happens on re-accept/relog).
            player.CollectedPickups.Remove(entry.Key);

            player.SendTunneled(npc.GetAddNpcPacket());

            if (npc.CursorId != 0)
            {
                relevance.Entries.Add(new PlayerUpdatePacketNpcRelevance.Entry
                {
                    Guid = npc.Guid,
                    Unknown = true,
                    CursorId = npc.CursorId,
                    HasCursor = true
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
        player.QuestGoalProgress.Remove(questId); // fresh accept starts on the first goal
        player.QuestCollectProgress.Remove(questId); // and with no collect progress
        ClearTalkProgress(player, quest); // ...and with nobody yet talked to on its counted talk goals
        player.ActiveQuestId = questId; // a freshly accepted quest becomes the tracked one
        player.LastQuestAcceptedAt = DateTime.UtcNow; // guards against a stray post-accept QuestAbandon

        using (var db = _dbContextFactory.CreateDbContext())
        {
            // A freshly accepted quest becomes the tracked one - clear IsActive off every other quest
            // this character has so at most one row stays true.
            foreach (var existing in db.CharacterQuests.Where(x => x.CharacterId == player.CharacterId))
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

        // Restore this quest's collectible pickups for the player: any collected in a PRIOR attempt were
        // hidden with RemovePlayer (which persists until relog), so without this a collect-then-abandon-then-
        // reaccept would leave fewer than RequiredCount pickups and the goal could never finish.
        RespawnQuestCollectibles(player, questId);

        RefreshQuestNotifications(player, quest);

        // Finalize the interaction so the offer camera doesn't stay frozen on the giver (sub-opcode 29
        // recomputes the camera + dispatches QuestStartHandler:DismissEndScreen).
        player.SendTunneled(new CommandPacketQuestDialogComplete());
    }

    public void CompleteQuest(Player player, int questId)
    {
        if (!_resourceManager.Quests.TryGet(questId, out var quest))
            return;

        if (player.Quests.TryGetValue(questId, out var done) && done)
            return; // already finalized

        player.Quests[questId] = true;
        player.QuestCollectProgress.Remove(questId);
        ClearTalkProgress(player, quest);

        UpdateCharacterQuest(player, questId, q => q.Completed = true);

        player.SendTunneled(new QuestCompletePacket { QuestId = questId });

        // Bump the journal's lifetime "quests completed" counter (op49/12).
        player.SendTunneled(new CompletedQuestCountUpdatePacket
        {
            Count = player.Quests.Values.Count(done => done)
        });

        // Mark this quest complete in the storybook Adventurer's Journal (op209/2) so its sticker earns.
        SendJournalQuestStates(player);

        GrantReward(player, quest);

        // Clear the badges on both quest NPCs.
        RefreshQuestNotifications(player, quest);

        // The next quest in the chain becomes offerable automatically (IsOfferable checks the prereq);
        // refresh its giver's badge so the "!" appears without a relog if that NPC is already spawned.
        if (quest.NextQuestId != 0 && _resourceManager.Quests.TryGet(quest.NextQuestId, out var next))
            RefreshQuestNotification(player, next.GiverGuid);

        // Clear the completed quest's tracker arrow / mini-map indicator (or re-point at another active quest).
        RefreshObjectiveTarget(player);
    }

    public void AbandonQuest(Player player, int questId)
    {
        // Ignore a stray abandon fired in the moments right after accepting (the client has been seen
        // retransmitting it around the accept flow) - that would drop a just-taken quest.
        if ((DateTime.UtcNow - player.LastQuestAcceptedAt).TotalSeconds < 3)
            return;

        // Prefer the id the client sent; if it isn't a quest the player currently has active, fall back
        // to their single active quest (guards against the client sending an unexpected id).
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
        QuestDialogue.Clear(player); // no stale turn of an abandoned quest's conversation left to surface

        using (var db = _dbContextFactory.CreateDbContext())
        {
            var dbQuest = db.CharacterQuests.FirstOrDefault(x => x.QuestId == questId && x.CharacterId == player.CharacterId);
            if (dbQuest is not null)
            {
                db.CharacterQuests.Remove(dbQuest);
                db.SaveChanges();
            }
        }

        // Tell the client to remove the quest from the Hero's Journal, then restore the giver's "!".
        player.SendTunneled(new QuestAbandonedPacket { QuestId = questId });

        RefreshQuestNotifications(player, quest);

        // Remove the now-dangling tracker arrow / mini-map indicator (re-point at another active quest, or clear).
        RefreshObjectiveTarget(player);
    }

    public void SetActiveQuest(Player player, int questId)
    {
        if (!_resourceManager.Quests.TryGet(questId, out var quest))
            return;

        if (player.Quests.TryGetValue(questId, out var completed) && !completed)
        {
            player.ActiveQuestId = questId; // this is now the tracked quest for the arrow + "Take Me There"
            PersistTrackedQuest(player, questId);

            int done = player.QuestGoalProgress.TryGetValue(questId, out var progress) ? progress : 0;
            var goals = quest.EffectiveGoals;

            if (done < goals.Count)
                SendObjectiveActivated(player, questId, goals[done]);

            // Point the tracker/breadcrumb at the active goal's target.
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

        // Seed the journal's lifetime "quests completed" counter (op49/12) from the DB-backed state.
        player.SendTunneled(new CompletedQuestCountUpdatePacket
        {
            Count = player.Quests.Values.Count(done => done)
        });

        // Seed the storybook Adventurer's Journal's completed-quest set (op209/2) so earned stickers
        // show as complete on login.
        SendJournalQuestStates(player);
    }

    // Pushes the storybook Adventurer's Journal quest-state map (op209/2 QuestUpdate). RE-verified
    // (FUN_00a44020): a quest id being PRESENT in this map marks it completed in the journal (the value
    // is only used for ordering), so we send every completed quest id. Sent on login + after each
    // completion. Harmless for quests that aren't journal stickers - the client just ignores unknown ids.
    //
    private void SendJournalQuestStates(Player player)
    {
        var states = new Dictionary<int, int>();
        foreach (var (questId, completed) in player.Quests)
            if (completed)
                states[questId] = 1; // presence = completed; value is ordering only

        if (states.Count > 0)
            player.SendTunneled(new AdventurersJournalQuestUpdatePacket { QuestStates = states });
    }

    // Refreshes both the giver's and target's badge - most quest state changes touch both at once. Also
    // refreshes any mutually-exclusive quest's badges (ExcludesQuestIds): accepting/completing/abandoning
    // this quest can flip whether those are offerable too, and without this their giver's "!" would only
    // catch up on some unrelated event (relog, walking out of and back into range).
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

        // A plain AddNpc resend does NOT live-update an already-spawned NPC's world badge, so remove
        // the NPC and re-add it with the updated NotificationImageSetId.
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
                Unknown = true,
                CursorId = npc.CursorId,
                HasCursor = imageId != 0
            });
            player.SendTunneled(relevance);
        }

        if (imageId == 0)
        {
            player.SendTunneled(new PlayerUpdatePacketRemoveNotifications { Guids = [npc.Guid] });
            return;
        }

        var notifications = new PlayerUpdatePacketAddNotifications();
        notifications.Notifications.Add(new NotificationInfo
        {
            Guid = npc.Guid,
            Combat = false,
            ImageId = imageId,
            NameId = npc.NameId,
            SubTextId = npc.SubTextNameId,
        });
        player.SendTunneled(notifications);
    }

    // Sends the quest offer popup (QuestInfoPacket) for the giver NPC.
    private void Offer(Player player, QuestDefinition quest)
    {
        player.SendTunneled(new QuestInfoPacket
        {
            QuestId = quest.QuestId,
            // TitleId is the ONLY field that drives the visible NPCText speech bubble (confirmed live
            // 2026-08-02: DescriptionId does not render it) and it also gets written to the chat log on
            // a stock client, with no server-side suppression available - all 4 of this packet's unknown
            // fields are ruled out above, and no other field can carry the dialogue instead. This is a
            // known limitation without a client-side (ScriptsBase.bin) patch.
            TitleId = quest.GiverDialogueId,
            DescriptionId = quest.DescriptionId,
            // The collapsed details-box line: the QUEST NAME, retail-style ("Welcome to Seaside").
            // Feeding the objective sentence here (as before) left the offer with no visible title.
            HelperTextId = quest.TitleId,
            IconId = quest.IconId,
            Unknown6 = quest.ObjectiveDescriptionId, // offer "Goals" list
            // Unknown7=true showed a "members only" style UI (a lock/membership gate), NOT a chat-log
            // toggle - ruled out 2026-08-02. Matches QuestAddPacket.MembersOnly conceptually.
            Unknown7 = false,
            NpcGuid = quest.GiverGuid,
            // Unknown10=1 had no visible effect - ruled out 2026-08-02, not chat-related. All 4 unknown
            // fields in this packet are now accounted for; none control chat-log suppression.
            Unknown10 = 0,
            // Unknown11=true had no visible effect on the chat leak - ruled out 2026-08-02.
            Unknown11 = false,
            // Unknown12=true removes the decline option (accept-only quest) - ruled out 2026-08-02,
            // not chat-related.
            Unknown12 = false,
            RewardCoins = quest.RewardCoins,
            RewardExperience = quest.RewardExperience, // job XP shown in the reward preview
            RewardItems = BuildRewardItems(quest) // item icons in the "Show Details" reward preview
        });
    }

    // Resolves a quest's RewardItems def ids into reward-preview entries
    // (icon + name + count) by looking up each item's ClientItemDefinition. Shown as icons in the offer
    // and turn-in "Show Details" panels.
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

    // Ticks off the goal at goalIndex: sends the objective checkmark, advances the
    // player's progress, then either activates+retargets the next goal or, when this was the last goal,
    // hands the quest in (reward + end screen). Goals complete in order.
    // atNpcGuid = the NPC the goal was completed at, when that isn't the goal's nominal target (a counted
    // talk goal finishes at whichever of its NPCs the player reached last); 0 = use the nominal target.
    private void CompleteGoal(Player player, QuestDefinition quest, int goalIndex, ulong atNpcGuid = 0)
    {
        var goals = quest.EffectiveGoals;

        // The final goal ticks SILENTLY (checkmark, no "Goal Complete!" banner): the "Quest Completed!" banner
        // fires right after on turn-in, and two banners back-to-back make the second wait on the first's
        // animation. Intermediate goals still banner normally.
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

        // Persist progress so a relog mid-quest resumes on the right goal.
        UpdateCharacterQuest(player, quest.QuestId, q =>
        {
            q.GoalProgress = done;
            q.GoalCount = 0; // moving to the next goal - clear any collect count from the finished one
        });

        if (done >= goals.Count)
        {
            // Final goal done -> hand in (reward + "Quest Complete" end screen).
            TurnIn(player, quest);
            return;
        }

        // More goals to go: activate the next one and re-point the tracker/breadcrumb at its target. Its
        // row is already in the helper - SendActiveState adds every goal's row when the quest is taken,
        // so the player can see the whole checklist rather than one step at a time.
        SendObjectiveActivated(player, quest.QuestId, goals[done]);
        SendObjectiveForGoal(player, quest, done);

        // Mid-quest NPC reply.
        // ONLY TalkToNpc goals complete AT an NPC, so only they get the bubble: Kill/Collect/
        // EncounterComplete goals fire from field events, their DialogueId is the giver's MID-GOAL
        // reminder line, and popping it at the trigger moment reads wrong (at the arena win, Gerold's
        // "I can still hear those Frostfang Growlers howling..." looked like another wave inbound)
        // while camera-focusing an NPC who may not even be in the player's zone.
        var completedGoal = goals[goalIndex];
        if (completedGoal.Type == QuestGoalType.TalkToNpc)
        {
            // A counted talk goal completes at whichever of its NPCs was talked to last, so the line (and
            // the camera) must follow that NPC, not the goal's nominal first target.
            ulong spokenBy = atNpcGuid != 0 ? atNpcGuid : GoalTargetGuid(quest, goalIndex);
            QuestDialogue.Begin(player, completedGoal.ConversationFor(spokenBy), spokenBy);
        }
    }

    // Shows the "Quest Complete" end screen; finalize happens on the Complete click. The completing
    // goal's checkmark is already sent by CompleteGoal before this is called.
    private void TurnIn(Player player, QuestDefinition quest)
    {
        // No QuestAdd re-send here: the end screen's bubble reads QuestEndPacket's own TitleId field
        // below, not QuestData at all, so nothing needs refreshing. Re-sending QuestAdd would APPEND a
        // duplicate journal row (the client never dedupes) that completion then can't fully clear -
        // the bug that left finished quests in the journal.
        player.SendTunneled(new QuestEndPacket
        {
            // Camera focus = the LAST goal's NPC (where hand-in happens). For single-goal quests this is
            // quest.TargetGuid; for multi-goal it's the final goal's target (e.g. back at the giver).
            NpcGuid = GoalTargetGuid(quest, quest.EffectiveGoals.Count - 1),
            QuestId = quest.QuestId,
            // With the ScriptsBase details-split applied, the end screen's speech bubble reads
            // SetNPCDialog(showEndText), and showEndText is fed by THIS packet's TitleId field (verified
            // in-game: the bubble showed whatever went here). So put the turn-in DIALOGUE here. The panel
            // title + "Show Details" description come from QuestData columns 1/2 (set by SendActiveState:
            // col1=TitleId title, col2=ObjectiveDescriptionId objective), independent of this packet.
            TitleId = quest.TurnInDialogueId, // -> showEndText -> speech bubble = the NPC's turn-in line
            DescriptionId = quest.TitleId,    // -> showEndId (not rendered as text); harmless
            RewardCoins = quest.RewardCoins,
            RewardExperience = quest.RewardExperience, // job XP shown in the reward preview
            RewardItems = BuildRewardItems(quest) // item icons in the "Show Details" reward preview
        });

        // Reward/completion is applied when the player clicks "Complete" (QuestEndReply invokes this).
        player.PendingQuestEndAction = () => CompleteQuest(player, quest.QuestId);
    }

    // The journal/tracker entry. HelperTextId (client QuestData column 10) is read by the end
    // screen's speech bubble on a patched client (ScriptsBase.bin points ShowEndScreen's SetNPCDialog
    // at column 10 instead of DescriptionId, decoupling it from the journal) - but on a STOCK/retail
    // client, column 10 is also what the quest-helper TRACKER widget reads natively as its header
    // while the quest is active (disassembly of the unmodified bytecode confirmed this). So this value
    // must stay short while the quest is in progress: SendActiveState passes ObjectiveDescriptionId
    // (retail-safe), not the long TurnInDialogueId. The actual turn-in bubble doesn't depend on this at
    // all - the end screen reads QuestEndPacket's own TitleId field directly (see TurnIn()), on both
    // patched and stock clients, so it stays correct regardless of what's sent here.
    private static void SendQuestAdd(Player player, QuestDefinition quest, int helperTextId, float completedPercentage = 0f)
    {
        player.SendTunneled(new QuestAddPacket
        {
            QuestId = quest.QuestId,
            TitleId = quest.TitleId,
            // DescriptionId (client QuestData col 2) feeds BOTH the on-screen tracker's header line AND the
            // StoryBook journal's right-page description. Use the objective ("Introduce yourself to X in Y")
            // so the tracker header reads as the objective; the shorter sub-goal ("Talk to X") is the goal
            // row (QuestObjectiveAddedPacket, from the goal's NameId). They share this one client slot, so
            // the journal description shows the objective too rather than the longer flavour DescriptionId.
            DescriptionId = quest.ObjectiveDescriptionId,
            HelperTextId = helperTextId,
            MembersOnly = false,
            TimeStarted = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ProfileId = quest.ProfileId,
            CompletedPercentage = completedPercentage,
            IconId = quest.IconId,
            SystemQuest = false
        });
    }

    // QuestAdd + objective packets that put the quest into the client's journal + tracker.
    private void SendActiveState(Player player, QuestDefinition quest)
    {
        int alreadyDone = player.QuestGoalProgress.TryGetValue(quest.QuestId, out var p) ? p : 0;
        SendQuestAdd(player, quest, quest.ObjectiveDescriptionId, (float)alreadyDone / quest.EffectiveGoals.Count);

        // FULL CHECKLIST: every goal gets its row up front, so the quest helper shows the whole quest
        // (done, current, and still-to-come) rather than revealing steps one at a time. Reversed from the
        // earlier progressive reveal on user report - a helper that hides the remaining steps reads as if
        // the quest only ever has one objective. CompleteGoal therefore only ACTIVATES the next goal; it
        // no longer adds its row (that would duplicate one of these).
        var goals = quest.EffectiveGoals;
        int done = player.QuestGoalProgress.TryGetValue(quest.QuestId, out var progress) ? progress : 0;

        for (int i = 0; i < goals.Count; i++)
        {
            player.SendTunneled(new QuestObjectiveAddedPacket
            {
                QuestId = quest.QuestId,
                // Body int0 is the objective's IDENTITY (the client hashes rows by it - traced
                // FUN_00bab950: row+0xf0 = int0) AND its name text id; Activated/Complete find the row
                // by sending the same value as ObjectiveId. Goal NameIds must therefore be unique
                // within a quest. (A raw index here broke everything: id 0 rendered as
                // "<STRING 0 NOT FOUND>" and the Activated/Complete lookups missed, so checkmarks and
                // goal advance never showed client-side.)
                ObjectiveNameId = goals[i].NameId,
                // The tracker goal row renders from body int1 ("Talk to Shakey").
                ObjectiveDescriptionId = goals[i].NameId,
                // Body int2 = the journal "Objectives" sub-line ("Shakey should be hanging out in
                // front of the Wildwood Speedway...").
                ObjectiveField2 = goals[i].DescriptionId != 0 ? goals[i].DescriptionId : goals[i].NameId
            });
        }

        // Replay already-completed goals as ticked (restores checkmarks after relog).
        for (int i = 0; i < done && i < goals.Count; i++)
        {
            player.SendTunneled(new QuestObjectiveCompletePacket
            {
                QuestId = quest.QuestId,
                ObjectiveId = goals[i].NameId,
                Percent = 1f,
                Silent = true // relog replay -> tick the checkmark but don't re-banner old goals
            });
        }

        // Activate the current goal (the first not-yet-done one).
        if (done < goals.Count)
        {
            var activeGoal = goals[done];

            SendObjectiveActivated(player, quest.QuestId, activeGoal);

            // If it's a count goal (Collect/Kill/counted talk) with restored progress (relog mid-count), show the current
            // count so the tracker reads e.g. 3/8 instead of 0/8. Activated only sets the "required" half.
            if ((activeGoal.Type is QuestGoalType.Collect or QuestGoalType.Kill || activeGoal.IsCountedTalk)
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

        // Point the tracker + "Take Me There" breadcrumb at the active goal's target NPC.
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

    // The NPC guid the goal at goalIndex points at: the goal's own TargetGuid, or the
    // quest's turn-in TargetGuid when the goal doesn't override it (or when all goals are already done).
    private static ulong GoalTargetGuid(QuestDefinition quest, int goalIndex)
    {
        var goals = quest.EffectiveGoals;
        if (goalIndex >= 0 && goalIndex < goals.Count && goals[goalIndex].TargetGuid != 0)
            return goals[goalIndex].TargetGuid;
        return quest.TargetGuid;
    }

    // Player-aware objective target: the NPC the tracker arrow / "Take Me There" breadcrumb should point
    // at for the active goal. For an EncounterComplete goal this is the encounter's world giver (the
    // Frostfang Growler wolf near spawn â€” the thing you click to enter the arena), whose guid is dynamic;
    // for every other goal it's the static GoalTargetGuid.
    //
    // CORRECTED 2026-07-28 (live feedback: "Bixies Gone Bad"'s tracker light was on Sunflower instead of
    // the Bixie Hive dungeon entrance) - the switch below only ever knew the two bespoke wandering-NPC
    // encounters (Frostfang/Tormented Spirits); every REAL atlas dungeon (Bixie Hive, Cracked Claw Caverns,
    // and any future one) fell through to null and then to the static GoalTargetGuid fallback below, which
    // resolves to the quest's turn-in NPC - wrong for an "enter the dungeon" goal. Added a generic fallback
    // via StartingZone.DungeonEntrance(EncounterId), which covers every atlas dungeon at once instead of
    // needing its own one-off case here.
    private ulong ResolveGoalTargetGuid(Player player, QuestDefinition quest, int goalIndex)
    {
        var goals = quest.EffectiveGoals;
        if (goalIndex >= 0 && goalIndex < goals.Count
            && goals[goalIndex].Type == QuestGoalType.EncounterComplete
            && player.Zone is StartingZone startingZone)
        {
            var entry = goals[goalIndex].EncounterId switch
            {
                Zones.FrostfangArenaZone.EncounterId => startingZone.GrowlerWolf,
                Zones.TormentedSpiritsArenaZone.EncounterId => startingZone.TormentedSpiritEntry(),
                _ => startingZone.DungeonEntrance(goals[goalIndex].EncounterId),
            };
            if (entry is not null)
                return entry.Guid;
        }

        // A Kill goal has no fixed NPC: fall back to the NEAREST still-living kill target. (The static
        // fallback resolved to quest.TargetGuid = the GIVER, which aimed the player back at the quest
        // NPC with no clue where the enemies were.) The visible indicator for Kill goals is the AREA
        // pin built by SendObjectiveForGoal; this guid path serves the walk-to/pathfinding consumers.
        if (goalIndex >= 0 && goalIndex < goals.Count
            && goals[goalIndex].Type == QuestGoalType.Kill)
        {
            var nearest = NearestLivingKillTarget(player, goals[goalIndex]);
            if (nearest is not null)
                return nearest.Guid;
        }

        // A COUNTED talk goal has several interchangeable NPCs: point at the nearest one this player hasn't
        // spoken to yet, so the marker walks them round the remaining Freewheelers instead of staying
        // pinned on the first (already-credited) one.
        if (goalIndex >= 0 && goalIndex < goals.Count && goals[goalIndex].IsCountedTalk)
        {
            var nearest = NearestUntalkedTarget(player, goals[goalIndex]);
            if (nearest != 0)
                return nearest;
        }

        // A Collect goal has no fixed NPC either: point at the NEAREST pickup this player hasn't taken
        // yet, so the marker/breadcrumb leads to the tools. Any pickup credits the goal (it's a counter),
        // so this is guidance only - the player can grab whichever they find first.
        if (goalIndex >= 0 && goalIndex < goals.Count
            && goals[goalIndex].Type == QuestGoalType.Collect)
        {
            var nearest = NearestUncollectedPickup(player, quest.QuestId, goalIndex);
            if (nearest is not null)
                return nearest.Guid;
        }

        return GoalTargetGuid(quest, goalIndex);
    }

    // Nearest Collect pickup for (questId, goalIndex) that this player hasn't gathered yet, or null when
    // none remain in this zone. Pickups are the collectible NPCs spawned from the goal's CollectSpawns.
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

    // Nearest of a counted talk goal's NPCs that this player hasn't spoken to yet, or 0 when they've all
    // been credited (or none are in this zone) - in which case the caller falls back to the static target.
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
            var d2 = dx * dx + dz * dz;
            if (d2 < best)
            {
                best = d2;
                nearest = guid;
            }
        }

        return nearest;
    }

    // Nearest still-living NPC that credits the given Kill goal, or null when none remain in this zone.
    private static Npc? NearestLivingKillTarget(Player player, QuestGoal goal)
    {
        var ids = goal.AllKillNameIds().ToHashSet();
        if (ids.Count == 0)
            return null;

        Npc? nearest = null;
        var best = float.MaxValue;
        foreach (var npc in player.Zone.Npcs)
        {
            if (!ids.Contains(npc.NameId) || !npc.IsAlive)
                continue;
            var dx = npc.Position.X - player.Position.X;
            var dz = npc.Position.Z - player.Position.Z;
            var d2 = dx * dx + dz * dz;
            if (d2 < best)
            {
                best = d2;
                nearest = npc;
            }
        }
        return nearest;
    }

    // The hunt AREA for a Kill goal = centroid of its living targets. Label = the primary kill NPC's
    // NameId so the indicator reads e.g. "Bixie Soldier".
    private static bool TryGetKillArea(Player player, QuestGoal goal, out Vector4 center, out int labelNameId)
    {
        center = default;
        labelNameId = 0;
        var ids = goal.AllKillNameIds().ToHashSet();
        if (ids.Count == 0)
            return false;

        float sx = 0, sy = 0, sz = 0;
        int n = 0;
        foreach (var npc in player.Zone.Npcs)
        {
            if (!ids.Contains(npc.NameId) || !npc.IsAlive)
                continue;
            sx += npc.Position.X;
            sy += npc.Position.Y;
            sz += npc.Position.Z;
            n++;
            if (labelNameId == 0)
                labelNameId = npc.NameId;
        }
        if (n == 0)
            return false;

        center = new Vector4(sx / n, sy / n, sz / n, 1f);
        if (goal.KillNpcNameId != 0)
            labelNameId = goal.KillNpcNameId;
        return true;
    }

    // Goal-aware objective indicator: a Kill goal marks the hunt AREA (centroid of its living targets,
    // Guid 0 - the retail "go to this area" pin, so the marker doesn't single out one highlighted
    // enemy); every other goal type points at its target NPC.
    private void SendObjectiveForGoal(Player player, QuestDefinition quest, int goalIndex)
    {
        var goals = quest.EffectiveGoals;

        // ReachLocation: pin the destination itself (Guid 0 - a place, not an entity). Label = the
        // goal row's text ("Take a look at the view").
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

        if (goalIndex >= 0 && goalIndex < goals.Count
            && goals[goalIndex].Type == QuestGoalType.Kill
            && TryGetKillArea(player, goals[goalIndex], out var center, out var labelNameId))
        {
            var zoneAreaId = player.Zone is StartingZone startingZone
                ? startingZone.GetZoneAreaId(center)
                : player.Zone.Id;

            player.SendTunneled(new ObjectiveTargetUpdatePacket
            {
                Active = true,
                LocationX = center.X,
                LocationZ = center.Z,
                ZoneId = zoneAreaId,
                Guid = 0, // no NPC: a location pin, not an entity arrow
                NameId = labelNameId,
                PositionX = center.X,
                PositionY = center.Y,
                PositionZ = center.Z,
                PositionW = 1f
            });
            return;
        }

        SendObjectiveTarget(player, ResolveGoalTargetGuid(player, quest, goalIndex));
    }

    // Sends the ObjectiveTargetUpdatePacket that drives the tracker arrow, mini-map indicator and the
    // "Take Me There" green breadcrumb trail. Target is the given NPC guid (the active goal's NPC); if it
    // isn't spawned in the player's current zone we send nothing (no destination to point at).
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
            // Display name shown on the tracker/mini-map indicator; the client resolves this id to the
            // label (0/invalid renders the "Default Housing NPC" fallback).
            NameId = target.NameId,
            PositionX = pos.X,
            PositionY = pos.Y,
            PositionZ = pos.Z,
            PositionW = 1f
        });
    }

    // Re-points the objective tracker/mini-map indicator at a still-active quest whose target NPC is
    // present, or clears it entirely (Active=false) when no trackable quest remains. Call after a quest
    // leaves the active set (abandon/complete) so a dangling indicator doesn't stay on screen, and on
    // overworld re-entry (a goal completed inside a battle instance points its next goal at an NPC that
    // isn't in that zone, so the in-arena update was skipped â€” e.g. "Return to Chloe" after the
    // Tormented Spirits dungeon kept the arrow on the entry spirit).
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

            // Once every goal is done the index sits one past the end (the quest is waiting to be handed
            // in), so only look at the goal itself while it's still in range.
            var onGoal = goalIndex >= 0 && goalIndex < goals.Count;

            // Reach goal: walk to the destination itself.
            if (onGoal && goals[goalIndex].Type == QuestGoalType.ReachLocation
                && goals[goalIndex].ReachPosition.Length >= 3)
            {
                var rp = goals[goalIndex].ReachPosition;
                targetPosition = new Vector3(rp[0], rp[1], rp[2]);
                return true;
            }

            // Kill goal: walk to the CLOSEST living enemy (the area centroid can be empty air in the
            // middle of a camp).
            if (onGoal && goals[goalIndex].Type == QuestGoalType.Kill
                && NearestLivingKillTarget(player, goals[goalIndex]) is { } enemy)
            {
                targetPosition = new Vector3(enemy.Position.X, enemy.Position.Y, enemy.Position.Z);
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

    // The quest + active goal the tracker/mini-map/Take Me There should follow: the player's selected
    // ActiveQuestId when it's still active and trackable in this zone; otherwise the first active quest
    // with a trackable goal. False when nothing is trackable.
    private bool TryGetTrackedGoal(Player player, out QuestDefinition quest, out int goalIndex)
    {
        // Prefer the quest the player actually has selected - the whole point of "make active" is that the
        // arrow and Take Me There follow IT, not whatever quest happens to be first in storage order.
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

    // The active goal of questId when it can be tracked from the player's current zone: a Kill goal
    // needs at least one living counted enemy here; every other goal type needs its resolved target
    // NPC spawned here.
    private bool TryGetTrackableGoal(Player player, int questId, out QuestDefinition quest, out int goalIndex)
    {
        quest = null!;
        goalIndex = -1;
        if (!_resourceManager.Quests.TryGet(questId, out var q))
            return false;

        int done = player.QuestGoalProgress.TryGetValue(questId, out var progress) ? progress : 0;
        var goals = q.EffectiveGoals;

        if (done >= 0 && done < goals.Count && goals[done].Type == QuestGoalType.Kill)
        {
            if (NearestLivingKillTarget(player, goals[done]) is null)
                return false;
            quest = q;
            goalIndex = done;
            return true;
        }

        // Reach goals are always trackable — the destination is a fixed world position.
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

        // Job/profile XP - grant to the active job (updates the job's level bar).
        var experience = quest.RewardExperience;
        if (experience > 0)
        {
            player.AwardXp(experience);

            // AwardXp only updates in-memory state and defers DB persistence to the normal
            // save-on-disconnect path (fine for combat kills, but a one-shot quest reward should be
            // as durable as the coins/items granted above it - otherwise a crash before logout
            // silently drops XP the client already showed the player).
            using (var db = _dbContextFactory.CreateDbContext())
            {
                var dbCharacter = db.Characters.Include(c => c.Profiles).FirstOrDefault(c => c.Id == player.CharacterId);
                var dbProfile = dbCharacter?.Profiles.FirstOrDefault(p => p.Id == player.ActiveProfile.Id);
                if (dbProfile is not null)
                {
                    dbProfile.Level = player.ActiveProfile.Rank;
                    dbProfile.LevelXP = player.ActiveProfile.LevelXpRaw;
                    db.SaveChanges();
                }
            }
        }

        // Reward-earned celebration (coins + XP fly-in with sound).
        if (coins > 0 || experience > 0)
            player.SendTunneled(new RewardBundlePacket { Coins = coins, Xp = experience });

        // Item rewards - defined per quest in Resources/Quests.json ("RewardItems": [id, ...]).
        foreach (var itemDefinitionId in quest.RewardItems)
        {
            GrantItem(player, itemDefinitionId);

            // "You earned an item" celebration (opcode 50/2): shows the item icon + "received 1".
            player.SendTunneled(new RewardNonBundledItemPacket { ItemDefinitionId = itemDefinitionId, Quantity = 1 });
        }
    }

    // Grants one of definitionId to the player: stacks it in the DB (by definition +
    // tint), mirrors it into the in-memory inventory, and tells the client (ItemAdd for a new item, or
    // ItemUpdate for an incremented stack). Mirrors the coin-store grant path.
    public void GrantItem(Player player, int definitionId)
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
