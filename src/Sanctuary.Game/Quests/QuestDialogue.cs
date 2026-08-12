using System.Collections.Generic;

using Sanctuary.Game.Entities;
using Sanctuary.Game.Resources.Definitions;
using Sanctuary.Packet;

namespace Sanctuary.Game.Quests;

// Plays a quest NPC's spoken conversation through the stock dialog UI (CommandPacketShowDialog, 26/3):
// a speech bubble with the NPC's line and a single response button captioned with the player's reply.
// HTML-rendered (colored <font> tags show), NO details box, NO journal touch (so no duplicate row).
// Camera focuses the NPC (CameraFocusParam) and restores when the conversation ends.
//
// A conversation can be several turns (NPC speaks, player replies, NPC speaks again). The client reports
// each button click as 26/6, which BaseCommandPacketHandler routes to TryAdvance: while turns remain we
// send the next bubble, and only when they run out do we send the EndDialog teardown that frees the
// dialog and restores the camera. Retail exchanges like Emuzz's two-parter need exactly this.
public static class QuestDialogue
{
    // Global.Text id for the generic "You got it!" response button - the caption used when a line
    // doesn't author the player's own reply.
    private const int YouGotItTextId = 103085;

    // The response button's icon says whether the reply CONTINUES the exchange or closes it - matched to
    // retail screenshots of Feminine Touch: Emuzz's "Yancy Gilbert sent me." (an answer still to come)
    // carries the plus, while "Great, Thanks!" and Tazinna's "Yum! Ok thanks!" - both the last word in
    // their conversation - carry the curved leave arrow. Image ids from the client's Images.txt; the two
    // are a matched pair, same orange palette.
    // NB: the `ui_dialog_*up`/`*roll` arrows (backup/okup, ids 320-322) are NOT these - extracting them
    // from the asset packs shows they're near-fully-transparent button shadow states, a dozen opaque
    // pixels each, not artwork.
    private const int PlusImageId = 303;   // ui_dialog_plus - this reply has more coming after it
    private const int LeaveImageId = 4008; // ui_dialog_leave - this reply ends the conversation

    // ImageSet id 17 = "dialog green button" (ImageSets.txt) - the response-button skin, green in retail
    // for both icons.
    private const int GreenButtonImageSet = 17;

    // Starts a conversation at its first turn and parks the rest on the player for TryAdvance to play out.
    // A single-turn conversation behaves exactly as the old one-shot bubble did.
    public static void Begin(Player player, IReadOnlyList<QuestDialogueLine> lines, ulong npcGuid)
    {
        player.PendingDialogue.Clear();

        if (lines.Count == 0)
            return;

        player.PendingDialogueNpcGuid = npcGuid;
        for (int i = 1; i < lines.Count; i++)
            player.PendingDialogue.Enqueue(lines[i]);

        Show(player, lines[0], npcGuid, isLastTurn: lines.Count == 1);
    }

    // The player clicked the response button. Plays the next turn if the conversation has one and reports
    // true, so the caller knows to hold back the EndDialog teardown until the exchange is actually over.
    public static bool TryAdvance(Player player)
    {
        if (player.PendingDialogue.Count == 0)
        {
            player.PendingDialogueNpcGuid = 0;
            return false;
        }

        var line = player.PendingDialogue.Dequeue();
        Show(player, line, player.PendingDialogueNpcGuid, isLastTurn: player.PendingDialogue.Count == 0);
        return true;
    }

    // Drops any conversation still queued - used when something else takes over the dialog (abandoning the
    // quest, a new conversation starting) so a stale turn can't surface in the middle of it.
    public static void Clear(Player player)
    {
        player.PendingDialogue.Clear();
        player.PendingDialogueNpcGuid = 0;
    }

    private static void Show(Player player, QuestDialogueLine line, ulong npcGuid, bool isLastTurn)
    {
        var dialog = new CommandPacketShowDialog
        {
            DialogueTextId = line.TextId,
            NpcGuid = npcGuid,
            CameraFocusParam = 1f,
        };

        dialog.Responses.Add(new CommandPacketShowDialog.Response
        {
            Id = 1,
            // The player's own line when the quest authors one ("Yancy Gilbert sent me."), else "You got it!".
            LabelTextId = line.ResponseTextId != 0 ? line.ResponseTextId : YouGotItTextId,
            // node+0x14 -> button icon: leave arrow when this click closes the conversation, plus when the
            // NPC still has an answer to give.
            Param1 = isLastTurn ? LeaveImageId : PlusImageId,
            Param2 = GreenButtonImageSet, // node+0x18 -> button skin = "dialog green button" imageSet
        });

        player.SendTunneled(dialog);
    }
}
