using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Sanctuary.Game.Pathfinding;

// Resolves a placement's model name (as it appears in a .gcnk RuntimeObject) to that model's real
// collision mesh, following the client's own asset chain - reverse-engineered 2026-08-06:
//
//   <name>.agr  -> XML <ActorSet><Actor name="<real>.adr"> (a skinned/retextured variant that just
//                  points at a shared base model - e.g. every cave_02_<material>_pieceNN.agr is the
//                  SAME cave_02_pieceNN.adr with different TextureAliases)
//   <name>.adr  -> references "<name>.dme" (render mesh), "<name>.dma", and "<name>.cdt" (COLLISION)
//   <name>.cdt  -> CDTA collision mesh (see CdtParser)
//
// That .agr indirection matters: it's exactly why the old name-matched radius table needed a separate
// rule per material. All four materials collapse to one base .adr, so resolving through the chain gets
// the same real geometry for all of them with no naming rules at all.
//
// Every lookup is cached, including negative results - most decorative props have no .cdt at all and we
// must not re-walk the asset tree for them on every placement.
public sealed class ModelCollisionLibrary
{
    private static readonly Regex AgrActorPattern = new("<Actor\\s+name=\"([^\"]+\\.adr)\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CdtReferencePattern = new("([A-Za-z0-9_\\-.]+\\.cdt)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // filename (lowercase, no ".z") -> full path on disk. Built once; the asset tree is ~100k files
    // spread over numbered folders, so per-model directory scans would be hopelessly slow.
    private readonly Dictionary<string, string> _files;
    private readonly Dictionary<string, CdtParser.CollisionMesh?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public int Resolved { get; private set; }
    public int Unresolved { get; private set; }

    public ModelCollisionLibrary(string assetsDirectory)
    {
        _files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(assetsDirectory))
            return;

        foreach (var path in Directory.EnumerateFiles(assetsDirectory, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(path);
            if (name.EndsWith(".z", StringComparison.OrdinalIgnoreCase))
                name = name[..^2];

            // Keep the first occurrence; duplicates across numbered folders are the same asset.
            _files.TryAdd(name, path);
        }
    }

    public bool Available => _files.Count > 0;

    // The collision mesh for this model, or null when the model has none (plenty of decorative props
    // genuinely ship without collision) or the chain can't be followed.
    public CdtParser.CollisionMesh? TryGet(string modelName)
    {
        if (_cache.TryGetValue(modelName, out var cached))
            return cached;

        var mesh = Resolve(modelName, depth: 0);
        _cache[modelName] = mesh;

        if (mesh is not null) Resolved++; else Unresolved++;
        return mesh;
    }

    private CdtParser.CollisionMesh? Resolve(string modelName, int depth)
    {
        if (depth > 4 || string.IsNullOrWhiteSpace(modelName))
            return null; // guard against a malformed .agr pointing in a cycle

        var bytes = ReadAsset(modelName);
        if (bytes is null)
            return null;

        // .agr - an XML ActorSet wrapping the real .adr.
        if (LooksLikeXml(bytes))
        {
            var xml = Encoding.UTF8.GetString(bytes);
            var actor = AgrActorPattern.Match(xml);
            return actor.Success ? Resolve(actor.Groups[1].Value, depth + 1) : null;
        }

        // .adr - a small binary blob with embedded asset filenames; the .cdt reference is the one we want.
        var text = Encoding.ASCII.GetString(bytes);
        var cdt = CdtReferencePattern.Match(text);
        if (!cdt.Success)
            return null;

        var cdtBytes = ReadAsset(cdt.Groups[1].Value);
        return cdtBytes is null ? null : CdtParser.Parse(cdtBytes);
    }

    private byte[]? ReadAsset(string name)
        => _files.TryGetValue(name, out var path) ? AssetCompression.ReadMaybeCompressed(path) : null;

    private static bool LooksLikeXml(ReadOnlySpan<byte> b)
    {
        foreach (var c in b[..Math.Min(64, b.Length)])
        {
            if (c == (byte)'<') return true;
            if (c is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')) return false;
        }
        return false;
    }
}
