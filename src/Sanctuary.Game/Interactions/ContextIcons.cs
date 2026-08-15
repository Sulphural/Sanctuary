namespace Sanctuary.Game.Interactions;

// Icons for the radial interaction menu.
//
// ★ These are RAW IMAGE ids (Client/Resources/Images/Images.txt), not image-SET ids. The radial movie
// resolves setButton's iconId through frSetIconByIndex (radial_menu.gfx), which indexes Images.txt
// directly. The two spaces overlap and neither errors, so a set id here silently draws the wrong
// picture - passing the quest badge's NotificationImageSetId 2 drew image 2,
// `icon_UI_jobs_star02_64.dds`, i.e. a star.
//
// ★★ CHECKING THE .pack FILES ALONE IS NOT A VALID AVAILABILITY TEST - it wrongly condemns half the
// client's art. The packs hold ~76k entries; `Assets_manifest.txt` lists ~163k, and everything else
// streams on demand as "<name>.z". The whole `icon_UI_context_*` family IS available that way. Check
// packs OR manifest (scratchpad assets.py) before deciding an id is unusable.
//
// The NPC head badge is a third space again: NotificationImageSetId 2 ("!") / 6 ("?") are badge types
// the HUD draws itself, so that art cannot be handed to the ring - the objective icons below are its
// look-alike counterpart, and are the same "!"/"?" discs retail shows on the ring.
public static class ContextIcons
{
    // Verified present in the packs and eyeballed as PNGs.
    public const int QuestOffer = 20;      // icon_UI_objective_task_64  - "!" on an orange disc
    public const int QuestTurnIn = 19;     // icon_UI_objective_quest_64 - "?" on an orange disc
    public const int Merchant = 1113;      // icon_UI_context_merchant01_64
    public const int Examine = 133;        // icon_UI_context_examine01_64
    public const int AddFriend = 134;      // icon_UI_context_friend_add01_64
    public const int RemoveFriend = 135;   // icon_UI_context_friend_remove01_64
    public const int Use = 140;            // icon_UI_context_use01_64
    public const int Collect = 1109;       // icon_UI_context_collect01_64
    public const int Fight = 1111;         // icon_UI_context_fight01_64
    public const int MiniGame = 1114;      // icon_UI_context_minigame01_64
    public const int Ignore = 9557;        // icon_UI_context_friend_ignore01_64

    public const int Talk = 138;           // icon_UI_context_talk01_64
    public const int GiveQuest = 1112;     // icon_UI_context_givequest01_64
    public const int StopIgnoring = 12052; // icon_UI_context_friend_unignore01_64
    public const int Trade = 41385;        // icon_UI_context_trade01_32
}
