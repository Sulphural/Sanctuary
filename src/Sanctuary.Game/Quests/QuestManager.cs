using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;

using Sanctuary.Core.IO;
using Sanctuary.Database;
using Sanctuary.Database.Entities;
using Sanctuary.Game.Entities;
using Sanctuary.Game.Interactions;
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
    private readonly IRewardManager _rewardManager;
    private readonly IDbContextFactory<DatabaseContext> _dbContextFactory;

    public QuestManager(IResourceManager resourceManager, IDbContextFactory<DatabaseContext> dbContextFactory,
        IRewardManager rewardManager)
    {
        _resourceManager = resourceManager;
        _dbContextFactory = dbContextFactory;
        _rewardManager = rewardManager;
    }

    // Clears out DAILY quests finished on an earlier UTC day so they can be taken again. Called before
    // anything asks what an NPC has to offer, and on login, so the reset lands whether the player logs in
    // fresh or simply plays past midnight. Same calendar-day comparison the daily wheel uses.
    public void ExpireDailyQuests(Player player)
    {
        var today = DateTime.UtcNow.Date;
        List<int>? expired = null;

        foreach (var (questId, completed) in player.Quests)
        {
            if (!completed || !_resourceManager.Quests.TryGet(questId, out var quest) || !quest.IsDaily)
                continue;

            (expired ??= new List<int>()).Add(questId);
        }

        if (expired is null)
            return;

        using var db = _dbContextFactory.CreateDbContext();
        bool changed = false;

        foreach (var questId in expired)
        {
            var row = db.CharacterQuests.FirstOrDefault(x => x.QuestId == questId && x.CharacterId == player.CharacterId);

            // No stamp at all means it predates daily support - treat it as long past and let it come round.
            if (row?.CompletedUtc is { } stamp && stamp.UtcDateTime.Date >= today)
                continue;

            if (row is not null)
            {
                db.CharacterQuests.Remove(row);
                changed = true;
            }

            player.Quests.Remove(questId);
            player.QuestGoalProgress.Remove(questId);
            player.QuestCollectProgress.Remove(questId);

            // Swap the greyed "come back tomorrow" badge back to the available one without a relog.
            if (_resourceManager.Quests.TryGet(questId, out var rolled))
                RefreshQuestNotification(player, rolled.GiverGuid);
        }

        if (changed)
            db.SaveChanges();
    }

    public bool IsQuestNpc(ulong npcGuid)
        => _resourceManager.Quests.ByGiver.ContainsKey(npcGuid) || _resourceManager.Quests.ByTarget.ContainsKey(npcGuid);

    // Every quest this NPC could start or advance for this player right now, as radial-menu options.
    // OnNpcInteract acts on the FIRST match and returns; this enumerates them all so the player can
    // pick - e.g. Chloe both takes the turn-in for "Ninja: That's the Spirit" and offers "Ninja: Strike
    // from the Shadows", and neither should be unreachable because the other happened to be found first.
    public List<NpcInteractionOption> GetInteractionOptions(Player player, Npc npc)
    {
        ExpireDailyQuests(player);

        var options = new List<NpcInteractionOption>();
        var quests = _resourceManager.Quests;

        // Advance/turn in first, matching OnNpcInteract's own precedence.
        foreach (var (questId, completed) in player.Quests)
        {
            if (completed || !quests.TryGet(questId, out var activeQuest))
                continue;

            if (!AdvancesHere(player, activeQuest, npc, out _))
                continue;

            var quest = activeQuest;
            options.Add(new NpcInteractionOption
            {
                IconId = quest.RadialTurnInIconId != 0 ? quest.RadialTurnInIconId : ContextIcons.QuestTurnIn,
                ButtonTextId = quest.TitleId,
                Invoke = interactingPlayer => AdvanceAtNpc(interactingPlayer, quest, npc)
            });
        }

        if (quests.ByGiver.TryGetValue(npc.Guid, out var giverQuestIds))
        {
            foreach (var questId in giverQuestIds)
            {
                if (!quests.TryGet(questId, out var offerableQuest) || !offerableQuest.IsOfferableFor(player.Quests))
                {
                    // A daily already taken today still gets a row - greyed out, doing nothing when picked
                    // - so it reads as "this exists, come back tomorrow" rather than vanishing.
                    if (offerableQuest is { IsDaily: true, RadialCompletedIconId: not 0 }
                        && player.Quests.TryGetValue(questId, out var doneToday) && doneToday)
                    {
                        options.Add(new NpcInteractionOption
                        {
                            IconId = offerableQuest.RadialCompletedIconId,
                            ButtonTextId = offerableQuest.TitleId,
                            Invoke = _ => { }
                        });
                    }

                    continue;
                }

                var quest = offerableQuest;
                options.Add(new NpcInteractionOption
                {
                    IconId = quest.RadialOfferIconId != 0 ? quest.RadialOfferIconId : ContextIcons.QuestOffer,
                    ButtonTextId = quest.TitleId,
                    Invoke = interactingPlayer => Offer(interactingPlayer, quest)
                });
            }
        }

        return options;
    }

    // Does talking to this NPC advance `quest`'s CURRENT goal? Shared by OnNpcInteract (which acts on
    // the first hit) and GetInteractionOptions (which lists them all) so the two can never disagree
    // about what a click on this NPC would do.
    private bool AdvancesHere(Player player, QuestDefinition quest, Npc npc, out int goalIndex)
    {
        goalIndex = player.QuestGoalProgress.TryGetValue(quest.QuestId, out var progress) ? progress : 0;

        var goals = quest.EffectiveGoals;
        if (goalIndex >= goals.Count)
            return false;

        // Collect/Kill/EncounterComplete goals advance only through their own events; GoalTargetGuid
        // would fall back to the quest's turn-in NPC and talking would bypass the objective.
        if (goals[goalIndex].Type is QuestGoalType.Collect or QuestGoalType.Kill or QuestGoalType.EncounterComplete)
            return false;

        if (goals[goalIndex].IsCountedTalk)
            return IsCountedTalkTarget(quest, goalIndex, npc);

        return GoalTargetGuid(quest, goalIndex) == npc.Guid;
    }

    // Runs the advance that AdvancesHere reported, taking the same two paths OnNpcInteract does.
    private void AdvanceAtNpc(Player player, QuestDefinition quest, Npc npc)
    {
        if (!AdvancesHere(player, quest, npc, out int goalIndex))
            return; // state moved on between building the menu and the player picking from it

        if (quest.EffectiveGoals[goalIndex].IsCountedTalk)
            TryCreditCountedTalk(player, quest, goalIndex, npc);
        else
            CompleteGoal(player, quest, goalIndex);
    }

    // A counted talk goal ("Talk to Freewheelers - 0/3") is satisfied by any of several NPCs, so
    // membership - not a single target guid - decides whether this NPC advances it.
    private static bool IsCountedTalkTarget(QuestDefinition quest, int goalIndex, Npc npc)
        => quest.EffectiveGoals[goalIndex].AllTalkTargetGuids().Contains(npc.Guid);

    // Single-action interact: do whatever the NPC's menu would have listed first. GetInteractionOptions
    // already orders goal progression / turn-in ahead of new offers, which is the precedence this had
    // when it walked the player's quests itself - talking to an NPC finishes what you are carrying
    // before it hands you something new.
    public void OnNpcInteract(Player player, Npc npc)
    {
        var options = GetInteractionOptions(player, npc);

        if (options.Count > 0)
            options[0].Invoke(player);
    }

    // /scare's QuickChat id, read straight out of the client's EmoteHandler table
    // (main/146: SETTABLE cmd="scare" / id=219). Others for reference: cheer 145, wave 143, point 139.
    public const int ScareQuickChatId = 219;

    // How close the player has to be for a scare to land on an NPC. Generous enough to cover "in front
    // of them" without letting one emote spook a whole plaza.
    private const float ScareRange = 8f;

    // The player used an emote. Emotes reach us as QuickChat (the client's EmoteHandler binds each one to
    // Ui.ProcessQuickChatCommand), so this is called from the quick-chat handler rather than any emote
    // packet - there isn't one. /scare is id 219.
    public void OnQuickChatEmote(Player player, int quickChatId)
    {
        var playerPosition = new Vector3(player.Position.X, player.Position.Y, player.Position.Z);

        foreach (var (questId, completed) in player.Quests)
        {
            if (completed || !_resourceManager.Quests.TryGet(questId, out var quest))
                continue;

            int done = player.QuestGoalProgress.TryGetValue(questId, out var progress) ? progress : 0;
            var goals = quest.EffectiveGoals;
            if (done >= goals.Count)
                continue;

            var goal = goals[done];

            // "Do <emote> over there": the right emote, close enough to the spot, and the goal ticks off.
            if (goal.Type == QuestGoalType.UseEmote)
            {
                if (goal.EmoteQuickChatId != quickChatId || goal.ReachPosition.Length < 3)
                    continue;

                var spot = new Vector3(goal.ReachPosition[0], goal.ReachPosition[1], goal.ReachPosition[2]);
                float radius = goal.ReachRadius > 0f ? goal.ReachRadius : 10f;

                if (Vector3.Distance(playerPosition, spot) <= radius)
                    CompleteGoal(player, quest, done);

                continue;
            }

            if (quickChatId != ScareQuickChatId || !goal.IsCountedTalk || !goal.RequiresScare)
                continue;

            foreach (var targetGuid in goal.AllTalkTargetGuids())
            {
                // Already collected from - scaring them again would let one NPC pay out twice.
                if (player.TalkedQuestNpcs.Contains(targetGuid))
                    continue;

                if (!player.Zone.TryGetNpc(targetGuid, out var npc))
                    continue;

                var npcPosition = new Vector3(npc.Position.X, npc.Position.Y, npc.Position.Z);
                if (Vector3.Distance(playerPosition, npcPosition) > ScareRange)
                    continue;

                if (player.ScaredNpcs.Add(targetGuid))
                {
                    // Only the BEAM comes off here - it meant "needs scaring", and that is now done. The
                    // badge stays until the candy is actually collected (see Player.GetNotificationImageId),
                    // which is what keeps a scared-but-uncollected townsperson findable.
                    //
                    // ★ And because no badge change is needed, nothing respawns the NPC - so the reaction
                    // cannot be stomped, which is what made the ordering here delicate before.
                    SetScareSpotlight(player, targetGuid, lit: false);

                    QuestDialogue.PlayScareReaction(player, targetGuid);
                }
            }
        }
    }

    // ── The spotlight on a scare target ───────────────────────────────────────────────────────────────
    // ★ 5486 PFX_light_white_root_god-beam_med_loop - a white light COLUMN anchored at the actor's ROOT,
    // i.e. a beam standing on the ground around them, which is exactly what retail put on the costumed
    // townsfolk ("NPCs with the spotlight beam around them"). The family has sm/med/lg/huge variants
    // (5568/5486/5569/5570); med is the person-sized one.
    //
    // ★★ ATTACHED PER PLAYER, NOT ON THE NPC. Npc.AttachedEffectId would light the NPC up for EVERYONE,
    // including players who have not taken the quest or have already scared that one - and the whole point
    // is that the beam marks what YOU still have to do. An effect tag is per-recipient state, the same
    // mechanism the snowball arena's team markers use, so each player sees their own set.
    private const int ScareSpotlightFxId = 5486;
    private const int ScareSpotlightTagId = 91040;

    // Light up every NPC this player still has to scare, and put out every beam that is no longer earned.
    //
    // ★★ IT MUST CLEAR AS WELL AS LIGHT. An earlier version only walked the player's ACTIVE quests and lit
    // their targets - so the moment the quest was completed or abandoned it stopped considering those NPCs
    // at all, and every beam already attached stayed burning on the client with nothing left to ever turn
    // it off. Working out the wanted set FIRST and then reconciling the currently-lit set against it makes
    // this self-correcting: whatever state the player was left in, one call puts it right.
    public void RefreshScareSpotlights(Player player)
    {
        var wanted = new HashSet<ulong>();

        foreach (var quest in ActiveScareQuests(player))
        {
            int goalIndex = player.QuestGoalProgress.TryGetValue(quest.QuestId, out var p) ? p : 0;
            var goals = quest.EffectiveGoals;
            if (goalIndex >= goals.Count)
                continue;

            var goal = goals[goalIndex];
            if (!goal.RequiresScare)
                continue;

            foreach (var targetGuid in goal.AllTalkTargetGuids())
            {
                if (!player.TalkedQuestNpcs.Contains(targetGuid) && !player.ScaredNpcs.Contains(targetGuid))
                    wanted.Add(targetGuid);
            }
        }

        // Put out anything lit that no longer belongs - snapshot first, the loop mutates the set.
        foreach (var guid in player.SpotlitNpcs.ToArray())
        {
            if (!wanted.Contains(guid))
                SetScareSpotlight(player, guid, lit: false);
        }

        foreach (var guid in wanted)
            SetScareSpotlight(player, guid, lit: true);
    }

    private static void SetScareSpotlight(Player player, ulong npcGuid, bool lit)
    {
        if (lit == player.SpotlitNpcs.Contains(npcGuid))
            return; // already in the right state - re-attaching would stack a second beam

        if (lit)
        {
            player.SpotlitNpcs.Add(npcGuid);
            player.SendTunneled(new PlayerUpdatePacketAddEffectTagCompositeEffect
            {
                Guid = npcGuid,
                TagId = ScareSpotlightTagId,
                CompositeEffectId = ScareSpotlightFxId,
                SourceGuid = npcGuid,
            });
            return;
        }

        player.SpotlitNpcs.Remove(npcGuid);
        player.SendTunneled(new PlayerUpdatePacketRemoveEffectTagCompositeEffect
        {
            Guid = npcGuid,
            TagId = ScareSpotlightTagId,
        });
    }

    private IEnumerable<QuestDefinition> ActiveScareQuests(Player player)
    {
        foreach (var (questId, completed) in player.Quests)
        {
            if (completed || !_resourceManager.Quests.TryGet(questId, out var quest))
                continue;

            yield return quest;
        }
    }

    // ── The trick-or-treat conversation ───────────────────────────────────────────────────────────────
    // Button dressing, matched to the retail screenshots: the dare is closed by a plain beige "Exit." with
    // the leave arrow, the thank-you by a green "Thanks!" with the tick. Ids are the real ones -
    // 3104 "Exit.", 1911 "Thanks!", image 4008 ui_dialog_leave, 300 ui_dialog_greencheck, sets 18/17.
    private const int ScareExitTextId = 3104;
    private const int ScareThanksTextId = 1911;
    private const int ScareLeaveImageId = 4008;
    private const int ScareCheckImageId = 300;
    private const int ScareBeigeButtonSet = 18;
    private const int ScareGreenButtonSet = 17;
    private const int ScareResponseId = 1;

    // ★ WHICH LINE AN NPC USES IS FIXED BY THEIR GUID, not rolled. Retail gives each costumed townsperson
    // one personality - the hiccuping one always hiccups - so re-approaching the same NPC must not reshuffle
    // what they say. Same index into both lists, which is what pairs the dare with its matching thank-you.
    private static int ScarePairIndex(ulong npcGuid, int count) =>
        count <= 0 ? -1 : (int)(npcGuid % (ulong)count);

    private static void ShowScareIntro(Player player, QuestGoal goal, Npc npc)
    {
        int index = ScarePairIndex(npc.Guid, goal.ScareIntroDialogueIds.Count);
        if (index < 0)
            return; // no conversation authored - the goal just isn't a talking one

        QuestDialogue.PlayTalkAnimation(player, npc.Guid);

        var dialog = new CommandPacketShowDialog
        {
            DialogueTextId = goal.ScareIntroDialogueIds[index],
            NpcGuid = npc.Guid,
            CameraFocusParam = 1f,
        };

        dialog.Responses.Add(new CommandPacketShowDialog.Response
        {
            Id = ScareResponseId,
            LabelTextId = ScareExitTextId,
            Param1 = ScareLeaveImageId,
            Param2 = ScareBeigeButtonSet,
        });

        // No action: this half of the conversation only tells the player what to do.
        player.PendingDialogChoices = null;
        player.SendTunneled(dialog);
    }

    private void ShowScareThanks(Player player, QuestDefinition quest, int goalIndex, QuestGoal goal, Npc npc)
    {
        int index = ScarePairIndex(npc.Guid, goal.ScareThanksDialogueIds.Count);

        // No thank-you authored - fall straight through to the payout so the goal can never stall.
        if (index < 0)
        {
            CreditScaredNpc(player, quest, goalIndex, npc);
            return;
        }

        QuestDialogue.PlayTalkAnimation(player, npc.Guid);

        var dialog = new CommandPacketShowDialog
        {
            DialogueTextId = goal.ScareThanksDialogueIds[index],
            NpcGuid = npc.Guid,
            CameraFocusParam = 1f,
        };

        dialog.Responses.Add(new CommandPacketShowDialog.Response
        {
            Id = ScareResponseId,
            LabelTextId = ScareThanksTextId,
            Param1 = ScareCheckImageId,
            Param2 = ScareGreenButtonSet,
        });

        // ★ THE PAYOUT RIDES THE BUTTON. Crediting on the click that OPENS this would hand over the candy
        // before the player has been told what they got.
        var guid = npc.Guid;
        player.PendingDialogChoices = new Dictionary<int, Action>
        {
            [ScareResponseId] = () =>
            {
                if (player.Zone.TryGetNpc(guid, out var target))
                    CreditScaredNpc(player, quest, goalIndex, target);
            },
        };

        player.SendTunneled(dialog);
    }

    // The payout: spend the scare, hand over a random candy, then run the normal counted-talk credit.
    private void CreditScaredNpc(Player player, QuestDefinition quest, int goalIndex, Npc npc)
    {
        var goal = quest.EffectiveGoals[goalIndex];

        // Spend the scare so one spooking can't be cashed in twice - they have to be spooked again.
        if (!player.ScaredNpcs.Remove(npc.Guid))
            return;

        if (goal.TalkRewardItems.Count > 0 && !player.TalkedQuestNpcs.Contains(npc.Guid))
        {
            int candy = goal.TalkRewardItems[Random.Shared.Next(goal.TalkRewardItems.Count)];
            GrantItem(player, candy);
            player.SendTunneled(new RewardNonBundledItemPacket { ItemDefinitionId = candy, Quantity = 1 });
        }

        CreditCountedTalk(player, quest, goalIndex, npc);
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

        // ★★ TRICK-OR-TREAT IS A TWO-STEP CONVERSATION. Clicking a costumed townsperson does NOT hand over
        // candy - they dare you to scare them first, you /scare, and only the SECOND click pays out. Both
        // halves are real dialogue (see QuestGoal.ScareIntroDialogueIds), and the credit rides the closing
        // button rather than the click, so the player gets their candy when they dismiss the thank-you.
        if (goal.RequiresScare)
        {
            // Already collected from - nothing more to say.
            if (player.TalkedQuestNpcs.Contains(npc.Guid))
                return true;

            if (!player.ScaredNpcs.Contains(npc.Guid))
            {
                // Not spooked yet: they tell you what they want and the conversation ends there.
                ShowScareIntro(player, goal, npc);
                return true;
            }

            // Spooked: the thank-you, with the payout hanging off its button.
            ShowScareThanks(player, quest, goalIndex, goal, npc);
            return true;
        }

        CreditCountedTalk(player, quest, goalIndex, npc);
        return true;
    }

    // The counted-talk credit itself, split out so the trick-or-treat conversation can run it from its
    // closing button instead of from the click that opened the dialog.
    private void CreditCountedTalk(Player player, QuestDefinition quest, int goalIndex, Npc npc)
    {
        var goal = quest.EffectiveGoals[goalIndex];

        // Each NPC counts once: talking to the same Freewheeler three times must not finish the goal.
        // Their line still replays (below) so a re-talk isn't a silent no-op.
        bool alreadyCredited = !player.TalkedQuestNpcs.Add(npc.Guid);

        // ★ THE BADGE COMES OFF HERE - the player is done with this NPC, which is the moment the marker
        // stops meaning anything. It has to be a full RefreshQuestNotification (the badge lives both on the
        // AddNpc and in the notification overlay, so the actor is respawned), and doing it at the END of
        // the conversation rather than at the scare is what keeps that respawn away from the NPC's
        // reaction animation.
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
            return;
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
    }

    // Forgets which of a counted talk goal's NPCs this player has spoken to, so the step starts clean on
    // accept/abandon and can't leak credit into a later re-run of the same quest.
    private static void ClearTalkProgress(Player player, QuestGoal goal)
    {
        foreach (var guid in goal.AllTalkTargetGuids())
        {
            player.TalkedQuestNpcs.Remove(guid);
            player.ScaredNpcs.Remove(guid); // an unspent scare must not survive into a re-run
        }
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

    // Confetti burst that goes off with it - PFX_confetti_red-green_explode_med_short, the red/green
    // Christmas-coloured one, paired with the gold sparkle swirl above for "confetti and sparkles".
    private const int CollectConfettiEffect = 16417;

    // The pickup's own one-shot animation (amb_oneshot_01). Prop models carry this - the present's
    // evnt_winter_holiday_tree_presents_04_loc_amb_oneshot_01.gr2 IS this clip - and it reads as a big
    // bounce. Sent as PlayType 1, the only animation path that works on a non-player entity; see
    // QuestDialogue.SetBaseAnimation for why "play now" is unreachable.
    private const int CollectBounceAnimation = 2001;

    // How long a picked-up collectible is left standing before it's taken away: long enough for the
    // bounce AND the confetti to play out, not just the animation. Every removal path for that quest's
    // pickups waits this out, or the goal completing would yank them mid-bounce.
    private const int CollectBounceMs = 2500;

    // A collectible pickup was clicked. Credits the quest's active Collect goal (one per distinct pickup),
    // hides the pickup for this player, animates the tracker counter, and completes the goal - advancing to
    // the return step - once RequiredCount is reached.
    // A COLLECTION NODE was gathered. Credits any active Collect goal whose CollectNodeType names this
    // node type - the pool-driven alternative to quest-owned CollectSpawns pickups (see QuestGoal).
    //
    // ★ Deliberately NOT keyed on the node's guid the way OnCollectInteract is. A pooled node respawns and
    // its hard point is reused, so "which node was this" is not a stable identity and cannot be used to
    // stop double-credit - the count is simply how many the player has gathered. The node itself is
    // consumed and respawns on the pool's own timer, which is what paces the goal.
    public void OnCollectionNodeGathered(Player player, string nodeTypeKey)
    {
        if (string.IsNullOrWhiteSpace(nodeTypeKey))
            return;

        // ★★ ITERATE A SNAPSHOT. CompleteGoal can finish the quest, which writes player.Quests - mutating
        // the very dictionary being enumerated and throwing "collection was modified" mid-credit. The
        // caller catches that, so the failure is invisible: the node is consumed and the item granted, but
        // the goal never ticks. Take a copy and the credit can safely finish the quest.
        foreach (var (questId, completed) in player.Quests.ToArray())
        {
            if (completed || !_resourceManager.Quests.TryGet(questId, out var quest))
                continue;

            int goalIndex = player.QuestGoalProgress.TryGetValue(questId, out var progress) ? progress : 0;
            var goals = quest.EffectiveGoals;
            if (goalIndex >= goals.Count)
                continue;

            var goal = goals[goalIndex];
            if (goal.Type != QuestGoalType.Collect
                || !string.Equals(goal.CollectNodeType, nodeTypeKey, StringComparison.OrdinalIgnoreCase))
                continue;

            int required = goal.RequiredCount;
            if (required <= 0)
                continue;

            // ★ CLAMP the running count. Progress can be left over from an earlier run of this goal - most
            // sharply when a goal is switched from quest-owned pickups to nodes mid-flight, where a stale
            // count could already sit at or above the target and complete the goal on the first gather.
            int previous = player.QuestCollectProgress.TryGetValue(questId, out var c) ? c : 0;
            int count = Math.Clamp(previous, 0, required - 1) + 1;

            if (count >= required)
            {
                player.QuestCollectProgress.Remove(questId);
                CompleteGoal(player, quest, goalIndex);
                return;
            }

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
            return;
        }
    }

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

        // The pickup takes a big bounce, then confetti and sparkles go off, and only then does it go -
        // all three while the source actor still exists, since effects and animations are addressed by
        // its guid.
        player.SendTunneledToVisible(new PlayerUpdatePacketSetAnimation
        {
            Guid = npc.Guid,
            AnimationId = CollectBounceAnimation,
            PlayType = 1
        }, sendToSelf: true);

        foreach (var effectId in new[] { CollectConfettiEffect, CollectPickupEffect })
        {
            player.SendTunneledToVisible(new PlayerUpdatePacketPlayCompositeEffect
            {
                Guid = npc.Guid,
                CompositeEffectId = effectId,
                Position = npc.Position
            }, sendToSelf: true);
        }

        // Claim it straight away - the delay below is only for the look, and a second click in that window
        // must not credit twice.
        player.CollectedPickups.Add(npc.Guid); // so the marker skips it and points at the next tool

        // Hide it for the collecting player once the bounce has played. Collectibles are shared, so other
        // players still see it; a relog re-adds them all and restarts this goal's (in-memory) count.
        var pickupGuid = npc.Guid;
        _ = Task.Run(async () =>
        {
            await Task.Delay(CollectBounceMs);
            player.SendTunneled(new PlayerUpdatePacketRemovePlayer { Guid = pickupGuid });
        });

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
    // Player harvested something from a world node. Credits the active Gather goal of any in-progress
    // quest that wants that item - the gathering system's counterpart to OnNpcKilled, and the same
    // counter/tracker/persistence path Kill and Collect goals use.
    public void OnItemGathered(Player player, int itemDefinitionId)
    {
        if (itemDefinitionId == 0)
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
            if (goal.Type != QuestGoalType.Gather || goal.GatherItemDefinitionId != itemDefinitionId)
                continue;

            int required = goal.RequiredCount > 0 ? goal.RequiredCount : 1;
            int count = (player.QuestCollectProgress.TryGetValue(questId, out var c) ? c : 0) + 1;

            if (count >= required)
            {
                player.QuestCollectProgress.Remove(questId);
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

                PersistCollectCount(player, questId, count);
                RefreshObjectiveTarget(player);
            }

            return;
        }
    }

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
    // Takes a quest's pickups back off this player's screen - they belong to the quest, so they go when
    // it does (completed, abandoned, or the collect goal ticked off and the tracker moved on).
    private void HideQuestCollectibles(Player player, int questId, int delayMs = 0)
    {
        var guids = _resourceManager.Quests.Collectibles
            .Where(entry => entry.Value.QuestId == questId)
            .Select(entry => entry.Key)
            .ToList();

        if (guids.Count == 0)
            return;

        if (delayMs <= 0)
        {
            foreach (var guid in guids)
                player.SendTunneled(new PlayerUpdatePacketRemovePlayer { Guid = guid });
            return;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(delayMs);
            foreach (var guid in guids)
                player.SendTunneled(new PlayerUpdatePacketRemovePlayer { Guid = guid });
        });
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

        // Just accepted - don't let the client's automatic re-interact immediately offer the next quest.
        SuppressInteractRefire(player, quest.GiverGuid);

        // Finalize the interaction so the offer camera doesn't stay frozen on the giver (sub-opcode 29
        // recomputes the camera + dispatches QuestStartHandler:DismissEndScreen).
        player.SendTunneled(new CommandPacketQuestDialogComplete());
    }

    // The client re-fires FreeInteractionNpc on its own for a moment after a quest dialog closes - it is
    // not a fresh click. Without holding the interact debounce open across that, finishing or accepting a
    // quest instantly re-opens the NPC and the player is handed the NEXT quest they never asked for.
    // Same guard the decline path uses; the window matches the debounce in the interact handlers.
    private static void SuppressInteractRefire(Player player, ulong npcGuid)
    {
        if (npcGuid == 0)
            return;

        player.LastInteractNpcGuid = npcGuid;
        player.LastInteractAt = DateTime.UtcNow;
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

        UpdateCharacterQuest(player, questId, q =>
        {
            q.Completed = true;
            q.CompletedUtc = quest.IsDaily ? DateTimeOffset.UtcNow : null;
        });

        HideQuestCollectibles(player, questId);

        player.SendTunneled(new QuestCompletePacket { QuestId = questId });

        // Bump the journal's lifetime "quests completed" counter (op49/12).
        player.SendTunneled(new CompletedQuestCountUpdatePacket
        {
            Count = player.Quests.Values.Count(done => done)
        });

        // Mark this quest complete in the storybook Adventurer's Journal (op209/2) so its sticker earns.
        SendJournalQuestStates(player);

        GrantReward(player, quest);

        // Just handed in - same guard, or the giver instantly offers whatever comes next in the chain.
        SuppressInteractRefire(player, GoalTargetGuid(quest, quest.EffectiveGoals.Count - 1));

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

        HideQuestCollectibles(player, questId);

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
        ExpireDailyQuests(player);


        foreach (var (questId, completed) in player.Quests)
        {
            // suppressStartBanner: this is a REPLAY of quests the player already accepted, not a fresh
            // accept. Without it the client fires its "Quest Started" toast (FUN_00a92680 ->
            // FUN_00cb7070, 6s each) once per active quest on every login. Same reasoning as the
            // Silent flag on the completed-goal replay below.
            if (!completed && _resourceManager.Quests.TryGet(questId, out var quest))
                SendActiveState(player, quest, suppressStartBanner: true);
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

        // A counted-talk goal marks its whole target list, not just one NPC - trick-or-treat's ten
        // costumed townsfolk each need the badge, and the spotlight that goes with it.
        foreach (var goal in quest.EffectiveGoals)
        {
            if (!goal.IsCountedTalk)
                continue;

            foreach (var targetGuid in goal.AllTalkTargetGuids())
                RefreshQuestNotification(player, targetGuid);
        }

        RefreshScareSpotlights(player);

        foreach (var excludedId in quest.ExcludesQuestIds)
        {
            if (!_resourceManager.Quests.TryGet(excludedId, out var excludedQuest))
                continue;

            RefreshQuestNotification(player, excludedQuest.GiverGuid);
            RefreshQuestNotification(player, excludedQuest.TargetGuid);
        }
    }

    // ★ THERE IS NO CHEAP WAY TO CLEAR A BADGE. Dropping just the notification overlay
    // (PlayerUpdatePacketRemoveNotifications) was tried and is NOT enough - the badge also rides the
    // AddNpc's own NotificationImageSetId, so the spawn-time copy stays on screen. Changing that field on
    // an already-spawned NPC means removing and re-adding the actor, which is what this does. The cost is
    // that the respawn wipes whatever the NPC was doing, so anything the caller wants the NPC to say or
    // play must be sent AFTER this, not before.
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
        // The giver gestures as it makes its pitch, same as it does mid-conversation.
        QuestDialogue.PlayTalkAnimation(player, quest.GiverGuid);

        var questInfoPacket = new QuestInfoPacket
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
            RewardBundle =
            {
                Coins = quest.RewardCoins,
                Experience = quest.RewardExperience, // job XP shown in the reward preview
            }
        };

        // Item icons in the "Show Details" reward preview.
        AddRewardItems(questInfoPacket.RewardBundle, quest);

        player.SendTunneled(questInfoPacket);
    }

    // Resolves a quest's RewardItems def ids into reward-preview entries
    // (icon + name + count) by looking up each item's ClientItemDefinition. Shown as icons in the offer
    // and turn-in "Show Details" panels.
    private void AddRewardItems(RewardBundleBase bundle, QuestDefinition quest)
    {
        // A RewardTable quest with no authored RewardItems lets the table describe its own preview, so
        // the icons and counts shown come from the drops that will actually be rolled instead of a
        // parallel display-only list nothing keeps in step. An authored RewardItems list still wins: it
        // is the literal payout for an ordinary quest, and the deliberate wrapped-gift stand-in for a
        // mystery one (a table can now carry that stand-in itself via PreviewItemDefinitionId).
        if (quest.RewardItems.Count == 0 && !string.IsNullOrWhiteSpace(quest.RewardTable))
        {
            _rewardManager.TryBuildPreview(quest.RewardTable, bundle); // logs its own failures
            return;
        }

        for (int i = 0; i < quest.RewardItems.Count; i++)
        {
            if (!_resourceManager.ClientItemDefinitions.TryGetValue(quest.RewardItems[i], out var itemDef))
                continue;

            bundle.Entries.Add(new RewardBundleEntryItem
            {
                // ClientItemDefinition.Icon.Id, passed through UNCHANGED. This field takes the image-SET
                // id, NOT a flat image id - resolving it through ImageSetMappings broke every reward
                // preview (tried 2026-08-12), so don't "fix" it again.
                IconId = itemDef.Icon.Id,
                NameId = itemDef.NameId,
                // Show what the player will actually get; 1 hides the "xN" label, which is what a
                // quantity-less reward wants anyway.
                Quantity = i < quest.RewardItemQuantities.Count ? Math.Max(1, quest.RewardItemQuantities[i]) : 1
            });
        }
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

        // The finished goal's pickups are no longer this player's business - clear them now rather than
        // leaving them standing until the quest ends (CanSeeQuestCollectible already stops them coming
        // back on the next visibility pass).
        if (goals[goalIndex].Type == QuestGoalType.Collect)
            HideQuestCollectibles(player, quest.QuestId, CollectBounceMs);

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

        // More goals to go: activate the next one and re-point the tracker/breadcrumb at its target.
        //
        // Whether its ROW already exists depends on the quest. By default SendActiveState puts every goal
        // up when the quest is taken, so activating is all that is needed - adding here would duplicate a
        // row. A progressive-reveal quest only has the rows uncovered so far, so this is where the next
        // one appears; it must be added BEFORE activating, or the activate has no row to find (the client
        // matches rows by NameId - see the identity note in SendActiveState).
        if (quest.RevealGoalsProgressively)
        {
            player.SendTunneled(new QuestObjectiveAddedPacket
            {
                QuestId = quest.QuestId,
                ObjectiveNameId = goals[done].NameId,
                ObjectiveDescriptionId = goals[done].NameId,
                ObjectiveField2 = goals[done].DescriptionId != 0 ? goals[done].DescriptionId : goals[done].NameId
            });
        }

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
        // The NPC handing the quest in speaks over the end screen, so it gestures there too.
        QuestDialogue.PlayTalkAnimation(player, GoalTargetGuid(quest, quest.EffectiveGoals.Count - 1));

        var questEndPacket = new QuestEndPacket
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
            RewardBundle =
            {
                Coins = quest.RewardCoins,
                Experience = quest.RewardExperience, // job XP shown in the reward preview
            }
        };

        // Item icons in the "Show Details" reward preview.
        AddRewardItems(questEndPacket.RewardBundle, quest);

        player.SendTunneled(questEndPacket);

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
    private static void SendQuestAdd(Player player, QuestDefinition quest, int helperTextId, float completedPercentage = 0f, bool suppressStartBanner = false)
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
            SystemQuest = false,
            SuppressStartBanner = suppressStartBanner
        });
    }

    // QuestAdd + objective packets that put the quest into the client's journal + tracker.
    private void SendActiveState(Player player, QuestDefinition quest, bool suppressStartBanner = false)
    {
        int alreadyDone = player.QuestGoalProgress.TryGetValue(quest.QuestId, out var p) ? p : 0;
        SendQuestAdd(player, quest, quest.ObjectiveDescriptionId, (float)alreadyDone / quest.EffectiveGoals.Count, suppressStartBanner);

        // FULL CHECKLIST: every goal gets its row up front, so the quest helper shows the whole quest
        // (done, current, and still-to-come) rather than revealing steps one at a time. Reversed from the
        // earlier progressive reveal on user report - a helper that hides the remaining steps reads as if
        // the quest only ever has one objective. CompleteGoal therefore only ACTIVATES the next goal; it
        // no longer adds its row (that would duplicate one of these).
        var goals = quest.EffectiveGoals;
        int done = player.QuestGoalProgress.TryGetValue(quest.QuestId, out var progress) ? progress : 0;

        // ★ PROGRESSIVE REVEAL sends only the goals reached so far (the done ones plus the current), and
        // CompleteGoal adds each next row as it becomes active. On a relog this replays exactly the rows
        // the player had already uncovered, so the list never jumps ahead of their progress.
        int rows = quest.RevealGoalsProgressively ? Math.Min(done + 1, goals.Count) : goals.Count;

        for (int i = 0; i < rows; i++)
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
            // A node-backed collect goal has no quest-owned pickups to point at - aim at the nearest LIVE
            // node of its type instead. Which ones exist changes as the pool cycles them, so this is
            // resolved from the zone each time rather than from a fixed list.
            if (!string.IsNullOrWhiteSpace(goals[goalIndex].CollectNodeType))
            {
                var node = NearestCollectionNode(player, goals[goalIndex].CollectNodeType);
                if (node is not null)
                    return node.Guid;
            }

            var nearest = NearestUncollectedPickup(player, quest.QuestId, goalIndex);
            if (nearest is not null)
                return nearest.Guid;
        }

        return GoalTargetGuid(quest, goalIndex);
    }

    // Nearest Collect pickup for (questId, goalIndex) that this player hasn't gathered yet, or null when
    // none remain in this zone. Pickups are the collectible NPCs spawned from the goal's CollectSpawns.
    // The nearest LIVE collection node of a given type. Pool-driven nodes come and go, so this walks the
    // player's visible entities rather than any static spawn list.
    private static Npc? NearestCollectionNode(Player player, string nodeTypeKey)
    {
        Npc? nearest = null;
        var best = float.MaxValue;

        foreach (var npc in player.VisibleNpcs.Values)
        {
            if (npc is not CollectionNode node
                || !string.Equals(node.TypeDefinition.Key, nodeTypeKey, StringComparison.OrdinalIgnoreCase))
                continue;

            var dx = node.Position.X - player.Position.X;
            var dz = node.Position.Z - player.Position.Z;
            var d2 = dx * dx + dz * dz;
            if (d2 < best)
            {
                best = d2;
                nearest = node;
            }
        }

        return nearest;
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

        // ★ SOME QUESTS HAVE NOWHERE TO POINT. A quest whose targets are scattered over the whole world -
        // trick-or-treat, whose costumed townsfolk stand in every district - gets no arrow, no green trail
        // and no auto-walk, because picking one of them would be arbitrary. Retail simply omits "Take Me
        // There" for those, so clear any stale target and send nothing.
        if (quest.SuppressTakeMeThere)
        {
            player.SendTunneled(new ObjectiveTargetUpdatePacket { Active = false });
            return;
        }

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
            player.SendTunneled(new RewardBundlePacket { RewardBundle = { Coins = coins, Experience = experience } });

        // WEIGHTED mystery gift: roll the quest's reward table and let the reward manager grant it. The
        // table decides both what drops and how likely it is, so a rare sweater can sit alongside common
        // cookies - unlike RandomRewardItems below, which is a flat pick. The reward manager sends its own
        // grant banner (50/1), so this path skips the 50/2 celebration.
        if (!string.IsNullOrWhiteSpace(quest.RewardTable))
        {
            // Both calls log their own failures.
            if (_rewardManager.TryRollReward(quest.RewardTable, out var drop) && drop is not null)
                _rewardManager.TryGrantReward(player, drop);

            return;
        }

        // Item rewards - defined per quest in Resources/Quests.json ("RewardItems": [id, ...]).
        // A quest with a RandomRewardItems pool pays out ONE item drawn from it instead; its RewardItems
        // stay in the preview only, so the player is shown the wrapped gift and finds out what's inside
        // on completion (retail's Holiday Mystery Gift).
        IReadOnlyList<int> granted = quest.RandomRewardItems.Count > 0
            ? new[] { quest.RandomRewardItems[Random.Shared.Next(quest.RandomRewardItems.Count)] }
            : quest.RewardItems;

        for (int i = 0; i < granted.Count; i++)
        {
            int itemDefinitionId = granted[i];

            // Quantities are index-aligned with RewardItems; short/empty means one of each, so the common
            // single-item reward needs no extra data. A random pool always pays out one.
            int quantity = quest.RandomRewardItems.Count > 0 || i >= quest.RewardItemQuantities.Count
                ? 1
                : Math.Max(1, quest.RewardItemQuantities[i]);

            GrantItem(player, itemDefinitionId, quantity);

            // "You earned an item" celebration (opcode 50/2): the icon + how many actually arrived.
            player.SendTunneled(new RewardNonBundledItemPacket { ItemDefinitionId = itemDefinitionId, Quantity = quantity });
        }
    }

    // Grants one of definitionId to the player: stacks it in the DB (by definition +
    // tint), mirrors it into the in-memory inventory, and tells the client (ItemAdd for a new item, or
    // ItemUpdate for an incremented stack). Mirrors the coin-store grant path.
    public void GrantItem(Player player, int definitionId, int quantity = 1)
    {
        if (quantity < 1)
            return;

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
                row.Item.Count += quantity;
                itemId = row.Item.Id;
                count = row.Item.Count;
            }
            else
            {
                var dbItem = new DbItem { Id = row.NextId + 1, Definition = definitionId, Tint = tint, Count = quantity };
                row.Character.Items.Add(dbItem);
                itemId = dbItem.Id;
                count = quantity;
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
