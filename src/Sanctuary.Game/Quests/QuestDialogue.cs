using System;
using System.Collections.Generic;
using System.Threading.Tasks;

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

    // The NPC gestures while it speaks instead of standing frozen behind its own speech bubble.
    // emo_talk_neutral_med, live-confirmed present in NPC animation sets (emo_talk_* also carries
    // inquire/exclaim 3102/3103 and neutral/happy/angry in short/med/long 3104-3112, if lines ever
    // gain a mood). NOTE the amb_* family, which looked like the obvious NPC choice, does NOT play.
    public const int TalkAnimationId = 3105;

    // StopDancing's idle id - the proven way to put an entity back to its default animation.
    private const int IdleAnimationId = 1;

    // ★ This MUST be PlayType 1. The op35/8 handler (0x009315C7) forks on bit0 of PlayType:
    //   bit0 set   -> writes the entity's BASE animation at [entity+0x51C] directly. No gate.
    //   bit0 clear -> "play now", which calls 0x0096C780 - and that returns immediately unless
    //                 [entity+0x1870] is non-null, which it never is for an NPC. Live-confirmed:
    //                 PlayType 2 does nothing on NPCs no matter the animation id.
    // Because it sets a BASE animation it LOOPS until replaced, so every path that ends a conversation
    // has to call StopTalkAnimation or the NPC gestures at nobody forever.
    private const byte SetBaseAnimation = 1;

    // How long the gesture is left running before it's reset to idle - i.e. how we fake a single play
    // out of a looping base animation. An ESTIMATE of emo_talk_neutral_med's real length: too long and
    // the clip starts over, too short and it cuts off. Tune against the client;
    // emo_talk_neutral_short (3104) / _long (3106) are the shorter and longer clips if the pacing is off.
    private const int TalkAnimationMs = 1500;

    // Plays the talking gesture ONCE on a speaking NPC. Sent only to the player in the conversation: the
    // bubble is theirs alone, and two players talking to the same NPC would otherwise fight over its
    // animation.
    //
    // "Once" has to be emulated. The client's real one-shot ("play now") path is unreachable on NPCs -
    // see SetBaseAnimation - leaving only the base-animation write, which LOOPS. So the gesture is
    // started and then reset to idle a clip-length later.
    public static void PlayTalkAnimation(Player player, ulong npcGuid)
    {
        if (npcGuid == 0)
            return;

        // Whoever was talking before stops first, so a conversation that hands off between NPCs (or an
        // offer interrupted by another) can't strand the previous one mid-gesture.
        if (player.TalkingNpcGuid != 0 && player.TalkingNpcGuid != npcGuid)
            StopTalkAnimation(player);

        player.TalkingNpcGuid = npcGuid;

        // Every start and stop takes a ticket. The delayed reset below only fires if it still holds the
        // current one, so the next line of dialogue - or the player closing the conversation - cleanly
        // supersedes a reset that hasn't come due yet instead of cutting the new gesture short.
        int ticket = ++player.TalkAnimationTicket;

        player.SendTunneled(new PlayerUpdatePacketSetAnimation
        {
            Guid = npcGuid,
            AnimationId = TalkAnimationId,
            PlayType = SetBaseAnimation
        });

        _ = Task.Run(async () =>
        {
            await Task.Delay(TalkAnimationMs);

            if (player.TalkAnimationTicket == ticket)
                StopTalkAnimation(player);
        });
    }

    // An NPC reacts to being scared: emo_afraid, the client's own fright emote. Played the same
    // base-animation way as the talking loop (PlayType 1 - see SetBaseAnimation) and cleared by the same
    // ticket/reset machinery, so a spooked NPC settles back to idle on its own.
    private const int AfraidAnimationId = 3339;

    // ★ THE REACTION IS BOTH A JUMP AND A YELP. Retail's two scare exclamations are 416769 "Ahh!" and
    // 416770 "Scary!" - they sit together in the trick-or-treat block, immediately before the Ghost
    // Hunter's own strings, which is what identifies them as this event's reaction lines rather than
    // generic chatter. Spoken as a bubble with IsChatLogged FALSE, the retail no-chat-log path (see
    // project_npc_chat_bubble) - a townsperson yelping should not fill up the chat window.
    private static readonly int[] ScareExclamationIds = [416769, 416770];

    public static void PlayScareReaction(Player player, ulong npcGuid)
    {
        if (npcGuid == 0)
            return;

        player.TalkingNpcGuid = npcGuid;
        int ticket = ++player.TalkAnimationTicket;

        player.SendTunneled(new PlayerUpdatePacketSetAnimation
        {
            Guid = npcGuid,
            AnimationId = AfraidAnimationId,
            PlayType = SetBaseAnimation
        });

        // ★★ OwnerGuid IS WHAT MAKES IT DRAW; HasColor IS WHAT MAKES IT AN ANNOUNCEMENT. Both were added
        // at once when the line wasn't appearing, which showed the bubble but also put a copy on screen -
        // so the two were separated by dropping the colour, and the bubble survived on its own. That
        // matches StartingZone.SnowmenInvaders' own note: a COLOURED line is treated as an announcement,
        // a plain one is just speech. The wave announcement there sets both because it WANTS the on-screen
        // copy; an ambient yelp does not.
        //
        // So: OwnerGuid alongside SpeakerGuid (the client drops a line whose speaker it cannot resolve),
        // no colour, and IsChatLogged=false - which leaves the bubble over their head and nothing else.
        player.SendTunneled(new ChatPacketFromStringId
        {
            SpeakerGuid = npcGuid,
            OwnerGuid = npcGuid,
            StringId = ScareExclamationIds[Random.Shared.Next(ScareExclamationIds.Length)],
            IsChatLogged = false,
        });

        _ = Task.Run(async () =>
        {
            await Task.Delay(TalkAnimationMs);
            if (player.TalkAnimationTicket == ticket)
                StopTalkAnimation(player);
        });
    }

    // Puts the NPC this player had talking back to its normal idle. Safe to call at any time - it's a
    // no-op when nobody is mid-sentence, so exit paths can call it unconditionally.
    public static void StopTalkAnimation(Player player)
    {
        if (player.TalkingNpcGuid == 0)
            return;

        var npcGuid = player.TalkingNpcGuid;
        player.TalkingNpcGuid = 0;
        player.TalkAnimationTicket++; // invalidate any reset still pending for this gesture

        player.SendTunneled(new PlayerUpdatePacketSetAnimation
        {
            Guid = npcGuid,
            AnimationId = IdleAnimationId,
            PlayType = SetBaseAnimation
        });
    }

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
            StopTalkAnimation(player); // conversation is over - the caller sends EndDialog next
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
        StopTalkAnimation(player);
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

        // Per turn, not per conversation - the NPC re-gestures each time it takes the floor again.
        PlayTalkAnimation(player, npcGuid);

        player.SendTunneled(dialog);
    }
}
