using System.Collections.Generic;

using Sanctuary.Game.Entities;
using Sanctuary.Packet;
using Sanctuary.Packet.Common;

namespace Sanctuary.Game.Combat;

// Warrior kit — surface over WarriorWeaponAbilities. Drives the equipped-weapon toolbar + traits + gameplay,
// and seeds the equipped weapon's item-def Abilities (the AbilitiesScreen Attack/Special columns). Icons and
// names are the real abil_warrior_* ids, so seeding the shared item defs is safe (same as the ninja/archer/
// brawler kits).
public sealed class WarriorJobKit : IJobKit
{
    public int ProfileId => WarriorWeaponAbilities.WarriorProfileId;
    public bool UsesCombatEnergy => true;
    public float AutoTargetReach => 7f; // melee reach
    public IReadOnlyList<int> SlotAbilityDefIds { get; } = new[] { 4895, 4899 };

    public IReadOnlyList<int> WeaponDefIds => WarriorWeaponAbilities.AllWeaponDefIds;

    public AbilityPacketSetDefinition? BuildToolbar(Player player, IResourceManager resources) =>
        WarriorWeaponAbilities.BuildToolbar(player, resources);

    public WeaponAbility ResolveAbility(Player player, int slot) =>
        WarriorWeaponAbilities.ResolveAbility(player, slot);

    public (int NameId, int DescId, int IconId)? ResolveDefinition(Player player, int abilityDefId) =>
        WarriorWeaponAbilities.ResolveDefinition(player, abilityDefId);

    public List<ItemDefinition.ItemAbilityEntry> BuildItemAbilityEntries(int weaponDefId) =>
        WarriorWeaponAbilities.BuildItemAbilityEntries(weaponDefId);

    public List<AbilityExperience>? BuildTraitEntries(int rank) => WarriorWeaponAbilities.BuildTraitEntries(rank);

    public (int NameId, int DescId, int IconId) SlotNameIcon(int weaponDefId, int slot) =>
        WarriorWeaponAbilities.SlotNameIcon(weaponDefId, slot);
}
