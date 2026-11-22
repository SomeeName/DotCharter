using System.Text.RegularExpressions;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     #373 / #376: the neck limits (<c>FretLimits</c>) and the string-pitch formula
///     (<c>SoundingPitch</c>) each have ONE definition. A product file that re-declares the
///     24-fret literal, or re-derives a string's MIDI note from the open-string table, is the
///     drift that left generated anchors on a different ceiling than hand-placed ones.
/// </summary>
public sealed partial class SingleSourceFretAndPitchLintTests
{
    /// <summary>Files that may name <c>GuitarLowEMidi</c>. The list only shrinks.</summary>
    private static readonly Dictionary<string, string> PitchFormulaAllowed = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DotCharter.Core/Utilities/SoundingPitch.cs"] = "THE pitch rule.",
        ["DotCharter.Core/Utilities/StandardOpenStrings.cs"] = "Defines the constant.",
        ["DotCharter.Core/Utilities/TuningUtilities.cs"] = "Derives tuning NAMES and offsets from the table, not a played pitch.",
    };

    private static readonly string[] AllProjects =
    [
        "DotCharter.Core", "DotCharter.Analysis", "DotCharter.Rendering", "DotCharter.Tone", "DotCharter.Services",
        "DotCharter.Presentation", "DotCharter.Tone.UI", "DotCharter.UI",
    ];

    private static List<(string Path, string Text)> ProductFiles()
    {
        var root = TestPaths.RepoRoot();
        var sep = Path.DirectorySeparatorChar;
        return AllProjects
            .SelectMany(p => Directory.EnumerateFiles(Path.Combine(root, p), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{sep}obj{sep}", StringComparison.Ordinal) && !f.Contains($"{sep}bin{sep}", StringComparison.Ordinal))
            .Select(f => (Path.GetRelativePath(root, f).Replace(sep, '/'), SourceText.StripCommentsKeepingLines(File.ReadAllText(f))))
            .ToList();
    }

    [Fact]
    public void No_product_file_re_declares_the_last_fret_as_a_literal()
    {
        var files = ProductFiles().Where(f => f.Path != "DotCharter.Core/Utilities/FretLimits.cs").ToList();
        Assert.True(files.Count > 500, "the scan stopped seeing the code.");
        var offenders = new List<string>();
        foreach (var (path, text) in files)
        {
            foreach (var m in FretDeclaredAs24().Matches(text).Concat(Clamp24().Matches(text)))
                offenders.Add($"{path}:{SourceText.LineAt(text, m.Index)}: {m.Value.Trim()}");
        }

        Assert.True(offenders.Count == 0, LintReport.Of(
            "The last fret (24) and the anchor limits are declared ONCE, in FretLimits (#373):",
            offenders,
            "use FretLimits.MaxFret / MaxAnchorFret / MinAnchorFret(capo) / MaxAnchorFretFor(width).",
            "DotCharter.Core/AGENTS.md"));
    }

    [Fact]
    public void No_product_file_re_derives_a_string_pitch_from_the_open_string_table()
    {
        var files = ProductFiles();
        var naming = files.Where(f => f.Text.Contains("GuitarLowEMidi", StringComparison.Ordinal)).Select(f => f.Path).ToList();
        Assert.True(naming.Count > 0, "the scan stopped seeing the code.");

        var offenders = naming.Where(p => !PitchFormulaAllowed.ContainsKey(p)).ToList();
        Assert.True(offenders.Count == 0, LintReport.Of(
            "String-to-MIDI is SoundingPitch's job (#376); these files rebuild it from the open-string table:",
            offenders,
            "call SoundingPitch.MidiFor / SoundingMidiFor / OpenSoundingMidi; an allow-list entry needs a reason and the list only shrinks."));

        var stale = PitchFormulaAllowed.Keys.Where(p => !naming.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList();
        Assert.True(stale.Count == 0, LintReport.Of("These allow-list entries no longer name GuitarLowEMidi:", stale, "delete them."));
    }

    [Fact]
    public void ProductSurfaces_UseTheSharedChordNamingPolicy()
    {
        var offenders = ProductFiles()
            .Where(f => !f.Path.StartsWith("DotCharter.Core/", StringComparison.Ordinal) &&
                        f.Path != "DotCharter.Services/Editing/ChordShapes/NewChordNames.cs" &&
                        f.Text.Contains("ChordNameSuggester.Suggest", StringComparison.Ordinal))
            .Select(f => f.Path);

        Assert.Empty(offenders);
    }

    [GeneratedRegex(@"\bint\s+\w*[Ff]ret\w*\s*(?:=|\{\s*get;\s*(?:init|set);\s*\}\s*=)\s*24\b")]
    private static partial Regex FretDeclaredAs24();

    [GeneratedRegex(@"Math\.Min\(\s*24\s*,|Math\.Clamp\([^;()]*,\s*24\s*\)")]
    private static partial Regex Clamp24();
}
