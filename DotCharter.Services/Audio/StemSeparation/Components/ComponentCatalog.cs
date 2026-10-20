using DotCharter.Services.Infrastructure.Features;

namespace DotCharter.Services.Audio.StemSeparation.Components;

/// <summary>
/// Pure factory that turns the runtime environment (models directory, active
/// model id, the stem-model catalog, the app base directory) into the flat list
/// of <see cref="ComponentDefinition"/>s the <see cref="ComponentInventory"/>
/// scans. No I/O, no statics — everything it needs is passed in, so it is fully
/// unit-testable.
/// </summary>
public static class ComponentCatalog
{
    /// <summary>
    /// Build definitions for every known stem model plus the external media
    /// tools DotCharter can make use of.
    /// </summary>
    /// <param name="modelsDirectory">Directory htdemucs / ONNX weights live in.</param>
    /// <param name="activeModelId">Currently-selected active model id (may be empty).</param>
    /// <param name="models">The stem-model catalog (built-ins + custom).</param>
    /// <param name="appBaseDirectory">The app executable directory (for bundled tools).</param>
    public static IReadOnlyList<ComponentDefinition> Build(
        string modelsDirectory,
        string? activeModelId,
        IReadOnlyList<StemModelDescriptor> models,
        string appBaseDirectory)
    {
        if (models == null) throw new ArgumentNullException(nameof(models));
        var defs = new List<ComponentDefinition>();

        foreach (var m in models)
            defs.Add(FromModel(m, modelsDirectory, activeModelId));

        defs.AddRange(ExternalTools(appBaseDirectory));
        return defs;
    }

    internal static ComponentDefinition FromModel(
        StemModelDescriptor m, string modelsDirectory, string? activeModelId)
    {
        var isActive = !string.IsNullOrWhiteSpace(activeModelId)
                       && string.Equals(activeModelId, m.Id, StringComparison.OrdinalIgnoreCase);
        return new ComponentDefinition
        {
            Id = m.Id,
            DisplayName = m.DisplayName,
            Description = m.Description,
            Category = ComponentCategory.StemModel,
            FileNames = new[] { m.FileName },
            SearchDirectories = new[] { modelsDirectory },
            SearchPath = false,
            IsActiveSelection = isActive,
            ExpectedBytes = m.ApproxBytes,
            // Download exists as a catalog capability but is intentionally not
            // wired up in this pass — the manager surfaces it disabled.
            Downloadable = true,
            Note = "Not installed. Download from Settings → Stem Models, or place the " +
                   $"file '{m.FileName}' in the models folder.",
        };
    }

    /// <summary>
    /// External command-line tools. These are LIST-ONLY: DotCharter uses them
    /// when present but never installs them, so they are never downloadable.
    /// </summary>
    internal static IReadOnlyList<ComponentDefinition> ExternalTools(string appBaseDirectory) =>
        ExternalTools(appBaseDirectory, ContestedFeatures.Enabled);

    /// <summary>
    ///     The same list for a build with the public-build switch <paramref name="contestedFeatures" />:
    ///     off, the yt-dlp row is not listed at all.
    /// </summary>
    internal static IReadOnlyList<ComponentDefinition> ExternalTools(string appBaseDirectory, bool contestedFeatures)
    {
        var toolsDir = string.IsNullOrWhiteSpace(appBaseDirectory)
            ? null
            : Path.Combine(appBaseDirectory, "tools");
        var appDirs = new List<string>();
        if (!string.IsNullOrWhiteSpace(appBaseDirectory)) appDirs.Add(appBaseDirectory);
        if (toolsDir != null) appDirs.Add(toolsDir);

        var tools = new List<ComponentDefinition>
        {
            new()
            {
                Id = "tool-ffmpeg",
                DisplayName = "ffmpeg",
                Description = "Audio transcoder. When present, separated stems are cached as Opus " +
                              "(much smaller than WAV) and audio imports can be converted. Optional — " +
                              "without it stems fall back to WAV.",
                Category = ComponentCategory.MediaTool,
                FileNames = new[] { "ffmpeg.exe", "ffmpeg" },
                SearchDirectories = appDirs,
                SearchPath = true,
                Downloadable = false,
                Note = "Not found. Install ffmpeg on your PATH, or drop ffmpeg.exe into the app 'tools' folder.",
            },
        };
        if (!contestedFeatures) return tools;

        tools.Add(
            new()
            {
                Id = "tool-yt-dlp",
                DisplayName = "yt-dlp",
                Description = "YouTube / media downloader used by the audio import flow to fetch a " +
                              "song's audio. Optional — only needed for URL-based audio import.",
                Category = ComponentCategory.MediaTool,
                FileNames = new[] { "yt-dlp.exe", "yt-dlp" },
                SearchDirectories = appDirs,
                SearchPath = true,
                Downloadable = false,
                Note = "Not found. Install yt-dlp on your PATH, or drop yt-dlp.exe into the app 'tools' folder.",
            });
        return tools;
    }
}
