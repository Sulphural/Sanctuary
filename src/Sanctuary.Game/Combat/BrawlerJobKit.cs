using System;
using System.Collections.Generic;

using Sanctuary.Game.Entities;
using Sanctuary.Packet;
using Sanctuary.Packet.Common;

namespace Sanctuary.Game.Combat;

// Brawler kit — surface over BrawlerWeaponAbilities. Drives the equipped-weapon toolbar + traits + gameplay,
// and seeds the equipped weapon's item-def Abilities (the AbilitiesScreen Attack/Special columns). The first
// pass left this empty because the seeded icon was a wrong stand-in (a ninja sword on hammers), which looked
// broken on the shared coin-shop weapons; now the icons AND names are the real abil_brawler_* ids, so seeding
// is safe (same as the archer/ninja kits).
public sealed class BrawlerJobKit : IJobKit
{
    public int ProfileId => BrawlerWeaponAbilities.BrawlerProfileId;
    public bool UsesCombatEnergy => true;
    public float AutoTargetReach => 7f; // melee reach
    public IReadOnlyList<int> SlotAbilityDefIds { get; } = new[] { 4895, 4899 };

    public IReadOnlyList<int> WeaponDefIds => BrawlerWeaponAbilities.AllWeaponDefIds;

    public AbilityPacketSetDefinition? BuildToolbar(Player player, IResourceManager resources) =>
        BrawlerWeaponAbilities.BuildToolbar(player, resources);

    public WeaponAbility ResolveAbility(Player player, int slot) =>
        BrawlerWeaponAbilities.ResolveAbility(player, slot);

    public (int NameId, int DescId, int IconId)? ResolveDefinition(Player player, int abilityDefId) =>
        BrawlerWeaponAbilities.ResolveDefinition(player, abilityDefId);

    public List<ItemDefinition.ItemAbilityEntry> BuildItemAbilityEntries(int weaponDefId) =>
        BrawlerWeaponAbilities.BuildItemAbilityEntries(weaponDefId);

    public List<AbilityExperience>? BuildTraitEntries(int rank) => BrawlerWeaponAbilities.BuildTraitEntries(rank);

    public (int NameId, int DescId, int IconId) SlotNameIcon(int weaponDefId, int slot) =>
        BrawlerWeaponAbilities.SlotNameIcon(weaponDefId, slot);
}
