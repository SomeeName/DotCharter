using System.Text.RegularExpressions;

namespace DotCharter.Tests.UI;

/// <summary>
///     0.24 note-inspector icon-chip rework: guards the wiring between the
///     technique glyph geometry (<c>Styles/Icons.axaml</c>) and the chips that
///     reference it (<c>SoundNoteTechniqueView.axaml</c>, the Sound tab's note card). A source-lint (no Avalonia
///     app boot — the full session cannot share a testhost with the gate, see
///     the Tests AGENTS guide), same shape as <see cref="PowerToolReachabilityTests" />.
///     It catches the two typo classes that would otherwise only surface as a
///     blank glyph or a hard XAML-load crash at runtime:
///     <list type="bullet">
///         <item>a missing / mis-keyed <c>IconTech*</c> resource, and</item>
///         <item>a chip pointing at an icon key that no longer exists.</item>
///     </list>
/// </summary>
public sealed class NoteTechniqueIconReachabilityTests
{
    // Every technique glyph the 0.24 rework introduced. Each must exist in
    // Icons.axaml with non-empty geometry.
    private static readonly string[] TechniqueIconKeys =
    [
        "IconTechMute", "IconTechPalmMute", "IconTechHammerOn", "IconTechPullOff",
        "IconTechTap", "IconTechSlideUp", "IconTechSlideDown", "IconTechSlide",
        "IconTechBend", "IconTechTremolo", "IconTechVibrato", "IconTechLinkNext",
        "IconTechLetRing", "IconTechHarmonic", "IconTechHarmonicPinch",
        "IconTechAccent", "IconTechSlap", "IconTechPop", "IconTechPassOther",
        "IconTechIgnore"
    ];

    // The icon-based enum segment pickers of the note's Technique section. Each
    // entry's icon must be referenced by it and resolvable in Icons.axaml. The
    // binary techniques are worded chips since the #601 dense rows.
    private static readonly string[] ChipIconKeys =
    [
        "IconTechMute", "IconTechPalmMute", "IconTechHammerOn", "IconTechPullOff",
        "IconTechTap", "IconTechHarmonic", "IconTechHarmonicPinch",
        "IconTechPop", "IconTechSlap"
    ];

    [Fact]
    public void EveryTechniqueIcon_IsDefinedWithGeometry()
    {
        var defined = DefinedIconGeometry();
        foreach (var key in TechniqueIconKeys)
        {
            Assert.True(defined.TryGetValue(key, out var data),
                $"Icons.axaml is missing the '{key}' technique glyph.");
            Assert.False(string.IsNullOrWhiteSpace(data),
                $"Icons.axaml '{key}' has empty geometry.");
            // Every glyph must start with an SVG-path move command (optionally a
            // fill-rule prefix) — catches a truncated / non-geometry value.
            Assert.Matches(@"^(F[01]\s*)?M", data!.Trim());
        }
    }

    [Fact]
    public void EveryChipIcon_ReferencedByNoteTab_ResolvesInIcons()
    {
        var defined = DefinedIconGeometry();
        // The note Technique card is its own view since #572.
        var view = Read("DotCharter.UI", "Views", "Editor", "Inspector", "SoundNoteTechniqueView.axaml");

        foreach (var key in ChipIconKeys)
        {
            Assert.True(defined.ContainsKey(key),
                $"Note tab references icon '{key}' but Icons.axaml does not define it.");
            // DynamicResource since 2026-08-11 (UI/IconResourceTests bans the
            // Static form across Views - it throws when the view is built
            // outside the app dictionary's scope).
            Assert.Contains($"{{DynamicResource {key}}}", view);
        }
    }

    [Fact]
    public void NoteTab_HasNoDanglingIconReferences()
    {
        var defined = DefinedIconGeometry();
        // The whole Sound tab: the view and every section view it hosts (#601).
        var dir = Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Editor", "Inspector");
        var files = Directory.GetFiles(dir, "Sound*View.axaml").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        Assert.True(files.Count >= 9, $"only {files.Count} Sound tab views found — the lint went blind.");
        var view = string.Concat(files.Select(f => Read("DotCharter.UI", "Views", "Editor", "Inspector", f!)));

        foreach (Match m in Regex.Matches(view, @"\{(?:StaticResource|DynamicResource) (Icon\w+)\}"))
        {
            var key = m.Groups[1].Value;
            Assert.True(defined.ContainsKey(key),
                $"The Sound tab references '{key}' which is not defined in Icons.axaml.");
        }
    }

    private static Dictionary<string, string> DefinedIconGeometry()
    {
        var icons = Read("DotCharter.Tone.UI", "Styles", "Icons.axaml");
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(icons,
                     @"x:Key=""(Icon\w+)""\s*>([^<]*)</StreamGeometry>"))
        {
            map[m.Groups[1].Value] = m.Groups[2].Value;
        }

        return map;
    }

    private static string Read(params string[] relativeParts)
    {
        var path = Path.Combine(new[] { TestPaths.RepoRoot() }.Concat(relativeParts).ToArray());
        Assert.True(File.Exists(path), $"source file not found at {path}");
        return UiSourceTree.ReadAllText(path);
    }
}
