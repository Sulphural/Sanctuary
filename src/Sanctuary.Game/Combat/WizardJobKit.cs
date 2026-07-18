using System.Collections.Generic;

using Sanctuary.Game.Entities;
using Sanctuary.Packet;
using Sanctuary.Packet.Common;

namespace Sanctuary.Game.Combat;

// Wizard kit — surface over WizardWeaponAbilities. Drives the equipped-wand toolbar + traits + gameplay, and
// seeds the equipped wand's item-def Abilities (the AbilitiesScreen Attack/Special columns). Icons and names are
// the real abil_wizard_* ids, so seeding the shared item defs is safe (same as the other kits).
public sealed class WizardJobKit : IJobKit
{
    public int ProfileId => WizardWeaponAbilities.WizardProfileId;
    public bool UsesCombatEnergy => true;
    public float AutoTargetReach => 20f; // ranged caster
    public IReadOnlyList<int> SlotAbilityDefIds { get; } = new[] { 4895, 4899 };

    public IReadOnlyList<int> WeaponDefIds => WizardWeaponAbilities.AllWeaponDefIds;

    public AbilityPacketSetDefinition? BuildToolbar(Player player, IResourceManager resources) =>
        WizardWeaponAbilities.BuildToolbar(player, resources);

    public WeaponAbility ResolveAbility(Player player, int slot) =>
        WizardWeaponAbilities.ResolveAbility(player, slot);

    public (int NameId, int DescId, int IconId)? ResolveDefinition(Player player, int abilityDefId) =>
        WizardWeaponAbilities.ResolveDefinition(player, abilityDefId);

    public List<ItemDefinition.ItemAbilityEntry> BuildItemAbilityEntries(int weaponDefId) =>
        WizardWeaponAbilities.BuildItemAbilityEntries(weaponDefId);

    public List<AbilityExperience>? BuildTraitEntries(int rank) => WizardWeaponAbilities.BuildTraitEntries(rank);

    public (int NameId, int DescId, int IconId) SlotNameIcon(int weaponDefId, int slot) =>
        WizardWeaponAbilities.SlotNameIcon(weaponDefId, slot);
}
