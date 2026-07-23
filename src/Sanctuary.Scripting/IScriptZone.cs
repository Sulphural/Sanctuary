using Microsoft.Extensions.Logging;

namespace Sanctuary.Scripting;

public interface IScriptZone
{
    int Id { get; }
    string Name { get; }
    ILogger Logger { get; }

    bool TrySpawnNpc(int npcId, ulong? npcGuid, float x, float y, float z, float heading);

    // Reports one fixed spawn point back to the zone, instead of spawning anything directly. Used by
    // scripts that supply POSITIONS ONLY for a caller-defined enemy roster (e.g. a dungeon's fixed enemy
    // composition in C#) — see BaseZone.CollectScriptSpawnPoints for how the calling side consumes these.
    void AddSpawnPoint(float x, float y, float z);
}
