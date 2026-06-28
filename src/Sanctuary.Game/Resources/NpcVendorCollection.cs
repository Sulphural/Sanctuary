using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

using Microsoft.Extensions.Logging;

namespace Sanctuary.Game.Resources;

public class VendorNotification
{
    public int Type { get; set; }
    public int ImageId { get; set; }
    public int DescriptionId { get; set; }
    public int NameId { get; set; }
    public int SubTextId { get; set; }
    public int Unknown3 { get; set; }
    public bool Unknown8 { get; set; }
    public int CompositeEffectId { get; set; }
    public bool Combat { get; set; }
    public bool Unknown10 { get; set; }
}

public class NpcVendorDefinition
{
    public List<int> Items { get; set; } = [];
    public List<int> ItemCosts { get; set; } = [];
    public List<int> Bundles { get; set; } = [];
    public int SubTextNameId { get; set; }
    public int ActiveProfile { get; set; }
    public int NameplateImageId { get; set; }
    public int ImageSetId { get; set; }
    public int NotificationImageSetId { get; set; }
    public VendorNotification? Notification { get; set; }
}

public class NpcVendorCollection : ConcurrentDictionary<ulong, NpcVendorDefinition>
{
    private readonly ILogger _logger;

    public NpcVendorCollection(ILogger logger)
    {
        _logger = logger;
    }

    public bool Load(string filePath)
    {
        if (!File.Exists(filePath))
        {
            _logger.LogWarning("NPC vendor file not found: \"{file}\". No NPC vendors will be loaded.", filePath);
            return true;
        }

        try
        {
            using var fileStream = File.OpenRead(filePath);
            var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(fileStream);

            if (raw is null)
                return true;

            foreach (var (key, value) in raw)
            {
                if (!ulong.TryParse(key, out var guid))
                {
                    _logger.LogWarning("Invalid NPC GUID in vendor file: \"{key}\"", key);
                    continue;
                }

                NpcVendorDefinition def;

                if (value.ValueKind == JsonValueKind.Array)
                {
                    // Legacy format: "GUID": [item1, item2, ...]
                    var items = JsonSerializer.Deserialize<List<int>>(value.GetRawText());
                    def = new NpcVendorDefinition { Items = items ?? [] };
                }
                else
                {
                    // New format: "GUID": { "Items": [...], "SubTextNameId": 12345 }
                    def = JsonSerializer.Deserialize<NpcVendorDefinition>(value.GetRawText()) ?? new NpcVendorDefinition();
                }

                TryAdd(guid, def);
            }

            _logger.LogInformation("Loaded {count} NPC vendor entries from \"{file}\"", Count, filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse file \"{file}\".", filePath);
            return false;
        }

        return true;
    }
}
