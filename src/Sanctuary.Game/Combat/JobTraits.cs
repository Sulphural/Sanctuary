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
    // ability-def map by Present, so if two entries share a Present the second is dropped and BOTH trait rows
    // resolve off whichever one survived (same displayed name/level for both). (This bit the Warrior — its L5
    // Instigation shares NameId 420950 with the Ninja's 4th trait, so keying Present off the raw NameId made
    // the Warrior row vanish. It also bites any job whose traits aren't real-NameId'd yet, like Medic's 4
    // traits all sharing NameId=0 — all 4 collided on the same Present and all showed the same "Unlocked at
    // level 5", 2026-07-28.) Salt with BOTH the profile id (disjoint per-job bands) AND the trait's own index
    // (disjoint WITHIN a job regardless of NameId, including the all-zero/unmined case) rather than the raw
    // NameId alone. The padlock is off when Level (rank) > 0 — 1 once the job level reaches the trait's
    // unlock, else 0 (locked); RequiredLevel is just the "Unlocked at level N" caption.
    public static List<AbilityExperience> Build(IReadOnlyList<Trait> traits, int rank, int profileId)
    {
        var list = new List<AbilityExperience>(traits.Count + 1);
        for (var i = 0; i < traits.Count; i++)
        {
            var t = traits[i];
            list.Add(new AbilityExperience
            {
                Present = profileId * 1_000_000 + (i + 1) * 1_000 + t.NameId, // globally-unique record id (see note above)
                IsActivateable = false,
                NameId = t.NameId,
                DescriptionId = t.DescId,
                IconId = t.IconId,
                Level = rank >= t.Level ? 1 : 0,
                RequiredLevel = t.Level,
            });
        }
        list.Add(new AbilityExperience { Present = 0 });
        return list;
    }
}
