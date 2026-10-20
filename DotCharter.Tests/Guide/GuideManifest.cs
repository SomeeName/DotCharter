using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotCharter.Tests.Guide;

/// <summary>
///     The usage guide's manifests (<c>docs/guide/manifests/*.json</c>, #60): one per guide page,
///     listing each screenshot — the scene to render, its arguments and, for every numbered
///     callout, the control it points at and the sentence that explains it. The capture run
///     (<c>Perf/Guide/GuideCaptureUiPerfSmokeTests</c>) draws the markers from it and the same
///     text becomes the page's numbered list, so the picture and the words come from one source.
///     Pure: the default gate reads it through <see cref="GuideManifestLintTests" />.
/// </summary>
internal static class GuideManifest
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static string GuideRoot => Path.Combine(TestPaths.RepoRoot(), "docs", "guide");

    public static string ImageRoot => Path.Combine(GuideRoot, "images");

    public static IReadOnlyList<GuidePage> LoadAll()
    {
        var dir = Path.Combine(GuideRoot, "manifests");
        return [.. Directory.GetFiles(dir, "*.json").Order(StringComparer.Ordinal).Select(Load)];
    }

    public static GuidePage Load(string path) =>
        JsonSerializer.Deserialize<GuidePage>(File.ReadAllText(path), Json)
        ?? throw new InvalidDataException($"{path} is empty");

    public static string BlockStart(GuideShot shot) => $"<!-- guide:callouts {shot.Id} -->";

    public const string BlockEnd = "<!-- /guide:callouts -->";

    /// <summary>The numbered list under a screenshot, exactly as the page must carry it.</summary>
    public static string RenderList(GuideShot shot)
    {
        var text = new StringBuilder();
        text.Append(BlockStart(shot)).Append('\n');
        for (var i = 0; i < shot.Callouts.Count; i++)
        {
            var callout = shot.Callouts[i];
            text.Append(i + 1).Append(". **").Append(callout.Title).Append(".** ").Append(callout.Text).Append('\n');
        }

        text.Append(BlockEnd);
        return text.ToString();
    }

    /// <summary>The block between the shot's markers in <paramref name="markdown" />, markers included; null when absent.</summary>
    public static string? FindBlock(string markdown, GuideShot shot)
    {
        var start = markdown.IndexOf(BlockStart(shot), StringComparison.Ordinal);
        if (start < 0) return null;
        var end = markdown.IndexOf(BlockEnd, start, StringComparison.Ordinal);
        return end < 0 ? null : markdown[start..(end + BlockEnd.Length)].Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    /// <summary><paramref name="markdown" /> with every shot's block replaced by its rendering.</summary>
    public static string Rewrite(string markdown, GuidePage page)
    {
        var text = markdown.Replace("\r\n", "\n", StringComparison.Ordinal);
        foreach (var shot in page.Shots)
        {
            var old = FindBlock(text, shot) ?? throw new InvalidDataException($"{page.Page} has no '{BlockStart(shot)}' block");
            text = text.Replace(old, RenderList(shot), StringComparison.Ordinal);
        }

        return text;
    }
}

/// <summary>One guide page: its Markdown file (under <c>docs/guide/</c>) and its screenshots in order.</summary>
internal sealed record GuidePage(string Page, IReadOnlyList<GuideShot> Shots);

/// <summary>
///     One screenshot. <see cref="Scene" /> is a key of <see cref="GuideScenes.Known" />;
///     <see cref="Args" /> are that scene's own options. <see cref="Crop" /> names controls whose
///     union (with every callout target and every open popup) the picture is cut to; empty keeps
///     the whole window.
/// </summary>
internal sealed record GuideShot(
    string Id,
    string Scene,
    IReadOnlyList<GuideCallout> Callouts,
    IReadOnlyDictionary<string, string>? Args = null,
    int Width = 1600,
    int Height = 900,
    IReadOnlyList<string>? Crop = null)
{
    public string Image => Id + ".png";

    public string Arg(string key, string fallback = "") =>
        Args is not null && Args.TryGetValue(key, out var value) ? value : fallback;
}

/// <summary>
///     One numbered marker. <see cref="Name" /> is the target's <c>x:Name</c>; a menu row has
///     none, so it is found by <see cref="Loc" />, the localization key of its header (or of a
///     button's tooltip). Exactly one of the two is set. <see cref="Side" /> (left, right, top,
///     bottom or inside) pins the marker to that side of its control when the automatic placement reads
///     ambiguously, as it does down a column of same-sized rows.
/// </summary>
internal sealed record GuideCallout(string Title, string Text, string? Name = null, string? Loc = null, string? Side = null);

/// <summary>The scenes the capture run knows how to build. A manifest naming another fails the gate.</summary>
internal static class GuideScenes
{
    public const string Editor = "editor";
    public const string Library = "library";
    public const string PlayMode = "playmode";
    public const string Inspector = "inspector";
    public const string Rig = "rig";
    public const string RigLab = "riglab";
    public const string Settings = "settings";

    public static IReadOnlySet<string> Known { get; } =
        new HashSet<string>(StringComparer.Ordinal) { Editor, Library, PlayMode, Inspector, Rig, RigLab, Settings };
}
