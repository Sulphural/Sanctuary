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

    // Same idea as AddSpawnPoint, but for a whole PACK at once: reports one marker point + a count, and
    // the zone scatters that many points around it itself. Lets a script mark "there's a pack of N enemies
    // roughly here" without hand-computing N individual coordinates — see BaseZone.AddSpawnArea.
    void AddSpawnArea(float x, float y, float z, int count);
}
