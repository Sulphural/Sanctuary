using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using Sanctuary.Game.Resources.Definitions;

namespace Sanctuary.Game.Resources;

public sealed class CollectibleSpawn
{
    public ulong Guid { get; init; }
    public int ModelId { get; init; }
    public int NameId { get; init; }
    public Vector4 Position { get; init; }
}

public class QuestDefinitionCollection
{
    private readonly ILogger _logger;

    public ConcurrentDictionary<int, QuestDefinition> Quests { get; } = new();

    public ConcurrentDictionary<ulong, List<int>> ByGiver { get; } = new();

    public ConcurrentDictionary<ulong, List<int>> ByTarget { get; } = new();

    public ConcurrentDictionary<ulong, (int QuestId, int GoalIndex)> Collectibles { get; } = new();

    public List<CollectibleSpawn> CollectibleSpawns { get; } = new();

    private const ulong CollectibleGuidBase = 700000000000UL;
    private ulong _nextCollectibleGuid = CollectibleGuidBase;

    public QuestDefinitionCollection(ILogger logger)
    {
        _logger = logger;
    }

    public bool TryGet(int questId, out QuestDefinition definition) => Quests.TryGetValue(questId, out definition!);

    public bool Load(string filePath)
    {
        if (!File.Exists(filePath))
        {
            _logger.LogWarning("Quest file not found: \"{file}\". No quests will be loaded.", filePath);
            return true;
        }

        try
        {
            using var fileStream = File.OpenRead(filePath);

            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            };

            var quests = JsonSerializer.Deserialize<List<QuestDefinition>>(fileStream, options);

            if (quests is null)
            {
                _logger.LogError("No entries found in file \"{file}\".", filePath);
                return false;
            }

            foreach (var quest in quests)
            {
                if (!Quests.TryAdd(quest.QuestId, quest))
                {
                    _logger.LogWarning("Duplicate quest id {id} in \"{file}\".", quest.QuestId, filePath);
                    continue;
                }

                if (quest.GiverGuid != 0)
                    ByGiver.GetOrAdd(quest.GiverGuid, _ => new List<int>()).Add(quest.QuestId);

                if (quest.TargetGuid != 0)
                    ByTarget.GetOrAdd(quest.TargetGuid, _ => new List<int>()).Add(quest.QuestId);

                var goalNameIds = new HashSet<int>();
                foreach (var goal in quest.EffectiveGoals)
                {
                    if (goal.TargetGuid != 0 && goal.TargetGuid != quest.TargetGuid
                        && !ByTarget.GetOrAdd(goal.TargetGuid, _ => new List<int>()).Contains(quest.QuestId))
                        ByTarget[goal.TargetGuid].Add(quest.QuestId);

                    if (!goalNameIds.Add(goal.NameId))
                        _logger.LogWarning("Quest {id}: duplicate goal NameId {nameId} - goals will collide client-side (checkmarks/advance won't render correctly).", quest.QuestId, goal.NameId);
                }

                var effective = quest.EffectiveGoals;
                for (int gi = 0; gi < effective.Count; gi++)
                {
                    var goal = effective[gi];

                    if (goal.Type != QuestGoalType.Collect)
                        continue;

                    if (goal.RequiredCount <= 0)
                        goal.RequiredCount = goal.CollectSpawns.Count;

                    foreach (var pos in goal.CollectSpawns)
                    {
                        if (pos is null || pos.Length < 3)
                            continue;

                        var guid = _nextCollectibleGuid++;
                        Collectibles[guid] = (quest.QuestId, gi);
                        CollectibleSpawns.Add(new CollectibleSpawn
                        {
                            Guid = guid,
                            ModelId = goal.CollectModelId,
                            NameId = goal.CollectNameId,
                            Position = new Vector4(pos[0], pos[1], pos[2], 1f)
                        });
                    }
                }
            }

            _logger.LogInformation("Loaded {count} quest definitions from \"{file}\".", Quests.Count, filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse file \"{file}\".", filePath);
            return false;
        }

        return true;
    }
}
