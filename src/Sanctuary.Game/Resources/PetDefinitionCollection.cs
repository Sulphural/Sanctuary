using System;
using System.IO;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using Sanctuary.Core.Collections;
using Sanctuary.Game.Resources.Definitions;

namespace Sanctuary.Game.Resources;

public class PetDefinitionCollection : ObservableConcurrentDictionary<int, PetDefinition>
{
    private readonly ILogger _logger;

    public PetDefinitionCollection(ILogger logger)
    {
        _logger = logger;
    }

    public bool Load(string filePath)
    {
        if (!File.Exists(filePath))
        {
            _logger.LogError("Missing {filename} resource file.", Path.GetFileName(filePath));
            return false;
        }

        try
        {
            var json = File.ReadAllText(filePath);
            var pets = JsonSerializer.Deserialize<PetDefinition[]>(json);

            if (pets is null)
            {
                _logger.LogError("Failed to deserialize {filename} resource file.", Path.GetFileName(filePath));
                return false;
            }

            Clear();

            foreach (var pet in pets)
                TryAdd(pet.Id, pet);

            _logger.LogInformation("Loaded {count} pets.", Count);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load {filename} resource file.", Path.GetFileName(filePath));
            return false;
        }
    }
}
