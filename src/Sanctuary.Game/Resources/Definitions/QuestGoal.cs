using System.Collections.Generic;

namespace Sanctuary.Game.Resources.Definitions;

public enum QuestGoalType
{
    TalkToNpc = 0,
    ReachLocation = 1,
    Collect = 2,
}

public class QuestDialogueLine
{
    public int TextId { get; set; }

    public int ResponseTextId { get; set; }
}

public class QuestGoal
{
    public int NameId { get; set; }

    public int DescriptionId { get; set; }

    public int DialogueId { get; set; }

    public List<QuestDialogueLine> Dialogue { get; set; } = new();

    public QuestGoalType Type { get; set; } = QuestGoalType.TalkToNpc;

    public ulong TargetGuid { get; set; }

    public List<ulong> TargetGuids { get; set; } = new();

    public List<int> TargetDialogueIds { get; set; } = new();

    public List<int> TargetResponseIds { get; set; } = new();

    public int RequiredCount { get; set; }

    public int CollectModelId { get; set; }

    public int CollectNameId { get; set; }

    public List<float[]> CollectSpawns { get; set; } = new();

    public float[] ReachPosition { get; set; } = [];

    public float ReachRadius { get; set; }

    public IEnumerable<ulong> AllTalkTargetGuids()
    {
        if (TargetGuid != 0)
            yield return TargetGuid;

        foreach (var guid in TargetGuids)
            if (guid != 0 && guid != TargetGuid)
                yield return guid;
    }

    public bool IsCountedTalk => Type == QuestGoalType.TalkToNpc && RequiredCount > 1;

    public IReadOnlyList<QuestDialogueLine> ConversationFor(ulong npcGuid)
    {
        if (Dialogue.Count > 0)
            return Dialogue;

        var index = 0;
        foreach (var guid in AllTalkTargetGuids())
        {
            if (guid == npcGuid && index < TargetDialogueIds.Count && TargetDialogueIds[index] != 0)
            {
                return [new QuestDialogueLine
                {
                    TextId = TargetDialogueIds[index],
                    ResponseTextId = index < TargetResponseIds.Count ? TargetResponseIds[index] : 0,
                }];
            }

            index++;
        }

        return DialogueId != 0 ? [new QuestDialogueLine { TextId = DialogueId }] : [];
    }
}
