using System;
using System.Numerics;

using Sanctuary.Game.Entities;
using Sanctuary.Game.Resources.Definitions;
using Sanctuary.Packet;
using Sanctuary.Packet.Common;

namespace Sanctuary.Gateway.Helpers.Abilities;

public sealed class CakeAbility(AbilityServices services) : ConsumableAbility(services)
{
    public override bool Matches(ClientItemDefinition itemDefinition) =>
        _resourceManager.Consumables.Cakes.ContainsKey(itemDefinition.Id);

    public override bool HandleAbility(Player player, AbilityPacketClientRequestStartAbility packet, int slot, ClientItem clientItem, ClientItemDefinition itemDefinition)
    {
        _resourceManager.Consumables.Cakes.TryGetValue(itemDefinition.Id, out var cakeDefinition);

        if (player.IsItemOnCooldown(itemDefinition.Id))
            return SendFailure(player);

        SpawnCakeNpc(player, cakeDefinition!);

        player.StartItemCooldown(itemDefinition.Id, cakeDefinition!.CooldownMs);
        player.StartActionBarCooldown(ActionBarId, slot, itemDefinition.Icon.Id, itemDefinition.NameId, clientItem.Count, cakeDefinition.CooldownMs);

        return true;
    }

    private void SpawnCakeNpc(Player player, CakeItemDefinition cakeDefinition)
    {
        var forwardDirection = Vector3.Transform(new Vector3(0, 0, 1), player.Rotation);
        var spawnPosition = new Vector4(
            player.Position.X + forwardDirection.X * 1.5f,
            player.Position.Y + forwardDirection.Y * 1.5f,
            player.Position.Z + forwardDirection.Z * 1.5f,
            player.Position.W
        );

        var cakeNpc = SpawnNpc(player, spawnPosition, npc =>
        {
            npc.NameId = cakeDefinition.NameId;
            npc.ModelId = cakeDefinition.ModelId;
            npc.TextureAlias = "";
            npc.TintAlias = "";
            npc.Scale = 1.0f;
            npc.Animation = cakeDefinition.Animation;
            npc.HideNamePlate = false;
            npc.IsInteractable = true;
            npc.CursorId = (byte)cakeDefinition.CursorId;
        });

        if (cakeNpc is null)
            return;

        if (cakeDefinition.Type == CakeItemType.BossCake)
        {
            var lastTransform = -1;

            cakeNpc.InteractAction = player =>
            {
                lastTransform = RollExcluding(cakeDefinition.TransformAbilityIds.Length, lastTransform);
                var abilityId = cakeDefinition.TransformAbilityIds[lastTransform];

                if (_resourceManager.Consumables.Transformations.TryGetValue(abilityId, out var transform))
                    player.ApplyTemporaryAppearance(transform.ModelId, transform.DurationMs, transform.CompositeEffectId);
            };
        }
        else
        {
            var scareReadyTime = DateTimeOffset.MinValue;
            var lastRoll = -1;

            cakeNpc.InteractAction = player =>
            {
                if (DateTimeOffset.UtcNow < scareReadyTime)
                    return;

                scareReadyTime = DateTimeOffset.UtcNow.AddMilliseconds(cakeDefinition.ScareCooldownMs);

                // Every scare group and transform is equally likely, except the one that played last.
                var roll = RollExcluding(cakeDefinition.ScareGroups.Length + cakeDefinition.TransformAbilityIds.Length, lastRoll);
                lastRoll = roll;

                if (roll < cakeDefinition.ScareGroups.Length)
                {
                    var scareEffects = cakeDefinition.ScareGroups[roll];

                    for (var i = 0; i < scareEffects.Length; i++)
                    {
                        player.SendTunneledToVisible(new PlayerUpdatePacketPlayCompositeEffect
                        {
                            Guid = cakeNpc.Guid,
                            CompositeEffectId = scareEffects[i],
                            Position = cakeNpc.Position,
                            Clear = i == 0
                        }, true);
                    }
                }
                else
                {
                    var abilityId = cakeDefinition.TransformAbilityIds[roll - cakeDefinition.ScareGroups.Length];

                    if (_resourceManager.Consumables.Transformations.TryGetValue(abilityId, out var transform))
                        player.ApplyTemporaryAppearance(transform.ModelId, transform.DurationMs, transform.CompositeEffectId);
                }
            };
        }

        BroadcastSpawn(player, cakeNpc, spawnPosition, cakeDefinition.SpawnPoofEffectId);

        var despawnTime = DateTimeOffset.UtcNow.AddMilliseconds(cakeDefinition.LifetimeMs);

        cakeNpc.UpdateEverySecondAction = () =>
        {
            if (DateTimeOffset.UtcNow >= despawnTime)
                DespawnNpc(cakeNpc, cakeDefinition.SpawnPoofEffectId);
        };
    }

    private static int RollExcluding(int count, int previous)
    {
        if (count <= 1 || previous < 0)
            return Random.Shared.Next(count);

        var roll = Random.Shared.Next(count - 1);

        return roll >= previous ? roll + 1 : roll;
    }
}
