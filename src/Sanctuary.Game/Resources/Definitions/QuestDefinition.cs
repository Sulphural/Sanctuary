using System.Collections.Generic;

namespace Sanctuary.Game.Resources.Definitions;

public sealed class QuestDefinition
{
    public int QuestId { get; set; }

    public int TitleId { get; set; }
    public int DescriptionId { get; set; }
    public int GiverDialogueId { get; set; }

    public int ObjectiveDescriptionId { get; set; }

    public int IconId { get; set; }

    public List<QuestGoal> Goals { get; set; } = [];

    public ulong GiverGuid { get; set; }

    public ulong TargetGuid { get; set; }

    public int RewardCoins { get; set; }

    public int RewardExperience { get; set; }

    public List<int> RewardItems { get; set; } = [];

    public int RewardCollectionId { get; set; }

    public int PrerequisiteQuestId { get; set; }
    public int NextQuestId { get; set; }

    public List<int> ExcludesQuestIds { get; set; } = [];

    public int NotificationAvailable { get; set; } = 2;
    public int NotificationActive { get; set; } = 6;

    public int TurnInDialogueId => Goals.Count > 0 ? Goals[^1].DialogueId : 0;

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
