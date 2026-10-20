// .NET 10 file-based program. Writes a release's payload-manifest.json using the
// REAL in-app type (PayloadManifest from DotCharter.Services), so what the
// release publishes and what GitHubUpdateService expects cannot drift apart.
//
//   manifest <publishDir> <version> <outManifestJson>
//       Hashes the publish output and writes payload-manifest.json.
//
// THE MANIFEST IS LOAD-BEARING. Since 0.45 there are no delta-patch assets: the
// updater fetches this file, diffs it against what the install has, and pulls
// only the differing members out of the ONE full zip over HTTP range requests
// (DotCharter.Services/Update/PartialUpdateDownloader). That works from any
// prior version and adds nothing to the release page -- which is exactly what
// the covering-patch scheme it replaced could not do, since a patch only ever
// helped installs inside a fixed window and widening it meant publishing more
// assets forever.
//
// Exit codes: 0 = ok, 1 = failure, 2 = usage.

#:project ../../DotCharter.Services/DotCharter.Services.csproj
// A file-based program defaults to trim-friendly settings, which turn OFF
// reflection-based System.Text.Json — and PayloadManifest.ToJson is exactly
// that. Without this the manifest build dies at the first Serialize call.
#:property JsonSerializerIsReflectionEnabledByDefault=true

using DotCharter.Services.Update;

if (args.Length == 0) return Usage();

return args[0] switch
{
    "manifest" when args.Length == 4 => BuildManifest(args[1], args[2], args[3]),
    _ => Usage()
};

static int Usage()
{
    Console.Error.WriteLine("usage: dotnet run BuildPayloadManifest.cs manifest <publishDir> <version> <outJson>");
    return 2;
}

static int BuildManifest(string publishDir, string version, string outJson)
{
    if (!Directory.Exists(publishDir))
    {
        Console.Error.WriteLine($"ERROR: publish directory not found: {publishDir}");
        return 1;
    }

    // The manifest ships INSIDE the payload, so it must not describe itself —
    // its own hash would change the moment it was written. A split payload (#413)
    // keeps it in app/ (PayloadManifest.SplitInstalledPath), a flat one at the top.
    var manifest = PayloadManifest.Build(publishDir, version, exclude: PayloadManifest.IsManifestPath);

    if (manifest.Layout != InstallLayout.SelfContained)
    {
        Console.Error.WriteLine(
            $"ERROR: publish output is {AppPayload.Describe(manifest.Layout)}; releases must be --self-contained.");
        return 1;
    }

    // A manifest that does not list the five files AppPayload.Validate demands
    // would let the updater assemble a payload the apply path then REFUSES — on
    // the user's machine, after the download.
    var missing = PayloadManifest.AlwaysIncludeFor(manifest)
        .Where(name => !manifest.Files.Any(f => string.Equals(f.Path, name, StringComparison.OrdinalIgnoreCase)))
        .ToList();
    if (missing.Count > 0)
    {
        Console.Error.WriteLine($"ERROR: the payload is missing {string.Join(", ", missing)}.");
        return 1;
    }

    // A split payload must still be one a v0.62 updater accepts, or no install
    // older than this layout can ever reach it (LegacyUpdaterShim).
    if (manifest.IsSplit)
    {
        var legacy = LegacyUpdaterShim.CheckLegacyUpdaterAccepts(
            name => manifest.Files.Any(f => string.Equals(f.Path, name, StringComparison.OrdinalIgnoreCase)),
            File.Exists(Path.Combine(publishDir, AppPayload.RuntimeConfigName))
                ? File.ReadAllText(Path.Combine(publishDir, AppPayload.RuntimeConfigName))
                : null);
        if (legacy != null)
        {
            Console.Error.WriteLine($"ERROR: the split payload is not one a v0.62 updater accepts: {legacy}.");
            return 1;
        }

        var expected = Path.GetFullPath(Path.Combine(publishDir, PayloadManifest.SplitInstalledPath));
        if (!string.Equals(Path.GetFullPath(outJson), expected, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"ERROR: a split payload carries its manifest at {PayloadManifest.SplitInstalledPath}, not {outJson}.");
            return 1;
        }
    }

    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outJson))!);
    File.WriteAllText(outJson, manifest.ToJson());
    Console.WriteLine($"manifest: {manifest.Files.Count:N0} files, " +
                      $"{manifest.Files.Sum(f => f.Size) / (1024.0 * 1024.0):N0} MB, v{manifest.Version}");
    return 0;
}
