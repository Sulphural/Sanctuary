using Sanctuary.Game.Entities;

namespace Sanctuary.Game.Gathering;

// Shared world resource nodes (Miner ore veins, etc): click a node -> grant a real item -> the node
// hides for everyone who can see it and reappears after a respawn timer. A DI singleton; per-node
// state lives here, keyed by the node NPC's guid.
public interface IGatheringManager
{
    // Registers a spawned, already-positioned node NPC as gatherable, wiring its InteractAction and
    // UpdateEverySecondAction. Call once per node right after spawning it.
    void RegisterNode(Npc node, int itemDefinitionId, int respawnSeconds = 60);

    // Player clicked a registered node.
    void OnGatherInteract(Player player, Npc node);
}
