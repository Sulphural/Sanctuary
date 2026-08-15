using System.Collections.Generic;

namespace Sanctuary.Game.Resources.Definitions;

// How a QuestGoal is completed. Drives which server event ticks the goal off.
// TalkToNpc, Collect and Kill are wired; ReachLocation is still a placeholder.
public enum QuestGoalType
{
    // Completes when the player interacts with TargetGuid.
    TalkToNpc = 0,

    // Completes when the player comes within ReachRadius of ReachPosition
    // (2D X/Z check, evaluated on every client position update).
    ReachLocation = 1,

    // Completes when the player has gathered RequiredCount pickups.
    Collect = 2,

    // Completes when the player has defeated RequiredCount NPCs whose
    // NameId matches KillNpcNameId (kills credit via QuestManager.OnNpcKilled).
    Kill = 3,

    // Completes when the player WINS the battle-instance encounter whose activity id matches
    // EncounterId (credited via QuestManager.OnEncounterComplete when the arena
    // win fires). This is how a dungeon/encounter becomes a quest objective.
    EncounterComplete = 4,

    // Completes when the player uses a particular EMOTE while standing within ReachRadius of
    // ReachPosition - retail's "stand near the Gifting Tree and /cheer". Emotes reach the server as
    // QuickChat (see QuestManager.OnQuickChatEmote), so the emote is identified by its quick-chat id.
    UseEmote = 5,

    // Completes when the player HARVESTS RequiredCount of GatherItemDefinitionId from world nodes -
    // the gathering system's counterpart to Kill, credited from GatheringManager via OnItemGathered.
    Gather = 6,
}

// One turn of an NPC conversation: the NPC speaks, and the player's reply is the caption on the
// dialog's single response button. Clicking it advances to the next turn, or closes the dialog on the
// last one - which is how retail plays out exchanges like Emuzz's ("...what are you trying to steal
// from me?" -> "Yancy Gilbert sent me." -> "Oh, well that is different entirely...").
public class QuestDialogueLine
{
    // What the NPC says (Global.Text id), rendered as the bubble body.
    public int TextId { get; set; }

    // The player's reply, used as the response button's caption. 0 = the generic "You got it!".
    public int ResponseTextId { get; set; }
}

// One goal (checklist row) within a quest. Each goal becomes a client objective row
// (QuestObjectiveAddedPacket) shown in the quest tracker with a status icon that ticks off when the
// goal's trigger fires (QuestObjectiveCompletePacket). Goals complete in order; the active goal is
// the first one not yet completed, and the quest is ready to hand in once every goal is done.
public class QuestGoal
{
    // Localized text id for the goal row shown in the tracker/journal ("Talk to Shakey").
    public int NameId { get; set; }

    // Optional longer description id shown as the journal "Objectives" sub-line under the goal row
    // ("Shakey should be hanging out in front of the Wildwood Speedway..."); 0 = reuse NameId.
    public int DescriptionId { get; set; }

    // What the goal's NPC says when this goal is completed at them. Currently only shown for the
    // FINAL goal: it becomes the turn-in end screen's speech bubble (so a quest that ends back at
    // the giver shows the giver's closing line, not the intermediate NPC's). 0 = fall back to the
    // quest's TargetDialogueId.
    public int DialogueId { get; set; }

    // A MULTI-TURN version of DialogueId: the back-and-forth the goal's NPC plays instead of a single
    // bubble (NPC line -> player reply -> NPC line -> ...). Overrides DialogueId when non-empty; a
    // one-entry list is just DialogueId with a custom reply caption.
    public List<QuestDialogueLine> Dialogue { get; set; } = new();

    // For a counted TalkToNpc goal: OPTIONAL player reply captions, index-aligned with TargetDialogueIds
    // (and so with the goal's target guids). 0 or missing = the generic "You got it!".
    public List<int> TargetResponseIds { get; set; } = new();

    // How this goal completes.
    public QuestGoalType Type { get; set; } = QuestGoalType.TalkToNpc;

    // For TalkToNpc: the NPC guid the player must interact with to
    // complete this goal. 0 falls back to the quest's TargetGuid (the turn-in NPC).
    public ulong TargetGuid { get; set; }

    // For TalkToNpc: OPTIONAL additional NPC guids that also credit this goal — for a COUNTED talk step
    // where several interchangeable NPCs share ONE tracker row ("Talk to Freewheelers - 0/3") rather than
    // getting a row each. Combined with TargetGuid; set RequiredCount to how many must be talked to.
    // Retail authors these as a single plural goal string, so they can't be split into one goal per NPC -
    // there's only one NameId to give the rows.
    public List<ulong> TargetGuids { get; set; } = new();

    // For a counted TalkToNpc goal: OPTIONAL per-NPC reply lines, index-aligned with the goal's target
    // guids in AllTalkTargetGuids() order (TargetGuid first, then TargetGuids). Lets each of the three
    // Freewheelers speak their own line instead of all sharing DialogueId. Short/empty = fall back to
    // DialogueId for the targets it doesn't cover.
    public List<int> TargetDialogueIds { get; set; } = new();

    // Counted TalkToNpc only: the NPC must have been SCARED before talking will credit it - retail's
    // Trick-or-Treat, where nobody hands out candy until you /scare them. The client sends emotes as
    // QuickChat (EmoteHandler binds every one to Ui.ProcessQuickChatCommand), so /scare arrives as
    // QuickChat id 219 and QuestManager.OnQuickChatEmote records it against nearby targets.
    public bool RequiresScare { get; set; }

    // For count goals (Collect/Kill, and a counted TalkToNpc): how many
    // of the thing are required. 0 falls back to CollectSpawns.Count (collect them all).
    // The tracker renders "current/required" as the player collects.
    public int RequiredCount { get; set; }

    // For Collect: the model (Models.txt id) each collectible world object
    // uses - e.g. 93 = bw_collectible_mushrooms_01. Spawned as interactable pickups the player clicks.
    public int CollectModelId { get; set; }

    // For Collect: the collectible's hover/name text id (Global.Text).
    public int CollectNameId { get; set; }

    // For Kill: the NameId of the NPCs this goal counts (e.g. 76190
    // "Tormented Spirit"). Any world NPC with this NameId is made hostile/damageable at spawn, and
    // each kill credits the goal until RequiredCount is reached.
    public int KillNpcNameId { get; set; }

    // For Kill: OPTIONAL additional NameIds that also credit this goal — for hunts where several
    // NPC variants share a camp (Bixie Skirmish counts Soldiers, Guardians, and Magi alike). Combined
    // with KillNpcNameId; every listed NameId is also made hostile/damageable at spawn.
    public List<int> KillNpcNameIds { get; set; } = new();

    // All NameIds this Kill goal credits (the single id + the list, whichever are set).
    public IEnumerable<int> AllKillNameIds()
    {
        if (KillNpcNameId != 0)
            yield return KillNpcNameId;
        foreach (var id in KillNpcNameIds)
            if (id != 0 && id != KillNpcNameId)
                yield return id;
    }

    // Every NPC guid that credits this TalkToNpc goal (the single guid + the list), in the order the
    // per-target dialogue ids are aligned to.
    public IEnumerable<ulong> AllTalkTargetGuids()
    {
        if (TargetGuid != 0)
            yield return TargetGuid;
        foreach (var guid in TargetGuids)
            if (guid != 0 && guid != TargetGuid)
                yield return guid;
    }

    // Whether this goal is a counted talk step ("Talk to Freewheelers - 0/3") rather than a plain
    // talk-to-this-one-NPC goal: a single row that several NPCs tick up.
    public bool IsCountedTalk => Type == QuestGoalType.TalkToNpc && RequiredCount > 1;

    // The conversation the given NPC plays when this goal completes at them: the authored multi-turn
    // Dialogue if there is one, otherwise the single line that NPC owns (their own entry in
    // TargetDialogueIds on a counted talk goal, else the goal's shared DialogueId). Empty = say nothing.
    public IReadOnlyList<QuestDialogueLine> ConversationFor(ulong npcGuid)
    {
        if (Dialogue.Count > 0)
            return Dialogue;

        int index = 0;
        foreach (var guid in AllTalkTargetGuids())
        {
            if (guid == npcGuid && index < TargetDialogueIds.Count && TargetDialogueIds[index] != 0)
                return [new QuestDialogueLine
                {
                    TextId = TargetDialogueIds[index],
                    ResponseTextId = index < TargetResponseIds.Count ? TargetResponseIds[index] : 0,
                }];
            index++;
        }

        return DialogueId != 0 ? [new QuestDialogueLine { TextId = DialogueId }] : [];
    }

    // For EncounterComplete: the activity/encounter id (e.g. 174 =
    // Frostfang Growler arena) that completes this goal when the player wins it.
    public int EncounterId { get; set; }

    // For Collect: world positions ([x, y, z] each) where the collectible
    // pickups spawn. Interacting with one credits the goal; at RequiredCount the goal ticks
    // off and the next goal (the "return" step) activates. Place at least RequiredCount.
    public List<float[]> CollectSpawns { get; set; } = new();

    // For ReachLocation: the world position ([x, y, z]) the player must get near. The check is 2D
    // (X/Z), so the Y only feeds the map pin.
    public float[] ReachPosition { get; set; } = [];

    // For ReachLocation: how close (world units) counts as "arrived". 0 -> default 12.
    public float ReachRadius { get; set; }

    // For UseEmote: the quick-chat id of the emote that satisfies this goal. From the client's
    // EmoteHandler table - /cheer is 145, /scare 219, /wave 143, /point 139.
    public int EmoteQuickChatId { get; set; }

    // For Gather: the item definition harvested from a node that credits this goal.
    public int GatherItemDefinitionId { get; set; }
}
