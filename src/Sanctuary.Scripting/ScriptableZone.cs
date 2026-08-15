using System.Threading.Tasks;

using Lua;

namespace Sanctuary.Scripting;

internal sealed class ScriptableZone(IScriptZone zone) : ILuaUserData
{
    private readonly IScriptZone _zone = zone;
    private LuaTable? _metatable;

    public LuaTable? Metatable
    {
        get => _metatable ??= BuildMetatable();
        set => _metatable = value;
    }

    private LuaTable BuildMetatable()
    {
        var metatable = new LuaTable();

        metatable["__index"] = new LuaFunction("__index", (context, cancellationToken) =>
        {
            var key = context.GetArgument<string>(1);

            var result = key switch
            {
                "id" => new LuaValue(_zone.Id),
                "name" => new LuaValue(_zone.Name),
                "spawnNpc" => SpawnNpcFunction,
                "spawnNpcWithGuid" => SpawnNpcWithGuidFunction,
                "spawnGatheringNode" => SpawnGatheringNodeFunction,
                "spawnSnowballPile" => SpawnSnowballPileFunction,
                "spawnQuestCollectible" => SpawnQuestCollectibleFunction,
                "spawnDungeonEntrance" => SpawnDungeonEntranceFunction,
                "addSpawnPoint" => AddSpawnPointFunction,
                "addSpawnArea" => AddSpawnAreaFunction,
                _ => LuaValue.Nil
            };

            return new ValueTask<int>(context.Return(result));
        });

        return metatable;
    }

    private LuaFunction SpawnNpcFunction => new("spawnNpc", (context, cancellationToken) =>
    {
        var npcId = context.GetArgument<int>(0);
        var x = context.GetArgument<float>(1);
        var y = context.GetArgument<float>(2);
        var z = context.GetArgument<float>(3);
        var heading = context.GetArgument<float>(4);

        var success = _zone.TrySpawnNpc(npcId, null, x, y, z, heading);

        return new ValueTask<int>(context.Return(success));
    });

    private LuaFunction SpawnNpcWithGuidFunction => new ("spawnNpcWithGuid", (context, cancellationToken) =>
    {
        var npcId = context.GetArgument<int>(0);
        var npcGuid = context.GetArgument<ulong>(1);
        var x = context.GetArgument<float>(2);
        var y = context.GetArgument<float>(3);
        var z = context.GetArgument<float>(4);
        var heading = context.GetArgument<float>(5);

        var success = _zone.TrySpawnNpc(npcId, npcGuid, x, y, z, heading);

        return new ValueTask<int>(context.Return(success));
    });

    private LuaFunction SpawnGatheringNodeFunction => new("spawnGatheringNode", (context, cancellationToken) =>
    {
        var modelId = context.GetArgument<int>(0);
        var itemDefinitionId = context.GetArgument<int>(1);
        var name = context.GetArgument<string>(2);
        var x = context.GetArgument<float>(3);
        var y = context.GetArgument<float>(4);
        var z = context.GetArgument<float>(5);

        var success = _zone.TrySpawnGatheringNode(modelId, itemDefinitionId, name, x, y, z);

        return new ValueTask<int>(context.Return(success));
    });

    private LuaFunction SpawnSnowballPileFunction => new("spawnSnowballPile", (context, cancellationToken) =>
    {
        var x = context.GetArgument<float>(0);
        var y = context.GetArgument<float>(1);
        var z = context.GetArgument<float>(2);
        var heading = context.GetArgument<float>(3);

        var success = _zone.TrySpawnSnowballPile(x, y, z, heading);

        return new ValueTask<int>(context.Return(success));
    });

    private LuaFunction SpawnQuestCollectibleFunction => new("spawnQuestCollectible", (context, cancellationToken) =>
    {
        var guid = context.GetArgument<ulong>(0);
        var x = context.GetArgument<float>(1);
        var y = context.GetArgument<float>(2);
        var z = context.GetArgument<float>(3);

        var success = _zone.TrySpawnQuestCollectible(guid, x, y, z);

        return new ValueTask<int>(context.Return(success));
    });

    private LuaFunction SpawnDungeonEntranceFunction => new("spawnDungeonEntrance", (context, cancellationToken) =>
    {
        var poiId = context.GetArgument<int>(0);
        var x = context.GetArgument<float>(1);
        var y = context.GetArgument<float>(2);
        var z = context.GetArgument<float>(3);
        var heading = context.GetArgument<float>(4);

        var success = _zone.TrySpawnDungeonEntrance(poiId, x, y, z, heading);

        return new ValueTask<int>(context.Return(success));
    });

    private LuaFunction AddSpawnPointFunction => new("addSpawnPoint", (context, cancellationToken) =>
    {
        var x = context.GetArgument<float>(0);
        var y = context.GetArgument<float>(1);
        var z = context.GetArgument<float>(2);

        _zone.AddSpawnPoint(x, y, z);

        return new ValueTask<int>(0);
    });

    private LuaFunction AddSpawnAreaFunction => new("addSpawnArea", (context, cancellationToken) =>
    {
        var x = context.GetArgument<float>(0);
        var y = context.GetArgument<float>(1);
        var z = context.GetArgument<float>(2);
        var count = context.GetArgument<int>(3);

        _zone.AddSpawnArea(x, y, z, count);

        return new ValueTask<int>(0);
    });
}
