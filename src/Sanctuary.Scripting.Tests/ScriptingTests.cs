using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Sanctuary.Scripting.Tests;

[TestClass]
public class ScriptingTests
{
    private sealed class FakeZone : IScriptZone
    {
        public int Id => 1;
        public required string Name { get; init; }
        public ILogger Logger => NullLogger.Instance;
        public List<(float X, float Y, float Z)> SpawnPoints { get; } = [];

        public List<int> SpawnedNpcIds { get; } = [];
        public List<(int ModelId, int ItemDefinitionId, string Name)> GatheringNodes { get; } = [];
        public List<(float X, float Y, float Z, float Heading)> SnowballPiles { get; } = [];

        public bool TrySpawnNpc(int npcId, ulong? npcGuid, float x, float y, float z, float heading)
        {
            SpawnedNpcIds.Add(npcId);
            return true;
        }

        public bool TrySpawnGatheringNode(int modelId, int itemDefinitionId, string name, float x, float y, float z)
        {
            GatheringNodes.Add((modelId, itemDefinitionId, name));
            return true;
        }

        public bool TrySpawnSnowballPile(float x, float y, float z, float heading)
        {
            SnowballPiles.Add((x, y, z, heading));
            return true;
        }

        public bool TrySpawnQuestCollectible(ulong guid, float x, float y, float z)
        {
            QuestCollectibles.Add(guid);
            return true;
        }

        public List<ulong> QuestCollectibles { get; } = [];

        public bool TrySpawnDungeonEntrance(int poiId, float x, float y, float z, float heading)
        {
            DungeonEntrances.Add(poiId);
            return true;
        }

        public List<int> DungeonEntrances { get; } = [];

        public void AddSpawnPoint(float x, float y, float z) => SpawnPoints.Add((x, y, z));

        public void AddSpawnArea(float x, float y, float z, int count)
        {
            for (var i = 0; i < count; i++)
                SpawnPoints.Add((x, y, z));
        }
    }

    private ServiceProvider _serviceProvider = null!;
    private ScriptManager _scriptManager = null!;

    [TestInitialize]
    public void Setup()
    {
        var services = new ServiceCollection();

        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton<ScriptManager>();

        _serviceProvider = services.BuildServiceProvider();

        _scriptManager = _serviceProvider.GetRequiredService<ScriptManager>();
    }

    [TestMethod]
    public void InitSucceeds()
    {
        _scriptManager.Load();
    }

    [TestMethod]
    public async Task AllZoneScriptsValid()
    {
        var zoneScriptsDirectory = ScriptManager.ZoneScriptsDirectory;
        var luaFiles = Directory.GetFiles(zoneScriptsDirectory, "*.lua");
        foreach (var luaFile in luaFiles)
        {
            _ = await _scriptManager.LoadInstanceAsync(luaFile);
        }
    }

    [TestMethod]
    public async Task BanditHideoutReportsExpectedSpawnPointCount()
    {
        var zone = new FakeZone { Name = "sg_bandit_hideout" };
        var context = await _scriptManager.GetContextForZoneAsync(zone);
        Assert.IsNotNull(context, "sg_bandit_hideout.lua should load.");

        await context!.CallFunctionAsync("getSpawnPoints", zone);

        // 9 thugawugs + 5 Big Bandits + 1 boss (Muggenstomp), matching DungeonDefinition's ActivityId=29
        // Enemies composition. EncounterArenaZone.BuildDungeonSpawns silently falls back to its procedural
        // layout on any other count, so a mismatch here would ship without the mistake being obvious.
        Assert.AreEqual(15, zone.SpawnPoints.Count);
    }

    [TestMethod]
    public async Task FabledRealmsPlacesTheWorldProps()
    {
        var zone = new FakeZone { Name = "FabledRealms" };
        var context = await _scriptManager.GetContextForZoneAsync(zone);
        Assert.IsNotNull(context, "FabledRealms.lua should load.");

        await context!.CallFunctionAsync("onStart", zone);

        // The overworld's placement all comes out of this one generated script, so a generator that
        // silently stopped emitting a block would otherwise just look like the props vanished in game.
        // Counts are Resources/MiningNodes.json and Resources/SnowballPiles.json.
        Assert.AreEqual(5, zone.GatheringNodes.Count, "ore veins (MiningNodes.json)");
        Assert.AreEqual(3, zone.SnowballPiles.Count, "snowball piles (SnowballPiles.json)");
        // 39 = 31 + the 8 candy bags of Bag Snatchers (quest 3095). This number is meant to move when a
        // collect goal is added or removed - what it guards is the generator silently emitting NONE.
        Assert.AreEqual(39, zone.QuestCollectibles.Count, "collect-goal pickups (Quests.json)");
        Assert.AreEqual(42, zone.DungeonEntrances.Count, "dungeon entrances (PointOfInterests.json)");

        // The pickup guid is its identity - it binds back to a (quest, goal), so a generator that
        // renumbered them would silently credit the wrong goals. 700000000000 is CollectibleGuidBase.
        Assert.AreEqual(700000000000UL, zone.QuestCollectibles[0], "first collectible guid");

        // The NPC roster dwarfs the props - this only guards against the roster block disappearing.
        Assert.IsTrue(zone.SpawnedNpcIds.Count > 4000, $"NPC roster looked short: {zone.SpawnedNpcIds.Count}");
    }

    [TestCleanup]
    public void Cleanup()
    {
        _serviceProvider.Dispose();
    }
}
