using System.Collections.Generic;

using Sanctuary.Game.Entities;
using Sanctuary.Packet;
using Sanctuary.Packet.Common;

namespace Sanctuary.Game.Combat;

// Medic kit — surface over MedicWeaponAbilities. Drives the equipped-weapon toolbar + traits + gameplay, and
// seeds the equipped weapon's item-def Abilities (the AbilitiesScreen Attack/Special columns). Ability icons/
// names are UNCONFIRMED (0) this pass — see MedicWeaponAbilities.cs header for what's real vs. unmined.
public sealed class MedicJobKit : IJobKit
{
    public int ProfileId => MedicWeaponAbilities.MedicProfileId;
    public bool UsesCombatEnergy => true;
    public float AutoTargetReach => 7f; // melee reach
    public IReadOnlyList<int> SlotAbilityDefIds { get; } = new[] { 4895, 4899 };

    public IReadOnlyList<int> WeaponDefIds => MedicWeaponAbilities.AllWeaponDefIds;

    public AbilityPacketSetDefinition? BuildToolbar(Player player, IResourceManager resources) =>
        MedicWeaponAbilities.BuildToolbar(player, resources);

    public WeaponAbility ResolveAbility(Player player, int slot) =>
        MedicWeaponAbilities.ResolveAbility(player, slot);

    public (int NameId, int DescId, int IconId)? ResolveDefinition(Player player, int abilityDefId) =>
        MedicWeaponAbilities.ResolveDefinition(player, abilityDefId);

    public List<ItemDefinition.ItemAbilityEntry> BuildItemAbilityEntries(int weaponDefId) =>
        MedicWeaponAbilities.BuildItemAbilityEntries(weaponDefId);

    public List<AbilityExperience>? BuildTraitEntries(int rank) => MedicWeaponAbilities.BuildTraitEntries(rank);

    public (int NameId, int DescId, int IconId) SlotNameIcon(int weaponDefId, int slot) =>
        MedicWeaponAbilities.SlotNameIcon(weaponDefId, slot);
}
