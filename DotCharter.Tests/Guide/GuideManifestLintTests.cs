using System.Text.RegularExpressions;

namespace DotCharter.Tests.Guide;

/// <summary>
///     The usage guide (<c>docs/guide/</c>, #60) cannot rot silently. A renamed control would make
///     the capture run lose its arrow, so every <c>x:Name</c> a manifest points at must still be
///     declared in the product's XAML, and every menu row it finds by header must still be on a
///     menu. The numbered list under each screenshot must be the manifest's own text, the
///     picture must be in the folder, and the pages ship publicly, so they carry nothing private.
/// </summary>
public sealed partial class GuideManifestLintTests
{
    private const int MaxImageBytes = 700 * 1024;

    /// <summary>The projects whose views the guide photographs.</summary>
    private static readonly string[] ViewProjects = ["DotCharter.UI", "DotCharter.Tone.UI", "DotCharter.RigLab"];

    /// <summary>What a user guide must not explain: reading the game's own files. Repository names,
    /// add-on paths and machine paths are the public export's leak check and
    /// <c>Infrastructure/PersonalDataLintTests</c>, over the whole tree.</summary>
    private static readonly string[] PrivateWords = ["psarc", "decrypt"];

    private static readonly Lazy<string[]> AxamlTexts = new(() =>
    [
        .. ViewProjects.SelectMany(p => Directory.EnumerateFiles(Path.Combine(TestPaths.RepoRoot(), p), "*.axaml", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(File.ReadAllText)
    ]);

    [GeneratedRegex("""(?<![.\w])(?:x:)?Name="([A-Za-z_]\w*)(?=")""")]
    private static partial Regex NameAttribute();

    [GeneratedRegex("""Loc ([A-Za-z0-9_]+)(?=\})""")]
    private static partial Regex LocKey();

    internal static IReadOnlySet<string> DeclaredNames() =>
        AxamlTexts.Value.SelectMany(t => NameAttribute().Matches(t).Select(m => m.Groups[1].Value)).ToHashSet(StringComparer.Ordinal);

    internal static IReadOnlySet<string> MarkupLocKeys() =>
        AxamlTexts.Value.SelectMany(t => LocKey().Matches(t).Select(m => m.Groups[1].Value)).ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void ThereAreFivePages_EachWithShots()
    {
        var pages = GuideManifest.LoadAll();
        Assert.Equal(
            ["charting.md", "getting-started.md", "playing.md", "settings.md", "tone-rig.md"],
            pages.Select(p => p.Page).Order(StringComparer.Ordinal));
        Assert.All(pages, p => Assert.NotEmpty(p.Shots));
        var ids = pages.SelectMany(p => p.Shots).Select(s => s.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void EveryCalloutTarget_StillExistsInTheXaml()
    {
        var names = DeclaredNames();
        var keys = MarkupLocKeys();
        var missing = new List<string>();
        foreach (var page in GuideManifest.LoadAll())
        {
            foreach (var shot in page.Shots)
            {
                foreach (var callout in shot.Callouts)
                {
                    if ((callout.Name is null) == (callout.Loc is null))
                        missing.Add($"{shot.Id}: '{callout.Title}' must set exactly one of name / loc");
                    else if (callout.Name is { } name && !names.Contains(name))
                        missing.Add($"{shot.Id}: x:Name '{name}' ('{callout.Title}') is declared in no .axaml");
                    else if (callout.Loc is { } key && !keys.Contains(key))
                        missing.Add($"{shot.Id}: no view's markup uses the header/tooltip key '{key}' ('{callout.Title}')");
                }

                foreach (var name in shot.Crop ?? [])
                {
                    if (!names.Contains(name))
                        missing.Add($"{shot.Id}: crop target '{name}' is declared in no .axaml");
                }
            }
        }

        Assert.True(missing.Count == 0, "A guide manifest points at a control that is gone — rename it in docs/guide/manifests:\n" + string.Join('\n', missing));
    }

    [Fact]
    public void EveryShot_UsesAKnownScene_AndHasCallouts()
    {
        foreach (var shot in GuideManifest.LoadAll().SelectMany(p => p.Shots))
        {
            Assert.True(GuideScenes.Known.Contains(shot.Scene), $"{shot.Id}: unknown scene '{shot.Scene}'");
            Assert.NotEmpty(shot.Callouts);
            Assert.True(shot.Callouts.Count <= 20, $"{shot.Id}: {shot.Callouts.Count} callouts is more than one picture can carry");
            Assert.All(shot.Callouts, c => Assert.False(string.IsNullOrWhiteSpace(c.Title) || string.IsNullOrWhiteSpace(c.Text), $"{shot.Id}: an empty callout"));
            Assert.All(shot.Callouts, c => Assert.True(c.Side is null or "left" or "right" or "top" or "bottom" or "inside", $"{shot.Id}: side '{c.Side}'"));
        }
    }

    [Fact]
    public void EveryPage_CarriesEachShotsImage_AndItsNumberedList()
    {
        foreach (var page in GuideManifest.LoadAll())
        {
            var path = Path.Combine(GuideManifest.GuideRoot, page.Page);
            Assert.True(File.Exists(path), $"{page.Page} is missing");
            var markdown = File.ReadAllText(path);
            foreach (var shot in page.Shots)
            {
                Assert.Contains($"](images/{shot.Image})", markdown, StringComparison.Ordinal);
                var block = GuideManifest.FindBlock(markdown, shot);
                Assert.True(block is not null, $"{page.Page}: no '{GuideManifest.BlockStart(shot)}' … '{GuideManifest.BlockEnd}' block");
                Assert.True(block == GuideManifest.RenderList(shot),
                    $"{page.Page}: the numbered list for {shot.Id} is not the manifest's — re-run the capture (docs/guide/README.md) or edit the manifest");
            }
        }
    }

    [Fact]
    public void EveryImage_IsThere_AndReasonablySized()
    {
        var shots = GuideManifest.LoadAll().SelectMany(p => p.Shots).ToList();
        foreach (var shot in shots)
        {
            var file = new FileInfo(Path.Combine(GuideManifest.ImageRoot, shot.Image));
            Assert.True(file.Exists, $"docs/guide/images/{shot.Image} is missing — run the capture (docs/guide/README.md)");
            Assert.True(file.Length <= MaxImageBytes, $"{shot.Image} is {file.Length / 1024} KiB; crop or scale it below {MaxImageBytes / 1024} KiB");
        }

        var expected = shots.Select(s => s.Image).ToHashSet(StringComparer.Ordinal);
        var strays = Directory.GetFiles(GuideManifest.ImageRoot, "*.png").Select(Path.GetFileName).Where(f => !expected.Contains(f!)).ToList();
        Assert.True(strays.Count == 0, "docs/guide/images holds pictures no manifest makes: " + string.Join(", ", strays));
    }

    [Fact]
    public void TheGuide_ShipsNothingPrivate()
    {
        var files = Directory.EnumerateFiles(GuideManifest.GuideRoot, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".md", StringComparison.Ordinal) || f.EndsWith(".json", StringComparison.Ordinal));
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            foreach (var word in PrivateWords)
                Assert.False(text.Contains(word, StringComparison.OrdinalIgnoreCase), $"{Path.GetFileName(file)} mentions '{word}', which does not ship publicly");
        }
    }
}
