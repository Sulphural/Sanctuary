using System.Collections.Generic;

using Sanctuary.Packet.Common;

namespace Sanctuary.Game.Combat;

// Shared builder for a job's passive-trait list (the AbilitiesScreen Traits section). A kit just declares its
// four traits and calls Build.
public static class JobTraits
{
    // One trait row: the client name/desc/icon ids + the job level it unlocks at.
    public readonly record struct Trait(int NameId, int DescId, int IconId, int Level);

    // Passive AbilityExperience entries for a rank, ending with the Present=0 terminator.
    // Present must be distinct + non-zero, and GLOBALLY unique across every job's traits: the client keys its
    // ability-def map by Present, so if two loaded profiles share a Present the second is dropped and its trait
    // row never renders. (This bit the Warrior — its L5 Instigation shares NameId 420950 with the Ninja's 4th
    // trait, so keying Present off the raw NameId made the Warrior row vanish.) Salt with the profile id to keep
    // trait rows in disjoint per-job bands. The padlock is off when Level (rank) > 0 — 1 once the job level
    // reaches the trait's unlock, else 0 (locked); RequiredLevel is just the "Unlocked at level N" caption.
    public static List<AbilityExperience> Build(IReadOnlyList<Trait> traits, int rank, int profileId)
    {
        var list = new List<AbilityExperience>(traits.Count + 1);
        foreach (var t in traits)
            list.Add(new AbilityExperience
            {
                Present = profileId * 1_000_000 + t.NameId, // globally-unique record id (see note above)
                IsActivateable = false,
                NameId = t.NameId,
                DescriptionId = t.DescId,
                IconId = t.IconId,
                Level = rank >= t.Level ? 1 : 0,
                RequiredLevel = t.Level,
            });
        list.Add(new AbilityExperience { Present = 0 });
        return list;
    }
}
