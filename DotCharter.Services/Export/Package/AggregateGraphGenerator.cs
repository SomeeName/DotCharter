using System.Security.Cryptography;
using System.Text;

namespace DotCharter.Services.Export.Package;

/// <summary>
/// Generates the aggregate graph ("{key}_aggregategraph.nt") that maps every
/// asset in the package to its canonical location and tags.
/// Ported from Rocksmith2014.NET AggregateGraph.fs / GraphItem.fs, both platforms.
/// </summary>
internal static class AggregateGraphGenerator
{
    private const string LineTemplate = "<urn:uuid:{0}> <http://emergent.net/aweb/1.0/{1}> \"{2}\".";

    private sealed record GraphItem(
        Guid Uuid,
        Guid? Llid,
        string[] Tags,
        string Canonical,
        string Name,
        string RelPath,
        string? LogPath);

    /// <summary>The graph for <paramref name="meta" />'s package; <paramref name="pinnedIds" />,
    /// one uuid (and llid) per item in graph order, pins the ids for a byte-for-byte
    /// rebuild, and null draws fresh ones. <paramref name="platform" /> (PC when null)
    /// places the SNGs and banks and tags them.</summary>
    public static byte[] Create(RsPackageMetadata meta, IReadOnlyList<RsPackageArrangement> arrangements,
        IReadOnlyList<RsGraphItemIdentity>? pinnedIds = null, RsPlatformLayout? platform = null)
    {
        platform ??= RsPlatformLayout.Pc;
        var dlcName = meta.DlcKey.ToLowerInvariant();
        var items = new List<GraphItem>
        {
            Normal(dlcName, "/gamexblocks/nsongs", "xblock", ["emergent-world", "x-world"])
        };

        foreach (var arrangement in arrangements)
        {
            var name = $"{dlcName}_{arrangement.PartitionName}";
            items.Add(Normal(name, $"/manifests/songs_dlc_{dlcName}", "json", ["database", "json-db"]));
            items.Add(Sng(name, platform));
        }

        // Showlights XML.
        items.Add(WithLlid($"{dlcName}_showlights", "/songs/arr", "xml", ["application", "xml"]));

        items.Add(Normal($"songs_dlc_{dlcName}", $"/manifests/songs_dlc_{dlcName}", "hsan",
            ["database", "hsan-db"]));

        foreach (var size in new[] { 64, 128, 256 })
            items.Add(WithLlid($"album_{dlcName}_{size}", "/gfxassets/album_art", "dds", ["dds", "image"]));

        items.Add(Bnk($"song_{dlcName}", platform));
        items.Add(Bnk($"song_{dlcName}_preview", platform));

        if (pinnedIds != null)
        {
            if (pinnedIds.Count != items.Count)
                throw new ArgumentException($"{pinnedIds.Count} pinned graph ids for {items.Count} items.", nameof(pinnedIds));
            items = [.. items.Select((item, i) => item with
            {
                Uuid = pinnedIds[i].Uuid,
                Llid = item.Llid == null ? null : pinnedIds[i].Llid ?? item.Llid
            })];
        }

        using var memory = new MemoryStream();
        using (var writer = new StreamWriter(memory, Encoding.UTF8, -1, true) { NewLine = "\n" })
        {
            for (var i = 0; i < items.Count; i++)
            {
                if (i != 0) writer.WriteLine();
                Write(writer, items[i]);
            }
        }

        return memory.ToArray();
    }

    private static Guid NewLlid() => new(RandomNumberGenerator.GetInt32(int.MaxValue), 0, 0, new byte[8]);

    private static GraphItem Make(string name, string canonical, string[] tags, string relPath, string? logPath)
    {
        return new GraphItem(
            Guid.NewGuid(),
            logPath != null ? NewLlid() : null,
            tags,
            canonical,
            name,
            relPath,
            logPath);
    }

    private static GraphItem Normal(string name, string canonical, string extension, string[] tags) => Make(name, canonical, tags, $"{canonical}/{name}.{extension}", null);

    private static GraphItem WithLlid(string name, string canonical, string extension, string[] tags)
    {
        var path = $"{canonical}/{name}.{extension}";
        return Make(name, canonical, tags, path, path);
    }

    /// <summary>The Mac SNG carries the platform tag, the PC one none (GraphItem.fs <c>sng</c>).</summary>
    private static GraphItem Sng(string name, RsPlatformLayout platform)
    {
        var canonical = $"/songs/bin/{platform.SngFolder}";
        return Make(
            name,
            canonical,
            platform.IsMac ? ["application", "musicgame-song", platform.PlatformTag] : ["application", "musicgame-song"],
            $"{canonical}/{name}.sng",
            $"/songs/bin/{name}.sng");
    }

    private static GraphItem Bnk(string name, RsPlatformLayout platform)
    {
        var canonical = $"/audio/{platform.AudioFolder}";
        return Make(
            name,
            canonical,
            ["audio", "wwise-sound-bank", platform.PlatformTag],
            $"{canonical}/{name}.bnk",
            $"/audio/{name}.bnk");
    }

    private static void Write(StreamWriter writer, GraphItem item)
    {
        var uuid = item.Uuid.ToString();

        foreach (var tag in item.Tags)
            writer.WriteLine(LineTemplate, uuid, "tag", tag);

        writer.WriteLine(LineTemplate, uuid, "canonical", item.Canonical);
        writer.WriteLine(LineTemplate, uuid, "name", item.Name);

        if (item.Llid is { } llid && item.LogPath is { } logPath)
        {
            writer.WriteLine(LineTemplate, uuid, "llid", llid);
            writer.WriteLine(LineTemplate, uuid, "logpath", logPath);
        }

        writer.Write(LineTemplate, uuid, "relpath", item.RelPath);
    }
}
