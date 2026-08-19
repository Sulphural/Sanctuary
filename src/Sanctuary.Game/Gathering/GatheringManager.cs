using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using Sanctuary.Game.Collections;
using Sanctuary.Game.Entities;
using Sanctuary.Game.Quests;
using Sanctuary.Packet;

namespace Sanctuary.Game.Gathering;

public sealed class GatheringManager : IGatheringManager
{
    // Sparkle burst played on a successful gather (same effect id as quest collectibles -
    // PFX_sparkles-swirl_gold_treasure-reward).
    private const int GatherEffectId = 5386;

    // farm_dig_long (id 3900009, AnimationTypes.xml "Farm" branch) IS a real clip bound on the base human
    // model (confirmed by extracting+inflating human_m.adr and grepping for the slot name), so it's not a
    // fake/invented id - but sending it via AbilityPacketStartCasting.Animation played nothing: that field
    // resolves through a smaller per-model animation-table index, not the client's global slot-name hash
    // registry. The Farm branch's slots are also priority=100/doNotInterrupt=1 - a different animation
    // CLASS than the dance branch (priority=1, no interrupt flag) the boombox's SetSynchronizedAnimations
    // loop trick was built for. PlayerUpdatePacketSetAnimation with PlayType=2 ("play now") is the
    // mechanism already pcap-verified against the real 2014 server for exactly this kind of one-shot
    // action clip (see that packet's own doc comment - it's how live NPCs play action/locomotion clips).
    // farm_dig_long doesn't loop on its own (doNotInterrupt), so it's re-triggered periodically for the
    // whole channel instead of once. No dedicated pickaxe clip exists anywhere in AnimationTypes.xml/
    // AnimationGroups.xml - farm_dig_long (a shovel-digging clip) is the closest real asset, and Miner's
    // own tool is a shovel (Profiles.json DefaultItems "Student Miner Shovel").
    private const int MiningAnimationId = 3900009;

    // farm_dig_long doesn't loop - re-send it at roughly this cadence to keep the digging animation going
    // for the whole channel. A guess at the clip's real length; adjust if it looks off live.
    private const float MiningAnimationReplaySeconds = 2f;

    // Reset animation (StopDancing's exact idle id/mechanism) once the channel ends, success or cancel.
    private const int IdleAnimId = 1;

    // How long the mining channel takes to fill before the ore is granted.
    private const float MiningActionSeconds = 15f;

    // How far the player is allowed to wander from the node before the gather cancels. Checked every
    // second during the channel (not just at the end), so the digging animation actually stops the moment
    // the player walks off, instead of continuing to play for the full duration regardless.
    private const float MaxGatherDistance = 8f;
    private const float MaxGatherDistanceSquared = MaxGatherDistance * MaxGatherDistance;

    private sealed class NodeState
    {
        public required Npc Node { get; init; }
        public required int ItemDefinitionId { get; init; }
        public required int RespawnSeconds { get; init; }
        public int SecondsRemaining { get; set; }

        // 0 = available, 1 = depleted. Interlocked-guarded so two players clicking the same node in the
        // same tick can't both win the gather - only one CompareExchange(1, 0) call can transition it.
        private int _depleted;
        public bool Depleted => Volatile.Read(ref _depleted) != 0;
        public bool TryClaim() => Interlocked.CompareExchange(ref _depleted, 1, 0) == 0;
        public void Reset() => Volatile.Write(ref _depleted, 0);
    }

    private readonly IQuestManager _questManager;
    private readonly ICollectionManager _collectionManager;
    private readonly ILogger<GatheringManager> _logger;
    private readonly ConcurrentDictionary<ulong, NodeState> _nodes = new();

    public GatheringManager(IQuestManager questManager, ICollectionManager collectionManager, ILogger<GatheringManager> logger)
    {
        _questManager = questManager;
        _collectionManager = collectionManager;
        _logger = logger;
    }

    public void RegisterNode(Npc node, int itemDefinitionId, int respawnSeconds = 60)
    {
        var state = new NodeState
        {
            Node = node,
            ItemDefinitionId = itemDefinitionId,
            RespawnSeconds = respawnSeconds
        };

        _nodes[node.Guid] = state;

        node.InteractAction = player => OnGatherInteract(player, node);
        node.UpdateEverySecondAction = () => Tick(state);
    }

    public void OnGatherInteract(Player player, Npc node)
    {
        if (!_nodes.TryGetValue(node.Guid, out var state) || !state.TryClaim())
            return;

        _logger.LogInformation("Gather started: player={player} node={node} item={item}", player.CharacterId, node.Guid, state.ItemDefinitionId);

        // Fillable progress bar only (no Animation - that field can't resolve MiningAnimationId, see
        // above). The node stays visible/clickable-looking to everyone else for the whole channel;
        // TryClaim above already prevents a second player from starting their own gather on it meanwhile.
        player.SendTunneledToVisible(new AbilityPacketStartCasting
        {
            Unknown = player.Guid,
            Unknown2 = player.Guid,
            ActionTime = MiningActionSeconds,
            HasActionProgress = true,
        }, sendToSelf: true);

        PlayMiningAnimation(player);

        _ = Task.Run(async () =>
        {
            try
            {
                var elapsedSeconds = 0f;
                var sinceAnimSeconds = 0f;
                while (elapsedSeconds < MiningActionSeconds)
                {
                    var step = Math.Min(1f, MiningActionSeconds - elapsedSeconds);
                    await Task.Delay((int)(step * 1000));
                    elapsedSeconds += step;
                    sinceAnimSeconds += step;

                    var dx = player.Position.X - node.Position.X;
                    var dz = player.Position.Z - node.Position.Z;
                    if (dx * dx + dz * dz > MaxGatherDistanceSquared)
                    {
                        _logger.LogInformation("Gather cancelled (player moved away): player={player} node={node}", player.CharacterId, node.Guid);
                        StopMiningAnimation(player);
                        state.Reset(); // node was never hidden, so it's still there for anyone to try again
                        return;
                    }

                    if (sinceAnimSeconds >= MiningAnimationReplaySeconds)
                    {
                        sinceAnimSeconds = 0f;
                        PlayMiningAnimation(player);
                    }
                }

                StopMiningAnimation(player);
                CompleteGather(player, node, state);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Gather completion failed.");
            }
        });
    }

    private static void PlayMiningAnimation(Player player)
    {
        player.SendTunneledToVisible(new PlayerUpdatePacketSetAnimation
        {
            Guid = player.Guid,
            AnimationId = MiningAnimationId,
            PlayType = 2 // "play now" - see MiningAnimationId's comment for why this packet, not the dance loop's PlayType=1 trick.
        }, sendToSelf: true);
    }

    private static void StopMiningAnimation(Player player)
    {
        player.SendTunneledToVisible(new PlayerUpdatePacketSetAnimation
        {
            Guid = player.Guid,
            AnimationId = IdleAnimId,
            PlayType = 1
        }, sendToSelf: true);
    }

    private void CompleteGather(Player player, Npc node, NodeState state)
    {
        _questManager.GrantItem(player, state.ItemDefinitionId);

        // Quests can ask for harvested goods, so a successful gather is reported the same way a kill is.
        _questManager.OnItemGathered(player, state.ItemDefinitionId);

        // Harvested goods are collection entries too - pay the collection out if that pickup was its last.
        _collectionManager.OnItemCollected(player, state.ItemDefinitionId);

        // Same "you earned an item" HUD celebration (icon + "received N") quest item rewards already use -
        // a fixed-position popup, not attached to the node's world position.
        player.SendTunneled(new RewardNonBundledItemPacket { ItemDefinitionId = state.ItemDefinitionId, Quantity = 1 });

        _logger.LogInformation("Gather completed: player={player} node={node} item={item}", player.CharacterId, node.Guid, state.ItemDefinitionId);

        var effect = new PlayerUpdatePacketPlayCompositeEffect
        {
            Guid = node.Guid,
            CompositeEffectId = GatherEffectId,
            Position = node.Position
        };
        var remove = new PlayerUpdatePacketRemovePlayer { Guid = node.Guid };

        foreach (var visiblePlayer in node.VisiblePlayers.Values)
        {
            visiblePlayer.SendTunneled(effect);
            visiblePlayer.SendTunneled(remove);
        }

        node.Visible = false;
        state.SecondsRemaining = state.RespawnSeconds;
    }

    private void Tick(NodeState state)
    {
        if (!state.Depleted)
            return;

        if (--state.SecondsRemaining > 0)
            return;

        state.Reset();
        state.Node.Visible = true;

        var addPacket = state.Node.GetAddNpcPacket();
        foreach (var visiblePlayer in state.Node.VisiblePlayers.Values)
            visiblePlayer.SendTunneled(addPacket);
    }
}
