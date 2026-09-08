namespace Sanctuary.Game.Interactions;

// Icons for the radial interaction menu.
//
// These are RAW IMAGE ids (Client/Resources/Images/Images.txt), not image-SET ids. The radial movie
// resolves setButton's iconId through frSetIconByIndex (radial_menu.gfx), which indexes Images.txt
// directly. The two spaces overlap and neither errors, so a set id here silently draws the wrong
// picture.
//
// The NPC head badge is a third space again: NotificationImageSetId 2 ("!") / 6 ("?") are badge types
// the HUD draws itself, so that art cannot be handed to the ring - the objective icons below are its
// look-alike counterpart, and are the same "!"/"?" discs retail shows on the ring.
public static class ContextIcons
{
    public const int QuestOffer = 20;  // icon_UI_objective_task_64  - "!" on an orange disc
    public const int QuestTurnIn = 19; // icon_UI_objective_quest_64 - "?" on an orange disc
}
