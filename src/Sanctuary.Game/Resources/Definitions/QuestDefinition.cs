using System.Collections.Generic;

namespace Sanctuary.Game.Resources.Definitions;

public class QuestDefinition
{
    public int QuestId { get; set; }

    public int TitleId { get; set; }
    public int DescriptionId { get; set; }
    public int GiverDialogueId { get; set; }
    public int ObjectiveDescriptionId { get; set; }
    public int SubGoalId { get; set; }
    public int TargetDialogueId { get; set; }
    public int IconId { get; set; }

    public List<QuestGoal> Goals { get; set; } = new();

    public ulong GiverGuid { get; set; }
    public ulong TargetGuid { get; set; }

    public int RewardCoins { get; set; }

    public int RewardExperience { get; set; }

    public List<int> RewardItems { get; set; } = new();

    public int PrerequisiteQuestId { get; set; }
    public int NextQuestId { get; set; }

    public List<int> ExcludesQuestIds { get; set; } = new();

    public int NotificationAvailable { get; set; } = 2;
    public int NotificationActive { get; set; } = 6;

    public IReadOnlyList<QuestGoal> EffectiveGoals =>
        Goals.Count > 0
            ? Goals
            : new[]
            {
                new QuestGoal
                {
                    NameId = SubGoalId != 0 ? SubGoalId : ObjectiveDescriptionId,
                    DescriptionId = ObjectiveDescriptionId,
                    DialogueId = TargetDialogueId,
                    Type = QuestGoalType.TalkToNpc,
                    TargetGuid = TargetGuid,
                }
            };

    public int TurnInDialogueId
    {
        get
        {
            var goals = EffectiveGoals;
            var last = goals[goals.Count - 1];
            return last.DialogueId != 0 ? last.DialogueId : TargetDialogueId;
        }
    }

    public bool IsOfferableFor(IReadOnlyDictionary<int, bool> playerQuests)
    {
        if (playerQuests.ContainsKey(QuestId))
            return false;

        if (PrerequisiteQuestId != 0)
            return playerQuests.TryGetValue(PrerequisiteQuestId, out var prerequisiteDone) && prerequisiteDone;

        foreach (var excludedId in ExcludesQuestIds)
            if (playerQuests.ContainsKey(excludedId))
                return false;

        return true;
    }
}
