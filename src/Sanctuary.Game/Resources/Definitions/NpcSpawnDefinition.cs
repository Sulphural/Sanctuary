using System.Numerics;

namespace Sanctuary.Game.Resources.Definitions;

public class NpcSpawnDefinition
{
    public ulong Guid { get; set; }
    public int ModelId { get; set; }
    public Quaternion Rotation { get; set; }
    public Vector4 Position { get; set; }
    public int NameId { get; set; }
    public string? TextureAlias { get; set; }
    public string? ModelFileName { get; set; }
    public string? Name { get; set; }
}
