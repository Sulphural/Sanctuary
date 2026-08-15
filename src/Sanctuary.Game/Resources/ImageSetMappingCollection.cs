using System.IO;

using Microsoft.Extensions.Logging;

using nietras.SeparatedValues;

using Sanctuary.Core.Collections;

namespace Sanctuary.Game.Resources;

// image-set id -> flat IMAGE id, generated from the client's Resources/Images/ImageSetMappings.txt
// (64px art preferred, falling back to 32 then 128).
//
// Needed because the client has TWO overlapping icon id spaces and neither errors on the wrong one.
// ClientItemDefinition.Icon.Id is an image-SET id, but several UI fields - the quest reward preview
// among them - want a flat image id. Roughly half the item icons happen to be valid in both spaces, so
// passing the set id straight through appears to work until it lands on one that isn't: Eggnog's set
// 5572 has no flat image 5572, so its reward preview drew the wrong thing.
public class ImageSetMappingCollection : ObservableConcurrentDictionary<int, int>
{
    private readonly ILogger _logger;

    public ImageSetMappingCollection(ILogger logger)
    {
        _logger = logger;
    }

    // The flat image id for an image-set id, or the id itself when unmapped - some callers legitimately
    // already hold a flat id, and passing it through unchanged is the safe fallback.
    public int ResolveIcon(int imageSetId)
        => TryGetValue(imageSetId, out var imageId) ? imageId : imageSetId;

    public bool Load(string filePath)
    {
        if (!File.Exists(filePath))
        {
            _logger.LogError("Failed to find file \"{file}\"", filePath);
            return false;
        }

        try
        {
            using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = Sep.New('^').Reader().From(fileStream);

            foreach (var row in reader)
            {
                if (row.ColCount < 2)
                    continue;

                if (int.TryParse(row[0].Span, out var setId) && int.TryParse(row[1].Span, out var imageId))
                    this[setId] = imageId;
            }
        }
        catch (System.Exception exception)
        {
            _logger.LogError(exception, "Failed to load \"{file}\"", filePath);
            return false;
        }

        _logger.LogInformation("Loaded {count} image-set mappings from \"{file}\".", Count, filePath);
        return true;
    }
}
