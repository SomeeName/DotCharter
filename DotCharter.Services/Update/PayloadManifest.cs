using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotCharter.Services.Update;

/// <summary>One file of a payload, identified by content rather than by date.</summary>
/// <param name="Path">Relative to the payload root, always forward-slashed.</param>
/// <param name="Sha256">Lower-case hex.</param>
/// <param name="Size">Bytes.</param>
public sealed record PayloadFile(string Path, string Sha256, long Size);

/// <summary>
///     What a release's payload contains, file by file, so a later release can be
///     downloaded as only the parts that actually changed.
///     <para>
///         <b>Why this exists.</b> A release is one ~414 MB self-contained zip, and
///         most of it is byte-identical from version to version: the .NET runtime,
///         <c>juce_sharp.dll</c>, the bundled ffmpeg/yt-dlp. Re-downloading all of
///         it to change a few managed assemblies is the entire cost of an update.
///     </para>
///     <para>
///         <b>The manifest is what makes the full zip range-addressable</b>, and
///         that is the whole mechanism: a release publishes exactly ONE
///         payload, the updater fetches its central directory over HTTP
///         <c>Range</c>, diffs this manifest against what the install has, and
///         pulls only the members that differ (<see cref="PartialUpdateDownloader" />).
///         That works from any prior version and adds no release artifacts.
///     </para>
///     <para>
///         <b>It replaced published delta-patch assets</b> (0.41–0.42.x), which
///         only ever helped installs inside a fixed covering window — an older one
///         still took the whole 414 MB, and widening the window meant more assets
///         on every release page forever. The premise that killed the earlier
///         design ("a release asset has no addressable interior") turned out to be
///         false: GitHub answers <c>206 Partial Content</c> on the API asset url.
///     </para>
///     <para>
///         The apply step is untouched by any of this. An update is robocopied over
///         the install dir with <b>no purge</b>, so a payload of only the changed
///         files merges correctly by construction — a partial update is a smaller
///         DOWNLOAD, never a new way of writing to disk.
///     </para>
/// </summary>
/// <param name="Version">The release this describes, without a leading "v".</param>
/// <param name="Layout">
///     Must match the install's own layout or applying it strands the old
///     <c>hostfxr.dll</c> next to a new runtimeconfig — see <see cref="AppPayload" />.
/// </param>
/// <param name="Files">Every file in the payload, sorted by path.</param>
public sealed record PayloadManifest(
    string Version,
    InstallLayout Layout,
    IReadOnlyList<PayloadFile> Files)
{
    /// <summary>The asset/file name a release publishes this under.</summary>
    public const string FileName = "payload-manifest.json";

    /// <summary>
    ///     Files a partial payload must ALWAYS carry, whether or not their hash
    ///     changed.
    ///     <para>
    ///         This list is <see cref="AppPayload.Validate" />'s requirements, read
    ///         off the validator itself rather than restated — and the validator
    ///         runs against the STAGED payload, before the irreversible updater
    ///         script launches. A payload missing any of them is refused at apply
    ///         time, on the user's machine, after the download.
    ///     </para>
    ///     <para>
    ///         <b>The host pair is the non-obvious half.</b> A release that changes
    ///         only managed assemblies leaves <c>hostfxr.dll</c> and
    ///         <c>coreclr.dll</c> byte-identical, so a purely content-diffed set
    ///         would naturally omit them — and then be rejected by the
    ///         self-contained check for "missing hostfxr / coreclr". Carrying them
    ///         costs ~7 MB against a 414 MB full payload and is what makes a
    ///         partial payload applicable at all.
    ///     </para>
    /// </summary>
    public static readonly string[] AlwaysInclude =
    [
        AppPayload.ExeName,
        AppPayload.RuntimeConfigName,
        AppPayload.DepsName,
        AppPayload.HostFxrName,
        AppPayload.CoreClrName,
    ];

    /// <summary>
    ///     <see cref="AlwaysInclude" /> for the shape of <paramref name="manifest" />:
    /// a SPLIT payload is validated in <c>app/</c>, so its five are the
    ///     app's own there plus the launcher at the top. Derived from
    ///     <see cref="AppPayload.SplitPrefix" />, so the validator and this list
    ///     cannot disagree about where the files live.
    /// </summary>
    public static IReadOnlyList<string> AlwaysIncludeFor(PayloadManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!manifest.IsSplit) return AlwaysInclude;
        return [AppPayload.ExeName, .. AlwaysInclude.Select(name => AppPayload.SplitPrefix + name)];
    }

    /// <summary>Whether this manifest describes a split payload (the app in <c>app/</c>).</summary>
    [JsonIgnore]
    public bool IsSplit => Files.Any(f =>
        string.Equals(f.Path, AppPayload.SplitPrefix + AppPayload.ExeName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    ///     Where a split payload carries its own copy: inside <c>app/</c>, so the
    ///     top of the install stays the launcher, <c>voices/</c>, <c>tools/</c> and
    ///     a readme. Its paths are still relative to the install's TOP. Keeping it
    ///     out of the top folder is also what lets a v0.62 install's own manifest
    ///     survive the one-step update there, for <c>SplitInstallCleanup</c> to read.
    /// </summary>
    public const string SplitInstalledPath = AppPayload.SplitPrefix + FileName;

    /// <summary>Where an install keeps THIS manifest: <see cref="SplitInstalledPath" /> when split, else the top.</summary>
    [JsonIgnore]
    public string InstalledPath => IsSplit ? SplitInstalledPath : FileName;

    /// <summary>Whether <paramref name="relativePath" /> is a copy of the manifest itself, at either home.</summary>
    public static bool IsManifestPath(string relativePath)
        => string.Equals(relativePath, FileName, StringComparison.OrdinalIgnoreCase) ||
           string.Equals(relativePath, SplitInstalledPath, StringComparison.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Reads a manifest, or null when the text is absent or unparseable.</summary>
    public static PayloadManifest? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<PayloadManifest>(json, Json); }
        catch (JsonException) { return null; }
    }

    /// <summary>
    ///     Hashes every file under <paramref name="directory" />.
    ///     <para>
    ///         Paths are stored relative and FORWARD-SLASHED because they are
    ///         compared against zip entry names, which only ever use forward
    ///         slashes — the same reason <c>AppPayload.ResolveArchiveRoot</c>
    ///         matches that way.
    ///     </para>
    /// </summary>
    public static PayloadManifest Build(string directory, string version, Func<string, bool>? exclude = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var root = Path.GetFullPath(directory);
        var files = new List<PayloadFile>();

        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (exclude?.Invoke(relative) == true) continue;

            using var stream = File.OpenRead(path);
            files.Add(new PayloadFile(relative, Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(),
                new FileInfo(path).Length));
        }

        files.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
        return new PayloadManifest(version, AppPayload.ClassifyDirectory(root), files);
    }

    /// <summary>Index by path, case-insensitively — this is a Windows payload.</summary>
    public Dictionary<string, PayloadFile> ByPath() =>
        Files.ToDictionary(f => f.Path, f => f, StringComparer.OrdinalIgnoreCase);
}

/// <summary>What has to be shipped to turn one payload into another.</summary>
/// <param name="Changed">Present in both, different content.</param>
/// <param name="Added">Only in the new payload.</param>
/// <param name="Deleted">
///     Only in the old payload. **The apply step must remove these explicitly** —
///     an update is robocopied with NO PURGE, so a file the new release dropped
///     survives forever otherwise.
/// </param>
/// <param name="Unchanged">Identical in both; the bytes a delta does not re-send.</param>
public sealed record PayloadDelta(
    IReadOnlyList<PayloadFile> Changed,
    IReadOnlyList<PayloadFile> Added,
    IReadOnlyList<string> Deleted,
    IReadOnlyList<PayloadFile> Unchanged)
{
    /// <summary>Files a patch payload has to contain (changed + added).</summary>
    public IEnumerable<PayloadFile> ToSend => Changed.Concat(Added);

    public long BytesToSend => ToSend.Sum(f => f.Size);
    public long BytesSaved => Unchanged.Sum(f => f.Size);

    /// <summary>
    ///     Nothing to do at all. Note this is NOT the same as "no update": a
    ///     re-published release with identical content is a legitimate no-op.
    /// </summary>
    public bool IsEmpty => Changed.Count == 0 && Added.Count == 0 && Deleted.Count == 0;

    /// <summary>
    ///     Diffs two manifests. Pure and order-independent.
    ///     <para>
    ///         <see cref="PayloadManifest.AlwaysInclude" /> is forced into
    ///         <see cref="Changed" /> even when its hash matches, because the
    ///         validator refuses a payload without those three files and would
    ///         reject the patch before it could be applied.
    ///     </para>
    /// </summary>
    public static PayloadDelta Between(PayloadManifest from, PayloadManifest to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        var old = from.ByPath();
        var alwaysInclude = PayloadManifest.AlwaysIncludeFor(to);
        var changed = new List<PayloadFile>();
        var added = new List<PayloadFile>();
        var unchanged = new List<PayloadFile>();

        foreach (var file in to.Files)
        {
            if (!old.TryGetValue(file.Path, out var previous))
            {
                added.Add(file);
                continue;
            }

            var mustShip = alwaysInclude.Contains(file.Path, StringComparer.OrdinalIgnoreCase);

            if (mustShip || !string.Equals(previous.Sha256, file.Sha256, StringComparison.OrdinalIgnoreCase))
                changed.Add(file);
            else
                unchanged.Add(file);
        }

        var current = to.ByPath();
        var deleted = from.Files
            .Where(f => !current.ContainsKey(f.Path))
            .Select(f => f.Path)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new PayloadDelta(changed, added, deleted, unchanged);
    }
}
