// .NET 10 file-based program (run with `dotnet run ValidateReleaseZip.cs <zip> [expectedRoot]`).
// Validates a DotCharter release zip with the REAL in-app updater rules
// (AppPayload from DotCharter.Services) so what we upload is exactly what
// GitHubUpdateService will accept on users' machines.
//
// Exit codes: 0 = valid, 1 = invalid payload, 2 = usage error.

#:project ../../DotCharter.Services/DotCharter.Services.csproj

using System.IO.Compression;
using DotCharter.Services.Update;

if (args.Length is < 1 or > 2)
{
    Console.Error.WriteLine("usage: dotnet run ValidateReleaseZip.cs <zip> [expectedRootPrefix]");
    return 2;
}

var zipPath = args[0];
var expectedRoot = args.Length > 1 ? args[1] : null;

if (!File.Exists(zipPath))
{
    Console.Error.WriteLine($"INVALID: file not found: {zipPath}");
    return 1;
}

using var archive = ZipFile.OpenRead(zipPath);

// AppPayload.ResolveArchiveRoot matches "<folder>/DotCharter.exe" with forward
// slashes only; a backslash entry name means the zip was built with a tool
// (e.g. PowerShell Compress-Archive) the updater rejects. Fail loudly with the
// actual cause instead of the downstream "no DotCharter.exe".
var backslashEntry = archive.Entries.FirstOrDefault(e => e.FullName.Contains('\\'));
if (backslashEntry != null)
{
    Console.Error.WriteLine(
        $"INVALID: entry '{backslashEntry.FullName}' uses backslash separators " +
        "(Compress-Archive output?). The updater only understands '/' — build the zip with " +
        "ZipFile.CreateFromDirectory (New-Release.ps1 does).");
    return 1;
}

var root = AppPayload.ResolveArchiveRoot(archive);
Console.WriteLine($"archive root: '{root}' ({archive.Entries.Count} entries)");

var problem = AppPayload.ValidateArchive(archive, InstallLayout.SelfContained);
if (problem != null)
{
    Console.Error.WriteLine($"INVALID: {problem}");
    return 1;
}

// A SPLIT payload (#413) is validated in app/ above. It must ALSO pass the flat
// rule every v0.62-and-older updater applies at the payload root, or none of those
// installs can update to it: that is what LegacyUpdaterShim's four top-level
// copies are for. And it carries its manifest in app/, where the updater reads it.
var names = archive.Entries.Select(e => e.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
if (names.Contains(root + AppPayload.SplitPrefix + AppPayload.ExeName))
{
    string? rootRuntimeConfig = null;
    var configEntry = archive.GetEntry(root + AppPayload.RuntimeConfigName);
    if (configEntry != null)
    {
        using var reader = new StreamReader(configEntry.Open());
        rootRuntimeConfig = reader.ReadToEnd();
    }

    var legacy = LegacyUpdaterShim.CheckLegacyUpdaterAccepts(name => names.Contains(root + name), rootRuntimeConfig);
    if (legacy != null)
    {
        Console.Error.WriteLine($"INVALID: a v0.62 updater would refuse this split payload: {legacy}.");
        return 1;
    }

    if (!names.Contains(root + PayloadManifest.SplitInstalledPath))
    {
        Console.Error.WriteLine($"INVALID: the split payload has no {PayloadManifest.SplitInstalledPath}.");
        return 1;
    }

    Console.WriteLine("split layout: app/ validated, and the top passes the v0.62 updater's flat rule.");
}

if (expectedRoot != null && !string.Equals(root, expectedRoot, StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine($"INVALID: archive root is '{root}' but expected '{expectedRoot}' " +
                            "(the release zip must wrap everything in one version-named folder).");
    return 1;
}

// Bundled media tools live under <root>tools/. They are OPTIONAL to the updater
// (the app degrades without them), so a missing tool is a WARNING, not a failure —
// but a normal release should ship all three. New-Release.ps1 -SkipTools skips them.
foreach (var tool in new[] { "ffmpeg.exe", "yt-dlp.exe" })
{
    var entryName = $"{root}tools/{tool}";
    var present = archive.Entries.Any(e => string.Equals(e.FullName, entryName, StringComparison.OrdinalIgnoreCase));
    if (!present)
        Console.WriteLine($"WARNING: bundled media tool 'tools/{tool}' is not in the zip " +
                          "(stem Opus encoding / YouTube import will need it on PATH). " +
                          "Expected unless the pack used -SkipTools.");
}

Console.WriteLine("OK: self-contained DotCharter payload, folder-wrapped, updater-compatible.");
return 0;
