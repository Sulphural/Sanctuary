using Microsoft.Extensions.Logging;

namespace Sanctuary.Scripting;

public interface IScriptZone
{
    int Id { get; }
    string Name { get; }
    ILogger Logger { get; }

    bool TrySpawnNpc(int npcId, ulong? npcGuid, float x, float y, float z, float heading);

    // World props that aren't Npcs.json entries. Each takes only a position plus the ids that identify
    // WHICH prop it is - the behaviour behind it (gather registration, the interact action, the models and
    // the real localized nameplates) stays in C#, so a script can place one without being able to
    // misconfigure it.

    // A shared harvestable resource node - an ore vein for the Miner job. itemDefinitionId is what a
    // successful gather grants; the zone registers it with the gathering manager, which owns the deplete
    // and respawn timers.
    bool TrySpawnGatheringNode(int modelId, int itemDefinitionId, string name, float x, float y, float z);

    // A Snow Days snowball pile. Clicking one hands the player the snowball tool - see SnowballTool, which
    // owns the model, the nameplate and the behaviour.
    bool TrySpawnSnowballPile(float x, float y, float z, float heading);

    // A Collect-goal pickup. The guid is the identity the quest system already assigned this pickup when
    // it loaded Quests.json - it's what binds the pickup back to its (quest, goal), so the zone looks the
    // rest up from it and REFUSES a guid it doesn't know. That makes a script left stale by a Quests.json
    // edit fail loudly here instead of quietly crediting the wrong goal.
    bool TrySpawnQuestCollectible(ulong guid, float x, float y, float z);

    // The invisible clickable widget at a walk-up dungeon's mouth. Identified by its ATLAS POI id, which is
    // what the dungeon catalog is keyed by - so the script says where the entrance stands and the zone
    // works out which dungeon that is, its name and its offer. A POI with no dungeon behind it is not an
    // error (most POIs are just map markers); the zone simply places nothing.
    bool TrySpawnDungeonEntrance(int poiId, float x, float y, float z, float heading);

    // Reports one fixed spawn point back to the zone, instead of spawning anything directly. Used by
    // scripts that supply POSITIONS ONLY for a caller-defined enemy roster (e.g. a dungeon's fixed enemy
    // composition in C#) — see BaseZone.CollectScriptSpawnPoints for how the calling side consumes these.
    void AddSpawnPoint(float x, float y, float z);

    // Same idea as AddSpawnPoint, but for a whole PACK at once: reports one marker point + a count, and
    // the zone scatters that many points around it itself. Lets a script mark "there's a pack of N enemies
    // roughly here" without hand-computing N individual coordinates — see BaseZone.AddSpawnArea.
    void AddSpawnArea(float x, float y, float z, int count);
}
