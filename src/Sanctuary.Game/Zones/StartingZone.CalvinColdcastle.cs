using System;
using System.Collections.Generic;
using System.Numerics;

using Microsoft.Extensions.Logging;

using Sanctuary.Game.Entities;
using Sanctuary.Packet;

namespace Sanctuary.Game.Zones;

// CALVIN COLDCASTLE - the Snowhill NPC who sets up a Snowball Fight.
//
// He is retail's own match-maker for the minigame, not an invention: the 12 Days quest chain sends you to
// him ("Trina Turtledove of Snowhill wants you to participate in a snowball fight by speaking to Calvin
// Coldcastle", 441083) and his own line names the venue - "Snowball fights are so much fun!<BR><BR>You
// should play too! I'll even setup a match for you, on the official Snowhill Snowball Fighting Field!"
// (420408). Talking to him is how retail got you into SnowballArenaZone.
//
// FIRST SLICE: clicking him drops you straight into the arena. Retail put its Quick Play / Invite Friends
// lobby in between (string 419546) - that is the GAQ encounter-invite system, and wiring it up is the next
// step; until then this is the same one-click entry `/snowball arena` gives, just on the right NPC.
public sealed partial class StartingZone
{
    private const int CalvinNameId = 420397;   // "Calvin Coldcastle"

    // ★ NOT a Calvin-specific model - the client ships none (no model in Models.txt carries his name, and
    // there is no dedicated .adr in the packs or the manifest). 837 human_m_snowhill.adr is the purpose-
    // built Snowhill townsperson: race 1 (human), and its texture set (dog-ear hat, goggles) is winter
    // clothing, so he reads as a local rather than as a generic import. Confirmed present in
    // Assets_manifest.txt as human_m_snowhill.adr.z. If his real appearance ever turns up on ZAM, this is
    // the one line to change.
    private const int CalvinModelId = 837;

    // The snowball-fight head badge, so he reads as "there's a minigame here" from across the village.
    // NotificationImages entry 251 = the context bubble around icon 26947, the 64px sibling of the
    // icon_event_snowball_fights art - the SAME badge the Snow Days snowball piles wear (see
    // SnowmenInvaders.SnowballPileBadgeImageId), which is what keeps the two reading as one feature.
    private const int CalvinBadgeImageId = 251;

    // His own retail line, and the two buttons under it - all three ids are the real strings, matched
    // against a screenshot of retail's own panel:
    //   420408 "Snowball fights are so much fun!<BR><BR>You should play too! I'll even setup a match for
    //           you, on the official Snowhill Snowball Fighting Field!"
    //   416239 "Ok, lets do this!"      2003 "No thanks!"
    // This goes out as CommandPacketShowDialog (26/3) - the speech bubble with response buttons - not as a
    // HUD message: the text carries <BR> markup, which that dialog renders and a chat bubble would not.
    private const int CalvinGreetingId = 420408;
    private const int CalvinAcceptTextId = 416239;
    private const int CalvinDeclineTextId = 2003;

    // Button dressing, the same pair QuestDialogue uses, and it matches the screenshot exactly: the accept
    // button is the green skin with the "+" icon, the decline button keeps the default skin and the leave
    // arrow that marks a reply as ending the conversation.
    private const int DialogPlusImageId = 303;    // ui_dialog_plus
    private const int DialogLeaveArrowImageId = 4008; // ui_dialog_leave
    private const int DialogGreenButtonSet = 17;  // ImageSets.txt "dialog green button"

    // ★ THE DECLINE BUTTON NEEDS A REAL SET ID - 0 IS NOT "the default skin". Passing 0 drew imageset 0,
    // which is a completely unrelated texture bleeding through the button (live 2026-08-15). ImageSets.txt
    // has one dialog button per colour: 16 yellow, 17 green, 18 beige, 30 blue, 34 lightblue, 36 red,
    // 37 purple, 38 orange. Retail's "No thanks!" is the BEIGE one.
    private const int DialogBeigeButtonSet = 18;  // ImageSets.txt "dialog beige button"

    private const int CalvinAcceptResponseId = 1;
    private const int CalvinDeclineResponseId = 2;

    // Measured in game (!pos): X=105.16 Y=22.00 Z=390.03, heading 56 degrees.
    private static readonly Vector4 CalvinPosition = new(105.16f, 22.00f, 390.03f, 1f);
    private const float CalvinHeading = 56f * MathF.PI / 180f;

    private Npc? _calvinColdcastle;

    // Unlike Bruce, Calvin is PERMANENT - he is a quest target and a minigame entrance, so he has to be
    // standing there whenever someone comes looking. Called from OnStart.
    private void SpawnCalvinColdcastle()
    {
        if (!TryCreateNpc(out var calvin))
            return;

        calvin.ModelId = CalvinModelId;
        calvin.NameId = CalvinNameId;
        calvin.Name = "Calvin Coldcastle";
        calvin.Static = true;
        calvin.Visible = true;
        calvin.Scale = _resourceManager.Models.TryGetValue(CalvinModelId, out var model) && model.Scale != 0f
            ? model.Scale
            : 1f;

        calvin.NotificationImageSetId = CalvinBadgeImageId;
        calvin.CursorId = 17; // hand cursor - he's clickable
        calvin.InteractAction = OfferSnowballMatch;

        var rotation = new Quaternion(MathF.Sin(CalvinHeading), 0f, MathF.Cos(CalvinHeading), 0f);
        calvin.UpdatePosition(CalvinPosition, rotation);
        GetTileFromPosition(CalvinPosition).Entities.TryAdd(calvin.Guid, calvin);

        _calvinColdcastle = calvin;

        _logger.LogInformation("Calvin Coldcastle is standing by at {position}.", CalvinPosition);
    }

    // Clicked: he offers to set a match up, and you say yes or no.
    private void OfferSnowballMatch(Player player)
    {
        // ★ NOT WHILE THEY'RE ALREADY QUEUED. Offering a match to someone who is already waiting for one
        // is wrong on its own terms, and it also stopped one shape of the re-opening bug below.
        if (player.MatchmakingQueueId != 0)
            return;

        // ★ AND NOT WHILE HIS OWN DIALOG IS STILL UP. PendingDialogChoices is cleared the moment either
        // button is answered, so a non-null value here means the offer is on screen right now and this is
        // a duplicate trigger, not a new conversation.
        if (player.PendingDialogChoices is not null)
            return;

        // ★★ THE REAL FIX FOR "CALVIN KEEPS TALKING TO ME" IS NOT HERE - it is in
        // CommandPacketFreeInteractionNpcHandler, and it is worth knowing why. The client's 26/20
        // auto-interact carries no target guid and is fired on UI EVENTS as well as on proximity, so
        // every panel closed or HUD button pressed while standing next to Calvin resolved to him and
        // re-ran this method. The two guards above only cover the cases where he happens to know
        // something is in progress; a player who simply opened his offer, declined it and then touched
        // the interface was caught by neither. That packet is now acted on once per approach, which fixes
        // it for every dialog NPC rather than just this one. These guards stay as cheap insurance.

        var dialog = new CommandPacketShowDialog
        {
            DialogueTextId = CalvinGreetingId,
            NpcGuid = _calvinColdcastle?.Guid ?? 0,
            CameraFocusParam = 1f, // frame the camera on him, as a real conversation does
        };

        dialog.Responses.Add(new CommandPacketShowDialog.Response
        {
            Id = CalvinAcceptResponseId,
            LabelTextId = CalvinAcceptTextId,
            Param1 = DialogPlusImageId,        // something follows this click
            Param2 = DialogGreenButtonSet,
        });

        dialog.Responses.Add(new CommandPacketShowDialog.Response
        {
            Id = CalvinDeclineResponseId,
            LabelTextId = CalvinDeclineTextId,
            Param1 = DialogLeaveArrowImageId,  // this click just ends the conversation
            Param2 = DialogBeigeButtonSet,
        });

        player.PendingDialogChoices = new Dictionary<int, Action>
        {
            [CalvinAcceptResponseId] = () => OpenSnowballMatchmaking(player),
            // Declining needs no action at all: the 26/6 handler tears the dialog down either way.
            [CalvinDeclineResponseId] = () => { },
        };

        player.SendTunneled(dialog);
    }

    // "Ok, lets do this!" - open the Matchmaking panel, which is where retail went next: the QUEUE LIST
    // first ("1 Waiting  Pirate's Plunder…"), the player picks Snowball Fighting there and hits Next, and
    // only then does the game's own pane come up. See SnowballArenaZone.MatchmakingOpenQueueId for why
    // that id is 0 rather than the snowball queue - passing 51 skips the list.
    //
    // `/snowball arena` still drops straight into a match, which is the way in that doesn't depend on any
    // of this.
    private void OpenSnowballMatchmaking(Player player)
    {
        player.SendTunneled(new SelectQueueForUserPacket
        {
            QueueId = SnowballArenaZone.MatchmakingOpenQueueId,
        });

        _logger.LogInformation("Calvin Coldcastle opened matchmaking (queue id {queue}) for {player}.",
            SnowballArenaZone.MatchmakingOpenQueueId, player.Name?.FullName);
    }
}
