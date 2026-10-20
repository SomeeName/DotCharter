using System.Text.RegularExpressions;
using DotCharter.Services.Editing;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     0.36 mask unification. Until this change the inspector owned a SECOND
///     per-string selector (<c>ChordStringTargets</c>) that was independent of
///     <c>ProjectState.StringEditMask</c> and had the opposite default:
///     every string <c>IsSelected = true</c> vs. the mask's empty. Masking
///     strings 4–5 on the lane headers (<c>Ctrl+4</c>, <c>Ctrl+5</c>) and then
///     clicking a technique chip in the inspector therefore applied the edit to
///     ALL strings — a live correctness bug in the seam between the v0.35
///     keyboard work and the untouched inspector.
///     <para>
///         Two guards: the targeting RULE (pure, so it runs without the
///         inspector's DI graph — the same <c>internal</c> seam
///         <c>AllSameNullable</c> uses) and a source lint proving the second
///         selector is really gone rather than merely unbound.
///     </para>
/// </summary>
public class InspectorStringMaskTests
{
    private static List<ChordNote> Chord(params int[] strings) =>
        strings.Select(s => new ChordNote { StringIndex = s, Fret = 5 }).ToList();

    [Fact]
    public void AnEmptyMaskTargetsEveryString()
    {
        // "Empty = all" is what makes the mask a strict no-op until the user
        // sets one — the reading StringEditMask.Includes encodes and both
        // reference tools use.
        var targeted = InspectorViewModel.MaskedChordNotes(Chord(0, 1, 2, 3, 4, 5), StringEditMask.Empty);
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, targeted.Select(n => n.StringIndex));
    }

    [Fact]
    public void AMaskSetOnTheLaneHeadersReachesTheInspector()
    {
        // THE regression. Ctrl+4 / Ctrl+5 → strings 3 and 4 (Ctrl+n is 1-based).
        var mask = StringEditMask.FromStrings(3, 4);
        var targeted = InspectorViewModel.MaskedChordNotes(Chord(0, 1, 2, 3, 4, 5), mask);
        Assert.Equal(new[] { 3, 4 }, targeted.Select(n => n.StringIndex));
    }

    [Fact]
    public void AMaskedStringTheChordDoesNotPlayTargetsNothingRatherThanEverything()
    {
        // The old code's "nothing picked ⇒ fall back to the bend target's single
        // string" is gone: a mask that misses this chord means the edit has no
        // target, and the scope bar says so. Silently retargeting would be the
        // same class of surprise the whole rework removes.
        var mask = StringEditMask.FromStrings(7);
        Assert.Empty(InspectorViewModel.MaskedChordNotes(Chord(0, 1, 2), mask));
    }

    [Fact]
    public void TargetsComeBackInStringOrderSoTheTechniqueReadoutIsStable()
    {
        var mask = StringEditMask.FromStrings(1, 4);
        var targeted = InspectorViewModel.MaskedChordNotes(Chord(4, 2, 1, 0), mask);
        Assert.Equal(new[] { 1, 4 }, targeted.Select(n => n.StringIndex));
    }

    [Fact]
    public void TogglingTheLastStringOffReturnsToEveryString()
    {
        var mask = StringEditMask.Empty.Toggle(2).Toggle(2);
        Assert.True(mask.IsEmpty);
        Assert.Equal(3, InspectorViewModel.MaskedChordNotes(Chord(0, 1, 2), mask).Count);
    }

    // ---- the second selector is gone, not merely unbound --------------------

    [Fact]
    public void TheInspectorHasNoSecondPerStringSelector()
    {
        var uiRoot = RepoPath("DotCharter.UI");
        var offenders = UiSourceTree
            .EnumerateFiles(uiRoot, "*.*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                         f.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase)) &&
                        !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
                        !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(f => Regex.IsMatch(UiSourceTree.ReadAllText(f),
                @"\bChordStringTargets?\b|\bSelectedStringPickIndices\b|\bToggleStringPickCommand\b|\bStringPickMode\b"))
            // The XML-doc note in InspectorViewModel that explains WHY it is
            // gone is allowed to name it.
            .Where(f => !UiSourceTree.ReadAllText(f).Contains("0.36 this read the inspector's own"))
            .ToList();

        Assert.True(offenders.Count == 0,
            "A second per-string selector is back — it must be ProjectState.StringEditMask, " +
            "the one the lane headers draw. Offenders: " + string.Join(", ", offenders));
    }

    [Fact]
    public void TheChordNoteWriteFunnelReadsTheMask()
    {
        var source = InspectorSource();

        var funnel = Between(source, "private void ApplyChordNoteMutation", "\n    }");
        Assert.Contains("StringEditMask", funnel);
        Assert.Contains("MaskedChordNotes", funnel);

        var fingers = Between(source, "private void SetFingerOnSelectedStrings", "\n    }");
        Assert.Contains("StringEditMask", fingers);
    }

    /// <summary>The whole <c>InspectorViewModel</c>, which is a set of topic
    /// partials — the two funnels this guard reads now live in different ones
    /// (<c>.Chords.cs</c> and <c>.BulkEdits.cs</c>), so reading the root alone
    /// would fail on a move rather than check anything.</summary>
    private static string InspectorSource()
    {
        var dir = RepoPath(UiSourceTree.Resolve(Path.Combine("DotCharter.UI", "ViewModels", "Editor")));
        var files = UiSourceTree.GetFiles(dir, "InspectorViewModel*.cs");
        Assert.NotEmpty(files);
        return string.Join("\n", files.Select(UiSourceTree.ReadAllText));
    }

    private static string Between(string source, string start, string end)
    {
        var from = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"'{start}' is gone from InspectorViewModel.");
        var to = source.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(to > from, $"Could not find the end of '{start}'.");
        return source[from..to];
    }

    private static string RepoPath(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "DotCharter.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return Path.Combine(dir!, relative);
    }
}
