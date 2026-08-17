using System;
using System.Collections.Generic;
using System.Numerics;

using Microsoft.Extensions.Logging;

using Sanctuary.Game.Entities;
using Sanctuary.Packet;

namespace Sanctuary.Game.Zones;

// TRINA TURTLEDOVE - the "Snow Days Cheer Specialist", and the giver of the 12 Days of Holidays.
//
// She is the hub of the whole Snow Days event: a new daily challenge from the 13th to the 24th, each one
// worth a present and its bow, and a Present Tracker for watching that collection fill up. The wiki puts
// it plainly - "Each day during Snow Days will have a separate quest that can be started by talking to
// Trina Turtledove. Additionally, you can track the amount of presents and bows you have by talking to
// her." She is also already referenced from this codebase: Calvin Coldcastle's own header quotes her
// Day 2 line, because Day 2 is the one that sends you to him.
//
// ★★ EVERY ID BELOW IS REAL, recovered from the client's own locale rather than invented. The text ids
// the wire carries are T4 DIRECTORY ids, not the CIDs stored in en_us_data.dat, so each one was reversed
// by brute-forcing lookup2("Global.Text.<N>") against the CID of the known string (the method in
// reference_t4_localization_hash, verified this session against its own known-good pair before use).
// That is why these are exact rather than approximate - anything here can be checked by re-running it.
public sealed partial class StartingZone
{
    // 441046 "Trina Turtledove".
    private const int TrinaNameId = 441046;

    // ★ 441047 "Snow Days Cheer Specialist" - her real ROLE line, recovered alongside the name and kept
    // here deliberately even though nothing sends it yet: Npc has no nameplate subtitle field (NameColor,
    // NameScale and NameplateImageId are the only nameplate knobs), and NotificationInfo.SubTextId belongs
    // to minimap badges, not to a standing NPC. If a subtitle field ever turns up, this is the id for it.
    private const int TrinaRoleNameId = 441047;

    // ★★ HER REAL MODEL, AND THE CLIENT NAMES IT OUTRIGHT: Models.txt row 4108
    // `human_f_winterwonderland.agr` carries the comment **"12Days2012 - Quest Giver"**. There is exactly
    // one 12 Days quest giver and this is her - so this is a recovered value, not a look-alike chosen by
    // eye, and it matches the reference screenshot (black top hat with holly, red-and-white striped scarf,
    // green button-front dress). Shipped: Assets_manifest.txt carries human_f_winterwonderland.agr.z.
    //
    // ★ An earlier pass here used 45 human_f_santa on the reasoning that no Trina model shipped. That was
    // wrong twice over: the search that "proved" it only matched Models.txt's COMMENT column, so it missed
    // both this row and the human_f_snowhill (826) female counterpart to Calvin's model. Searching an .adr
    // table by comment alone will keep hiding models - match the FILENAME column.
    private const int TrinaModelId = 4108;

    // ★ THE PRESENT BADGE, 382 - given explicitly rather than derived, and it is the right family for her:
    // Calvin wears 251 (the snowball-fight bubble) because he runs the snowball fight, and Trina hands out
    // PRESENTS. The badge is what makes her readable as the event's hub from across the village.
    private const int TrinaBadgeImageId = 382;

    // ── Her conversation ───────────────────────────────────────────────────────────────────────────────
    // ★ THIS IS A REAL SHIPPED CONVERSATION, not a written-for-us line. The client's locale holds it as a
    // contiguous block titled "12 Days Introduction" (441439), and it is a genuine two-step exchange with
    // a follow-up question - so it is reproduced with both steps rather than flattened into one popup:
    //
    //   441441  Trina : "Every day from the 13th through the 24th we will be adding a new challenge.
    //                    Complete each challenge on the first day it is offered and you get a present with
    //                    its bow... you get an extra "Big Present" for every 4 of the daily presents..."
    //   441442  You   : "And if I miss a day?"      <- the ONLY button on step one
    //   441450  Trina : "You can still complete the challenge for any day that you miss and earn your
    //                    present, but then you will need to buy a bow in order to open it..."
    //   439938  You   : "Got it!"                   <- the ONLY button on step two
    //
    // ★ BOTH STEPS SHOW EXACTLY ONE BUTTON, matched against the retail screenshots. An earlier pass gave
    // step one a second "Okay!" escape button; retail has none - the follow-up question is the only way
    // out, which is what makes this a conversation rather than an offer. And the closing label is
    // "Got it!" (439938), not the 441435 "Okay!" that sits in the same locale block but belongs to a
    // different NPC's dialog. (2045 is an older duplicate of "Got it!" and renders identically.)
    //
    // Sent as CommandPacketShowDialog (26/3) for the same reason Calvin's offer is: the text carries <BR>
    // markup, which that dialog renders and a chat bubble would not.
    private const int TrinaIntroTextId = 441441;
    private const int TrinaMissedDayQuestionId = 441442;
    private const int TrinaMissedDayAnswerId = 441450;
    private const int TrinaGotItTextId = 439938;

    // ★ THE BUTTON DRESSING IS READ OFF THE SCREENSHOTS, and the ids off the extracted image set - the
    // exported art is named with its own id, so 00300__ui_dialog_greencheck.png IS image 300 (the same way
    // 00303__ui_dialog_plus.png confirms the 303 that Calvin and QuestDialogue already use).
    //   step one : orange PLUS on a BEIGE button  -> 303 + set 18
    //   step two : green CHECK on a GREEN button  -> 300 + set 17
    // Note this inverts Calvin's pairing (he puts the plus on green), so it cannot be copied from him.
    private const int DialogGreenCheckImageId = 300; // ui_dialog_greencheck

    private const int TrinaAskResponseId = 1;
    private const int TrinaDoneResponseId = 2;

    // Measured in game (!pos): X=231.12 Y=24.89 Z=410.23, heading -72 degrees. Used verbatim - these are
    // real standing ground, the same rule every other measured spawn in this zone follows.
    // (Supersedes an earlier measurement at 237.78/25.67/403.20 heading -95.)
    private static readonly Vector4 TrinaPosition = new(231.121f, 24.891f, 410.231f, 1f);
    private const float TrinaHeading = -72f * MathF.PI / 180f;

    private Npc? _trinaTurtledove;

    // Permanent, like Calvin: she is the event's quest hub, so she has to be there whenever someone comes
    // looking. (Bruce is the exception - he exists only while performing.) Called from OnStart.
    private void SpawnTrinaTurtledove()
    {
        if (!TryCreateNpc(out var trina))
            return;

        trina.ModelId = TrinaModelId;
        trina.NameId = TrinaNameId;
        trina.Name = "Trina Turtledove";
        trina.Static = true;
        trina.Visible = true;
        trina.Scale = _resourceManager.Models.TryGetValue(TrinaModelId, out var model) && model.Scale != 0f
            ? model.Scale
            : 1f;

        trina.NotificationImageSetId = TrinaBadgeImageId;
        trina.CursorId = 17; // hand cursor - she's clickable
        trina.InteractAction = OpenTrinaIntroduction;

        var rotation = new Quaternion(MathF.Sin(TrinaHeading), 0f, MathF.Cos(TrinaHeading), 0f);
        trina.UpdatePosition(TrinaPosition, rotation);
        GetTileFromPosition(TrinaPosition).Entities.TryAdd(trina.Guid, trina);

        _trinaTurtledove = trina;

        _logger.LogInformation("Trina Turtledove is standing by at {position}.", TrinaPosition);
    }

    // Step one: she explains the 12 Days, and you can either ask the follow-up or leave it there.
    private void OpenTrinaIntroduction(Player player)
    {
        // Her dialog is already up - this is a duplicate trigger, not a new conversation. Same guard
        // Calvin carries, and for the same reason: the client's 26/20 auto-interact fires on UI events as
        // well as on proximity (see CommandPacketFreeInteractionNpcHandler).
        if (player.PendingDialogChoices is not null)
            return;

        var dialog = new CommandPacketShowDialog
        {
            DialogueTextId = TrinaIntroTextId,
            NpcGuid = _trinaTurtledove?.Guid ?? 0,
            CameraFocusParam = 1f, // frame the camera on her, as a real conversation does
        };

        // The single button - the orange plus on a beige button, exactly as retail draws it.
        dialog.Responses.Add(new CommandPacketShowDialog.Response
        {
            Id = TrinaAskResponseId,
            LabelTextId = TrinaMissedDayQuestionId,
            Param1 = DialogPlusImageId,        // something follows this click
            Param2 = DialogBeigeButtonSet,
        });

        player.PendingDialogChoices = new Dictionary<int, Action>
        {
            [TrinaAskResponseId] = () => AnswerTrinaMissedDay(player),
        };

        player.SendTunneled(dialog);
    }

    // Step two: her answer about missing a day, closed by the green "Got it!" - which is also what opens
    // the 12 Days of Presents panel.
    private void AnswerTrinaMissedDay(Player player)
    {
        var dialog = new CommandPacketShowDialog
        {
            DialogueTextId = TrinaMissedDayAnswerId,
            NpcGuid = _trinaTurtledove?.Guid ?? 0,
            CameraFocusParam = 1f,
        };

        dialog.Responses.Add(new CommandPacketShowDialog.Response
        {
            Id = TrinaDoneResponseId,
            LabelTextId = TrinaGotItTextId,
            Param1 = DialogGreenCheckImageId,  // green tick, not the leave arrow
            Param2 = DialogGreenButtonSet,
        });

        player.PendingDialogChoices = new Dictionary<int, Action>
        {
            [TrinaDoneResponseId] = () => OpenTwelveDaysOfPresents(player),
        };

        player.SendTunneled(dialog);
    }

    // ── The 12 Days of Presents panel ─────────────────────────────────────────────────────────────────
    // ★ EVERY STRING BELOW IS RECOVERED, not chosen - and the client's own authoring is visible in the ids:
    // each objective sits immediately before the button that satisfies it.
    //   441964 "12 Days of Presents"        (title; 441931 is a duplicate)
    //   441923 "Complete the quest"      -> 441924 "Start Quest"
    //   441925 "Get a bow for the present" -> 441926 "Purchase Bow"
    //   441927 "Open on December 25th"   -> 441928 "Open Present"      441930 "Already Opened"
    //   441950..441961 "Day 1 Present" .. "Day 12 Present"
    private const int PresentsTitleId = 441964;
    private const int PresentsObjective1Id = 441923;
    private const int PresentsObjective2Id = 441925;
    private const int PresentsObjective3Id = 441927;
    private const int PresentsButtonOpenId = 441928;    // "Open Present"
    private const int PresentsButtonOpenedId = 441930;  // "Already Opened"
    private const int PresentsButtonBuyId = 441926;     // "Purchase Bow"
    private const int PresentsDay1NameId = 441950;      // days 1-12 run consecutively from here

    // ★ THE PANEL OPENS AFTER THE DIALOG TEARDOWN, NOT BEFORE IT. "Got it!" is the button that ENDS the
    // conversation, so the 26/6 handler sends CommandPacketEndDialog right behind it, and that teardown
    // restores the camera. Opening the browser in the same breath would have it fighting a camera restore
    // aimed at the panel on its way out, so it waits a beat and arrives to a settled screen.
    private const int PresentsPanelDelayMs = 400;

    private const int PresentsQuestId = 1;
    private const int PresentsDayCount = 12;
    private const int PresentsBigPresentCount = 3;      // the three Big Presents along the bottom

    // ── The panel's art, and it is PURPOSE-DRAWN for this feature ──────────────────────────────────────
    // ★ Found in the client's own Resources/Images/Images.txt (`#ID^FILE_NAME^`), which is the id->name
    // table these IconId fields address. The whole set is authored at exactly the panel's tile sizes,
    // which is how you can tell it is the right art rather than a look-alike:
    //
    //   40732..40755  icon_gift_NN_bow / icon_gift_NN_box _83x83   - twelve days, BOW then BOX per day,
    //                                                                so box(day) = 40733 + day*2
    //   40756/40757   icon_gift_grayscale_bow / _box _83x83        - ★ the "shadow" present a day wears
    //                                                                until it is earned
    //   40758..40760  icon_gift_uber_01..03_134x134                - ★ the three Big Presents, at the
    //                                                                larger size the bottom row uses
    //   41049         icon_gift_grayscale_open                     - (unused here: an opened-but-grey box)
    //
    // An earlier pass guessed at housing-decoration present icons (26781/26787). Those are 32px item
    // icons for a completely different thing and looked nothing like retail - the giveaway that a guess is
    // wrong is the SIZE not matching the slot it renders into.
    private const int PresentsLockedIconId = 40757;      // icon_gift_grayscale_box_83x83
    private const int PresentsDay1IconId = 40733;        // icon_gift_01_box_83x83; +2 per day
    private const int PresentsBigPresent1IconId = 40758; // icon_gift_uber_01_134x134; +1 per prize

    // The gift badge above the title. 28546 icon_ui_claim_gift_32.
    private const int PresentsHeaderIconId = 28546;

    // ── What is inside a present ──────────────────────────────────────────────────────────────────────
    // ★ THE REAL SNOW DAYS GIFT BOXES, recovered as name/description PAIRS out of the locale exactly like
    // everything else here. Retail cycles them across the twelve days:
    //   441206 "Snow Days Gift Box - Cookies"      441221 "A delicious assortment of Snow Days treats!"
    //   441207 "Snow Days Gift Box - Fireworks"    441222 "A fantastic assortment of Snow Days fireworks!"
    //   441208 "Snow Days Gift Box - Decorations"  441223 "A festive assortment of Snow Days decorations!"
    //   441209 "Snow Days Builders Pack"           441224 "Get everything you need to make a giant..."
    private static readonly (int TitleId, int BodyId)[] PresentRewards =
    [
        (441206, 441221),
        (441207, 441222),
        (441208, 441223),
        (441209, 441224),
    ];

    // ★★ THE REWARD POPUP TAKES AN IMAGE **SET** ID, THE TILES TAKE RAW IMAGE IDS - two different id
    // spaces for what looks like the same kind of field, and mixing them is what drew the client's
    // "OOPS!!" missing-image placeholder in the popup while the very same asset rendered fine on a prize
    // tile. Both tables ship side by side and every gift asset appears in each:
    //     Resources/Images/Images.txt     40732..40760  <- raw image ids, used by Slot/PrizeSlot.IconId
    //     Resources/Images/ImageSets.txt   8195..8223   <- set ids,       used by ShowItemPanel's iconId
    // The sets follow the same bow/box pairing: box(day) = 8196 + day*2, 8220 grayscale box,
    // 8221..8223 the three uber presents.
    //
    // Retail's exact gift-box artwork is still not identified (nothing ships under a snowdays/gift_box
    // name, and no item or bundle carries 441206-441209 to read one off), so the popup shows a real
    // wrapped present instead. `/trina rewardicon <id>` retunes it - now with a SET id.
    public static int PresentRewardIconSetId { get; set; } = 8221; // icon_gift_uber_01_134x134

    // ★ THE OPENED PRESENT. Image 41049 icon_gift_grayscale_open - the only gift asset with NO ImageSets
    // entry, which is fine because the tiles address raw image ids. Worn by a day that has been claimed.
    private const int PresentsOpenedIconId = 41049;

    // ★★ THE REAL BOW BUNDLES: StoreBundles 5516..5527 are "12 Days Bow - Day 1".."Day 12" - so
    // KeyBundleId is a StoreBundles row id, and each day's bow is simply 5516 + day. That is what the
    // panel's "Purchase Bow" button spends against.
    private const int PresentsDay1BowBundleId = 5516;

    // How long the event has left, in seconds - the countdown in the panel's corner.
    public static int PresentsSecondsRemaining { get; set; } = 12 * 24 * 60 * 60;

    // How many days are unlocked so far (retail unlocks one per day, 13th-24th). Day 1 only by default,
    // matching the reference screenshot. `/trina days <n>` changes it.
    public static int PresentsUnlockedDays { get; set; } = 1;

    // ★ THE PROBE IS KEPT BUT IS NO LONGER NEEDED FOR FIELD IDENTITY - every column is now named from the
    // client's own GetColumnName/GetData tables (see ProgressiveQuestClientDataPacket). It stays as a way
    // to re-check a column quickly if the panel ever renders oddly again.
    public static bool ProbeFields { get; set; }

    private const int ProbeBase = 7100;
    private const int SlotProbeBase = 7200;
    private const int PrizeProbeBase = 7300;

    // Which days each player has already opened. Per-player because the panel is a personal collection;
    // in-memory because the 12 Days chain is not persisted yet (nothing else about it is either).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, HashSet<int>> _twelveDaysClaimed = new();

    private HashSet<int> ClaimedDaysFor(Player player) =>
        _twelveDaysClaimed.GetOrAdd(player.Guid, _ => []);

    private ProgressiveQuestClientDataPacket BuildTwelveDaysState(Player player)
    {
        var packet = new ProgressiveQuestClientDataPacket
        {
            QuestId = PresentsQuestId,
            NameId = ProbeFields ? ProbeBase + 1 : PresentsTitleId,
            SecondsRemaining = ProbeFields ? ProbeBase + 2 : PresentsSecondsRemaining,
            ActiveSlotId = 0,
            IconId = ProbeFields ? ProbeBase + 4 : PresentsHeaderIconId,
            ObjectiveStringId1 = ProbeFields ? ProbeBase + 5 : PresentsObjective1Id,
            ObjectiveStringId2 = ProbeFields ? ProbeBase + 6 : PresentsObjective2Id,
            ObjectiveStringId3 = ProbeFields ? ProbeBase + 7 : PresentsObjective3Id,
            UseSmallWindow = false,
            ButtonStringIdOpen = ProbeFields ? ProbeBase + 9 : PresentsButtonOpenId,
            ButtonStringIdOpened = ProbeFields ? ProbeBase + 10 : PresentsButtonOpenedId,
            ButtonStringIdBuy = ProbeFields ? ProbeBase + 11 : PresentsButtonBuyId,
        };

        // The twelve day tiles, in one of three states:
        //   locked  - not earned yet: the grayscale "shadow" present, and its bow can be bought
        //   earned  - openable: that day's own coloured box, "Open Present" lights up
        //   claimed - already opened: the opened-box art, and it can no longer be claimed
        var claimed = ClaimedDaysFor(player);

        for (var day = 0; day < PresentsDayCount; day++)
        {
            var unlocked = day < PresentsUnlockedDays;
            var opened = claimed.Contains(day);

            packet.Slots.Add(new ProgressiveQuestClientDataPacket.Slot
            {
                QuestId = PresentsQuestId,
                SlotId = day,
                IconId = ProbeFields
                    ? SlotProbeBase + 3
                    : opened ? PresentsOpenedIconId
                    : unlocked ? PresentsDay1IconId + day * 2
                    : PresentsLockedIconId,
                HasSlotItem = unlocked,
                HasKeyItem = unlocked,
                CanDoQuest = !unlocked && day == PresentsUnlockedDays,
                CanPurchaseKeyItem = !unlocked,
                KeyBundleId = PresentsDay1BowBundleId + day,
                KeyBundlePrice = 1,   // the StoreBundles rows all carry Price 1
                // ★ Claiming is one-shot: an opened day drops CanClaimSlotItem, which is what flips the
                // button to "Already Opened" (441930, ButtonStringIdOpened) instead of "Open Present".
                CanClaimSlotItem = unlocked && !opened,
                TooltipId = 0,
                NameId = ProbeFields ? SlotProbeBase + 12 : PresentsDay1NameId + day,
            });
        }

        // The three Big Presents - one per four daily presents opened, so the bar fills as days are earned.
        for (var prize = 0; prize < PresentsBigPresentCount; prize++)
        {
            var earnedInGroup = System.Math.Clamp(PresentsUnlockedDays - prize * 4, 0, 4);

            packet.PrizeSlots.Add(new ProgressiveQuestClientDataPacket.PrizeSlot
            {
                QuestId = PresentsQuestId,
                PrizeSlotId = prize,
                NameId = ProbeFields ? PrizeProbeBase + 3 : 0,
                IconId = ProbeFields ? PrizeProbeBase + 4 : PresentsBigPresent1IconId + prize,
                TooltipId = 0,
                ProgressPercent = earnedInGroup * 25,
                CanClaimPrizeItem = earnedInGroup >= 4,
            });
        }

        return packet;
    }

    // ★ DATA FIRST, WINDOW SECOND. The client's own op207/1 receive path refreshes the three data sources
    // as the last thing it does, so the rows have to exist before anything is asked to draw them - opening
    // an unfed browser shows the empty grid.
    // Push fresh panel state without re-opening the window - the answer to a button press on the panel
    // (see BaseProgressiveQuestPacketHandler).
    public void ResendTwelveDaysState(Player player) => player.SendTunneled(BuildTwelveDaysState(player));

    // "Open Present" was clicked on a day. Raise the reward popup (op207/7 -> the client's
    // UnifiedMessageWindow:ShowItemPanel), mark the day opened, then refresh the grid behind it.
    public void ClaimTwelveDaysPresent(Player player, int slotId)
    {
        var day = System.Math.Clamp(slotId, 0, PresentsDayCount - 1);

        // Only a day that is actually earned and not already opened can be claimed - the panel enforces
        // this too, but a click is a client message and the server does not have to take its word for it.
        if (day >= PresentsUnlockedDays || !ClaimedDaysFor(player).Add(day))
        {
            ResendTwelveDaysState(player);
            return;
        }

        var reward = PresentRewards[day % PresentRewards.Length];

        player.SendTunneled(new ProgressiveQuestNotifyRewardItemPacket
        {
            IconId = PresentRewardIconSetId,
            TintId = 0,
            TitleId = reward.TitleId,
            BodyId = reward.BodyId,
        });

        ResendTwelveDaysState(player);
    }

    // Forget what a player has opened, so the panel can be walked through again while testing.
    public void ResetTwelveDaysClaims(Player player) => _twelveDaysClaimed.TryRemove(player.Guid, out _);

    public void OpenTwelveDaysOfPresents(Player player)
    {
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                await System.Threading.Tasks.Task.Delay(PresentsPanelDelayMs);

                player.SendTunneled(BuildTwelveDaysState(player));           // op207/1 ClientData
                player.SendTunneled(new ProgressiveQuestShowWindowPacket()); // op207/0 ShowWindow

                _logger.LogInformation("Trina Turtledove: sent 12 Days state ({days} slots, {prizes} prizes) " +
                                       "+ ShowWindow to {player}{probe}.",
                    PresentsDayCount, PresentsBigPresentCount, player.Name?.FullName,
                    ProbeFields ? " [PROBE: definition fields stamped 7100+index]" : string.Empty);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Trina Turtledove: failed to open the 12 Days browser.");
            }
        });
    }

    // ── The 12 daily quests, and why none of them are wired here yet ───────────────────────────────────
    // ★ THE WHOLE CHAIN'S TEXT IS RECOVERED AND SITS RIGHT NEXT TO HER NAME in the locale, one
    // offer / objective / turn-in triple per day, running 441077-441124 with the "Return to Trina
    // Turtledove" turn-in goals at 441129-441203. Written down so nobody has to reverse them twice:
    //
    //   Day  1  441078 Penguin Defense       offer 441077  objective 441079  turn-in 441080
    //   Day  2  441082 Snowball Fight        offer 441081  objective 441083  turn-in 441084
    //   Day  3  441086 Crafty Robgoblins     offer 441085  objective 441087  turn-in 441088
    //   Day  4  441090 Cookie! Nom Nom!      offer 441089  objective 441091  turn-in 441092
    //   Day  5  441094 Wintery Fishing       offer 441093  objective 441095  turn-in 441096
    //   Day  6  441098 Defend the Gifting Tree offer 441097 objective 441099 turn-in 441100
    //   Day  7  441102 Snowhill Racing       offer 441101  objective 441103  turn-in 441104
    //   Day  8  441106 Nog Hog               offer 441105  objective 441107  turn-in 441108
    //   Day  9  441110 Snowhill Soccer       offer 441109  objective 441111  turn-in 441112
    //   Day 10  441114 Battle for Snowhill   offer 441113  objective 441115  turn-in 441116
    //   Day 11  441118 Snowhill Derby        offer 441117  objective 441119  turn-in 441120
    //   Day 12  441122 Abominable Invasion   offer 441121  objective 441123  turn-in 441124
    //
    // ★ MOST OF THEM CANNOT BE COMPLETED ON THIS SERVER, which is why adding the quests would mean adding
    // quests that dead-end. Days 1, 5, 7, 9 and 11 are gated on minigames that do not exist here at all
    // (Penguin Defense, fishing, kart racing, soccer, Demolition Derby), and days 4 and 8 want Snow Days
    // cookie and Eggnog consumables that are not implemented. What IS reachable today:
    //
    //   Day  2  Snowball Fight        - Calvin plus SnowballArenaZone deliver this one end to end.
    //   Day  6  Defend the Gifting Tree } both are StartingZone.SnowmenInvaders, which already spawns the
    //   Day 12  Abominable Invasion    } snowmen and the Abominable Snowman boss.
    //   Day 10  Battle for Snowhill   - the combat dungeons exist, though not all six it names.
    //
    // So the honest next slice is those four days rather than the chain as a whole, and it needs the quest
    // system (Quests.json + QuestManager) wired to her guid - deliberately left out of this change, which
    // is just the NPC herself.
}
