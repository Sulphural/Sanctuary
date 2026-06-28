using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;

using Microsoft.Extensions.Logging;

using Sanctuary.Core.Collections;
using Sanctuary.Game.Resources.Definitions;

namespace Sanctuary.Game.Resources;

public class NpcSpawnCollection : ObservableConcurrentDictionary<ulong, NpcSpawnDefinition>
{
    private readonly ILogger _logger;

    public NpcSpawnCollection(ILogger logger)
    {
        _logger = logger;
    }

    public bool Load(string filePath)
    {
        if (!File.Exists(filePath))
        {
            _logger.LogWarning("NPC spawn file not found, no NPCs will be spawned. \"{file}\"", filePath);
            return true;
        }

        try
        {
            var lines = File.ReadAllLines(filePath);

            // Skip the header line
            for (int i = 1; i < lines.Length; i++)
            {
                var line = lines[i].Trim();

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                // Split by tabs
                var parts = line.Split('\t');

                if (parts.Length < 8)
                {
                    _logger.LogWarning("Invalid NPC spawn data at line {line}: insufficient columns", i + 1);
                    continue;
                }

                try
                {
                    var npcSpawn = new NpcSpawnDefinition
                    {
                        Guid = ulong.Parse(parts[0]),
                        ModelId = int.Parse(parts[1]),
                        Rotation = new Quaternion(
                            float.Parse(parts[2], CultureInfo.InvariantCulture),
                            float.Parse(parts[3], CultureInfo.InvariantCulture),
                            float.Parse(parts[4], CultureInfo.InvariantCulture),
                            0f
                        ),
                        Position = new Vector4(
                            float.Parse(parts[5], CultureInfo.InvariantCulture),
                            float.Parse(parts[6], CultureInfo.InvariantCulture),
                            float.Parse(parts[7], CultureInfo.InvariantCulture),
                            1f
                        ),
                        NameId = parts.Length > 8 && !string.IsNullOrWhiteSpace(parts[8]) ? int.Parse(parts[8]) : 0,
                        TextureAlias = parts.Length > 9 && !string.IsNullOrWhiteSpace(parts[9]) ? parts[9] : null,
                        ModelFileName = parts.Length > 10 && !string.IsNullOrWhiteSpace(parts[10]) ? parts[10] : null,
                        Name = parts.Length > 11 && !string.IsNullOrWhiteSpace(parts[11]) ? parts[11] : null
                    };

                    if (!TryAdd(npcSpawn.Guid, npcSpawn))
                    {
                        _logger.LogWarning("Failed to add NPC spawn entry. Guid: {guid} at line {line}", npcSpawn.Guid, i + 1);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to parse NPC spawn data at line {line}", i + 1);
                }
            }

            _logger.LogInformation("Loaded {count} NPC spawn entries from \"{file}\"", Count, filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse file \"{file}\".", filePath);
            return false;
        }

        if (Count == 0)
            _logger.LogInformation("No NPC spawn entries found in \"{file}\".", filePath);

        return true;
    }
}
