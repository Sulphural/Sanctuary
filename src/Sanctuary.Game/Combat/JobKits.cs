using System.Collections.Generic;
using System.Linq;

using Sanctuary.Game.Entities;
using Sanctuary.Packet.Common;

namespace Sanctuary.Game.Combat;

// Registry of the combat job kits, keyed by profile id. Add a job here (one line) and the shared systems
// (toolbar, ability resolution, traits, item-def seeding) pick it up automatically.
public static class JobKits
{
    private static readonly Dictionary<int, IJobKit> ByProfileId = new IJobKit[]
    {
        new NinjaJobKit(),
        new ArcherJobKit(),
        new BrawlerJobKit(),
    }.ToDictionary(kit => kit.ProfileId);

    public static IEnumerable<IJobKit> All => ByProfileId.Values;

    public static IJobKit? For(int profileId) => ByProfileId.GetValueOrDefault(profileId);

    public static IJobKit? Active(Player player) => For(player.ActiveProfileId);

    public static bool Has(int profileId) => ByProfileId.ContainsKey(profileId);

    // Fill the profile's Traits section + Attack/Special column slots from the active kit. No-op for a job that
    // hasn't data'd its screen (BuildTraitEntries returns null), leaving the profile's default list alone.
    public static void ConfigureAbilitiesScreen(IJobKit kit, ClientPcProfile profile, int equippedWeaponDefId)
    {
        var traits = kit.BuildTraitEntries(profile.Rank);
        if (traits is null)
            return;

        profile.AbilityExperiences = traits;

        var (basicName, _, basicIcon) = kit.SlotNameIcon(equippedWeaponDefId, 0);
        var (specialName, _, specialIcon) = kit.SlotNameIcon(equippedWeaponDefId, 1);

        // A kit with no ability-name data (0) has traits but no Attack/Special column data yet — leave the
        // ability slots as the generic combat fill.
        if (basicName == 0)
            return;

        for (var i = 0; i < profile.Abilities.Count; i++)
            profile.Abilities[i] = new Ability { Type = 0 };

        profile.Abilities[0] = new Ability { Type = 3, IconId = basicIcon, NameId = basicName, AbilityDefinitionId = kit.SlotAbilityDefIds[0] };
        profile.Abilities[1] = new Ability { Type = 3, IconId = specialIcon, NameId = specialName, AbilityDefinitionId = kit.SlotAbilityDefIds[1] };
    }
}
