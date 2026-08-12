namespace Sanctuary.Game.Interactions;

// Icons for the radial interaction menu.
//
// ★ These are RAW IMAGE ids (Client/Resources/Images/Images.txt), not image-SET ids. The radial movie
// resolves setButton's iconId through frSetIconByIndex (radial_menu.gfx), which indexes Images.txt
// directly. The two spaces overlap and neither errors, so a set id here silently draws the wrong
// picture - passing the quest badge's NotificationImageSetId 2 drew image 2,
// `icon_UI_jobs_star02_64.dds`, i.e. a star.
//
// ★★ AND Images.txt IS NOT THE SHIPPED SET. Only 19,620 of its 38,331 icon_* rows exist in this
// build's .pack files; the rest are listed but absent, and an absent id renders as nothing. SOE's
// purpose-drawn `icon_UI_context_*` family is mostly in that gap - givequest01, merchant01, talk01,
// toggle01, attack01 and others are all missing here. So verify any new icon id against the packs
// (scratchpad export_icons.py dumps them as PNGs) rather than trusting the table.
//
// The NPC head badge is a third space again: NotificationImageSetId 2 ("!") / 6 ("?") are badge types
// the HUD draws itself, so that art cannot be handed to the ring - the objective icons below are its
// look-alike counterpart, and are the same "!"/"?" discs retail shows on the ring.
public static class ContextIcons
{
    // Verified present in the packs and eyeballed as PNGs.
    public const int QuestOffer = 20;      // icon_UI_objective_task_64  - "!" on an orange disc
    public const int QuestTurnIn = 19;     // icon_UI_objective_quest_64 - "?" on an orange disc
    public const int Merchant = 41385;     // icon_UI_context_trade01_32 (the 64px variant is absent)
    public const int Examine = 133;        // icon_UI_context_examine01_64
    public const int AddFriend = 134;      // icon_UI_context_friend_add01_64
    public const int RemoveFriend = 135;   // icon_UI_context_friend_remove01_64
    public const int Use = 140;            // icon_UI_context_use01_64
    public const int Collect = 1109;       // icon_UI_context_collect01_64
    public const int Fight = 1111;         // icon_UI_context_fight01_64
    public const int MiniGame = 1114;      // icon_UI_context_minigame01_64
    public const int Ignore = 9557;        // icon_UI_context_friend_ignore01_64

    // NOT SHIPPED in this build - listed in Images.txt but in no pack, so they draw nothing:
    //   138   icon_UI_context_talk01_64
    //   1112  icon_UI_context_givequest01_64
    //   1113  icon_UI_context_merchant01_64
    //   12052 icon_UI_context_friend_unignore01_64  <- StopIgnoringInteraction still points at this
}
