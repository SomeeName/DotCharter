using System.Text.RegularExpressions;
using DotCharter.Services.Editing.Selection;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Editor.Inspector;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     #119: the Inspector's three problem counts say what they count, and the
///     export verdict has ONE home.
///     <para>
///         The Issues badge counts lint FINDINGS over every level; the Sound
///         tab's tile counts SOUNDS on the level being edited whose findings
///         would stop the export ("190 export blockers" against "Issues 267",
///         irreconcilable by reading); the Issues tab's export readiness is the
///         separate <c>ChartExportValidator</c>, run on demand — and "Check
///         export" was a button on both tabs.
///     </para>
/// </summary>
public sealed class InspectorProblemCountTests
{
    private static GuitarNote Note(int beat, int stringIdx = 0) => new()
    {
        Position = new FractionalPosition(beat), StringIndex = stringIdx, Fret = 5
    };

    [Fact]
    public void TheBlockersTile_CountsSoundsOnThisLevel_AndKnowsHowManyFindingsTheyCarry()
    {
        var arrangement = new Arrangement { Tuning = ["E", "A", "D", "G", "B", "e"] };
        var level = new ChartLevel();
        var other = new ChartLevel();
        arrangement.Levels.Add(level);
        arrangement.Levels.Add(other);

        var twoFindings = Note(0);
        var oneFinding = Note(2);
        var advisoryOnly = Note(4);
        var elsewhere = Note(0);
        level.Notes.Add(twoFindings);
        level.Notes.Add(oneFinding);
        level.Notes.Add(advisoryOnly);
        other.Notes.Add(elsewhere);

        List<ArrangementIssue> issues =
        [
            new(ArrangementIssueType.NoteFretBelowCapo, "a", Note: twoFindings),
            new(ArrangementIssueType.NoteWithoutAnchor, "b", Note: twoFindings),
            new(ArrangementIssueType.NoteFretBelowCapo, "c", Note: oneFinding),
            new(ArrangementIssueType.HopoIntoSameNote, "d", Note: advisoryOnly),
            // The lint sweeps EVERY level, the tile counts this one.
            new(ArrangementIssueType.NoteFretBelowCapo, "e", Note: elsewhere)
        ];

        var queue = new SoundQueueViewModel(() => arrangement, () => level, () => [], () => issues,
            new SelectionService(), _ => { }, FakeLocalization.English());

        Assert.Equal(2, queue.BlocksExportCount);
        Assert.Equal(3, queue.BlocksExportFindingCount);
    }

    [Fact]
    public void TheTile_StatesItsUnits_AndNeverClaimsTheExportVerdict()
    {
        var keys = FakeLocalization.KeysOnly();

        Assert.Equal("Inspector_Empty_Tile_BlockersTipFmt(190, 230)", ProblemCountText.BlockingSoundsTip(keys, 190, 230));
        Assert.Equal("Inspector_Issues_BadgeTipFmt(267)", ProblemCountText.IssueBadgeTip(keys, 267));

        // The words, in English: the unit is SOUNDS, and the tip sends the user
        // to the one place the export verdict comes from.
        var english = FakeLocalization.English();
        Assert.StartsWith("sounds", english["Inspector_Empty_Tile_Blockers"], StringComparison.Ordinal);
        Assert.Contains("Issues tab", ProblemCountText.BlockingSoundsTip(english, 190, 230), StringComparison.Ordinal);
        Assert.Contains("lint", ProblemCountText.IssueBadgeTip(english, 267), StringComparison.Ordinal);
    }

    /// <summary>
    ///     The tile is LINT-based, so it may not speak with the export check's
    ///     voice: no <c>Inspector_Export_*</c> key anywhere in the text it shows,
    ///     and the Sound tab binds the tile to the unit-stating strings.
    /// </summary>
    [Fact]
    public void TheSoundTab_DoesNotUseTheExportVerdictsKeys_ForItsTile()
    {
        var keys = FakeLocalization.KeysOnly();
        Assert.DoesNotContain("Inspector_Export_", ProblemCountText.BlockingSoundsTip(keys, 3, 4), StringComparison.Ordinal);

        var view = SoundTabMarkup();
        Assert.Contains("Text=\"{localization:Loc Inspector_Empty_Tile_Blockers}\"", view, StringComparison.Ordinal);
        Assert.Contains("ToolTip.Tip=\"{Binding Queue.BlocksExportTileTip}\"", view, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Exactly ONE view binds the export check, and it is the Issues tab;
    ///     the Sound tab deep-links there instead of running the check where its
    ///     verdict cannot be seen.
    /// </summary>
    [Fact]
    public void CheckExport_IsBoundByExactlyOneView_TheIssuesTab()
    {
        var root = TestPaths.RepoRoot();
        var views = new[] { "DotCharter.UI", "DotCharter.Tone.UI" }
            .Select(p => Path.Combine(root, p, "Views"))
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.axaml", SearchOption.AllDirectories))
            .ToList();
        Assert.True(views.Count > 50, $"only {views.Count} views found — the lint went blind.");

        var binders = views
            .Where(f => Regex.IsMatch(StripComments(File.ReadAllText(f)), @"CheckExportReadinessCommand\b"))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Equal(["IssuesInspectorView.axaml"], binders);
        Assert.Contains("Command=\"{Binding ShowExportReadinessCommand}\"", SoundTabMarkup(), StringComparison.Ordinal);
    }

    /// <summary>The Sound tab's markup is SEVERAL files: each section of its rail is a view of its
    /// own (<c>Sound*View.axaml</c>, #601), and the tab hosts them.</summary>
    private static string SoundTabMarkup()
    {
        var dir = Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Editor", "Inspector");
        var view = File.ReadAllText(Path.Combine(dir, "SoundInspectorView.axaml"));
        var parts = Directory.GetFiles(dir, "Sound*View.axaml").Where(f => !f.EndsWith("SoundInspectorView.axaml", StringComparison.Ordinal))
            .OrderBy(f => f, StringComparer.Ordinal).Select(File.ReadAllText).ToList();
        Assert.True(parts.Count >= 8, "the Sound tab's own views were not found — repoint this test.");
        return view + string.Concat(parts);
    }

    private static string StripComments(string xaml) => Regex.Replace(xaml, "<!--.*?-->", string.Empty, RegexOptions.Singleline);
}
