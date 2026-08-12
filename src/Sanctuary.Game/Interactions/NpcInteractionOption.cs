using System;

using Sanctuary.Game.Entities;

namespace Sanctuary.Game.Interactions;

// One selectable entry in an NPC's radial interaction menu (the ring of icons the client draws
// around an NPC that can do more than one thing - see Npc.InteractionProviders).
//
// This is deliberately NOT an IInteraction: those are registered once at startup with a fixed id and
// a fixed label, which suits the player-to-player menu (Inspect / Add Friend / Ignore) but not an
// NPC's, where the label is per-quest and the set changes as the player's state changes. Options are
// built per interact, given a throwaway id, and remembered on the Player until the client replies.
public sealed class NpcInteractionOption
{
    // Ring icon: a RAW image id (Images.txt), not an image-set id - see ContextIcons.
    public required int IconId { get; init; }

    // Localized id for the label under the icon (e.g. the quest title).
    public required int ButtonTextId { get; init; }

    public int TooltipId { get; init; }

    // What running this option does - the same call the single-action path would have made.
    public required Action<Player> Invoke { get; init; }
}
