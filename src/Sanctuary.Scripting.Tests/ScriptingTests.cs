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

        public bool TrySpawnNpc(int npcId, ulong? npcGuid, float x, float y, float z, float heading) => true;

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

    [TestCleanup]
    public void Cleanup()
    {
        _serviceProvider.Dispose();
    }
}
