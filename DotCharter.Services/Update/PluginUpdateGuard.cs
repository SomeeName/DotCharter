namespace DotCharter.Services.Update;

/// <summary>
/// Keeps an install that carries plugins off releases without them. A plugin
///     (<see cref="Plugins.PluginLoader" />) never ships in a public asset:
///     <c>tools/release/New-Release.ps1</c> publishes with <c>-p:DotCharterPlugins=false</c>
///     and refuses a payload that holds one. So an install that HAS a plugin and is offered a
///     payload WITHOUT it is a plugin build looking at the public feed. Applying it would drop
///     the plugin: the new <c>deps.json</c> and the new host's declarations do not name it, so
///     it is never loaded again, and a partial update even deletes it (it is missing from the
///     new manifest).
///     <para>
///         The plugins an install carries are the files of the plugins the running app declares
///         (<see cref="Plugins.PluginLoader.DeclaredFiles" />) that sit in its app folder. A payload
///         "carries" one when it holds the file or when its own <c>payload-manifest.json</c>
///         lists it: a partial payload ships only the files that changed, and its manifest is
///         the whole release.
///     </para>
/// </summary>
public static class PluginUpdateGuard
{
    /// <summary>The <paramref name="pluginFiles" /> present in the app folder of the install at
    /// <paramref name="installDirectory" /> (its top).</summary>
    public static IReadOnlyList<string> InstalledPlugins(string installDirectory, IReadOnlyList<string> pluginFiles)
    {
        var appDirectory = AppPayload.ResolveAppDirectory(installDirectory);
        return [.. pluginFiles.Where(f => File.Exists(Path.Combine(appDirectory, f)))];
    }

    /// <summary>The <paramref name="pluginFiles" /> <paramref name="manifest" /> does NOT list, at the top or in <c>app/</c>.</summary>
    public static IReadOnlyList<string> MissingFromManifest(PayloadManifest manifest, IReadOnlyList<string> pluginFiles)
        => [.. pluginFiles.Where(p => !manifest.Files.Any(f => IsPluginPath(f.Path, p)))];

    /// <summary>
    ///     The <paramref name="pluginFiles" /> the extracted payload at
    ///     <paramref name="payloadDirectory" /> does NOT carry: neither the file itself nor a
    ///     payload manifest that lists it.
    /// </summary>
    public static IReadOnlyList<string> MissingFromPayload(string payloadDirectory, IReadOnlyList<string> pluginFiles)
    {
        var appDirectory = AppPayload.ResolveAppDirectory(payloadDirectory);
        var missing = pluginFiles.Where(f => !File.Exists(Path.Combine(appDirectory, f))).ToList();
        if (missing.Count == 0) return [];

        foreach (var relative in new[] { PayloadManifest.SplitInstalledPath, PayloadManifest.FileName })
        {
            var path = Path.Combine(payloadDirectory, relative);
            if (!File.Exists(path)) continue;
            var manifest = PayloadManifest.FromJson(File.ReadAllText(path));
            if (manifest != null) return MissingFromManifest(manifest, missing);
        }

        return missing;
    }

    /// <summary>
    ///     Null when the update may proceed; otherwise why not. Only an install WITH a plugin
    ///     can be refused, and only for a plugin the payload lacks: a public install never had
    ///     one, and gaining one is not this guard's call.
    /// </summary>
    public static string? Check(IReadOnlyList<string> missingPlugins, string? releaseName = null)
    {
        if (missingPlugins.Count == 0) return null;

        var what = string.IsNullOrWhiteSpace(releaseName) ? "this update" : releaseName;
        return $"this install carries plugins ({string.Join(", ", missingPlugins)}) and {what} is a build " +
               "without them - installing it would remove their features. Update this install from a build " +
               "that carries them instead";
    }

    private static bool IsPluginPath(string relativePath, string pluginFile)
        => string.Equals(relativePath, pluginFile, StringComparison.OrdinalIgnoreCase) ||
           string.Equals(relativePath, AppPayload.SplitPrefix + pluginFile, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
///     <see cref="PluginUpdateGuard" />'s refusal, raised where the download would start. It
///     passes through the partial path's catch-all: a refused update must not fall back to
///     downloading the full asset only to be refused again on apply.
/// </summary>
public sealed class PluginUpdateRefusedException(string message) : IOException(message);
