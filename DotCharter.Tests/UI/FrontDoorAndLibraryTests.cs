using System.Xml.Linq;
using DotCharter.UI.Controls;
using DotCharter.UI.ViewModels.PlayMode;
using DotCharter.Presentation.Shared.Library;

namespace DotCharter.Tests.UI;

/// <summary>
///     Default-gate guards for the 0.47 front door and song library.
///
///     <para>
///         Everything here is either PURE (the grouping, the chip model, the
///         facts snapshot) or a SOURCE-STRUCTURE assertion over the real XAML
///         parsed as XML. Booting the app session for a visual-tree walk is the
///         <c>HeadlessSessionFixture</c> teardown trap the suite excludes from
///         the default gate — the runtime halves (bounds, focus, the tuning
///         column's measured alignment) live in
///         <c>Perf/PlayModeScreensUiPerfSmokeTests</c>.
///     </para>
/// </summary>
public sealed class FrontDoorAndLibraryTests
{
    // ── The five deleted front-door candidates ────────────────────────────────

    /// <summary>
    ///     Nothing named <c>MenuDesign1..5</c> survives anywhere — not the
    ///     markup, not the view model, not <c>AppConfig</c>, not the design
    ///     picker, not its localized strings.
    ///     <para>
    ///         Scaffolding rots in place: the switcher shipped as "temporary
    ///         evaluation" and outlived three releases. A dangling
    ///         <c>IsMenuDesign3</c> binding is a SILENT no-op (the binding fails
    ///         and the panel simply never shows), which is exactly how half a
    ///         deletion survives review.
    ///     </para>
    /// </summary>
    [Fact]
    public void NothingNamedMenuDesign1To5_SurvivesAnywhere()
    {
        string[] forbidden =
        [
            "MenuDesign1Root", "MenuDesign2Root", "MenuDesign3Root", "MenuDesign4Root", "MenuDesign5Root",
            "IsMenuDesign1", "IsMenuDesign2", "IsMenuDesign3", "IsMenuDesign4", "IsMenuDesign5",
            "IsMenuDesign6", "MenuDesignSwitcher", "MenuDesignPicker", "MenuDesignCount",
            "PlayMode_Menu_Design"
        ];

        var offenders = new List<string>();
        foreach (var file in SourceFiles())
        {
            var text = UiSourceTree.ReadAllText(file);
            offenders.AddRange(forbidden
                .Where(token => text.Contains(token, StringComparison.Ordinal))
                .Select(token => $"{Path.GetRelativePath(TestPaths.RepoRoot(), file)}: '{token}'"));
        }

        Assert.True(offenders.Count == 0,
            "the five rejected front-door candidates and their picker were deleted in 0.47, but something " +
            "still names them:\n" + string.Join("\n", offenders));

        // …and the config field is gone with them. A leftover property would
        // keep round-tripping in every user's config file forever.
        Assert.DoesNotContain("MenuDesign",
            UiSourceTree.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "DotCharter.Services", "Infrastructure",
                "Configuration", "AppConfig.cs")).Replace("`MenuDesign`", "", StringComparison.Ordinal));

        // …and the winner went too (2026-09-22): Start leads to Home now.
        Assert.DoesNotContain(ViewDoc().Descendants(),
            e => e.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == "MenuDesign6Root"));
    }

    // ── §6.2's stated acceptance criteria, in the markup ──────────────────────

    /// <summary>
    ///     The tuning column is a FIXED column of the list's shared
    ///     <see cref="SongTableLayout" /> (#231). An <c>Auto</c> one measures per
    ///     row and the values go ragged down the list, which is the exact thing
    ///     §6.2 names as its acceptance criterion — and it is invisible to any
    ///     per-element bounds check.
    /// </summary>
    [Fact]
    public void TheSongRowsTuningColumn_IsFixedWidth()
    {
        var cell = ScreenRoot("SongSelectScreenRoot").Descendants()
            .Single(e => e.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == "SongTuningColumn"));

        var index = int.Parse(cell.Attributes().Single(a => a.Name.LocalName == "SongTablePanel.Column").Value);
        var table = cell.Parent!;
        Assert.Equal("SongTablePanel", table.Name.LocalName);
        Assert.Equal("{x:Static appControls:SongTableLayout.PlayModeSongList}", table.Attribute("Columns")?.Value);

        var column = SongTableLayout.PlayModeSongList.Columns[index];
        Assert.True(index != SongTableLayout.PlayModeSongList.SongColumn,
            "the tuning cell sits in the flexible song column — it must be a fixed pixel width, " +
            "or the tuning values stop lining up down the list");
        Assert.Equal("tuning", column.Name);
        Assert.True(column.Width > 100, $"the tuning column is {column.Width} px — too narrow for 'E STANDARD · CAPO 2'");

        // …and it prints the tuning WITH the capo, amber when either needs
        // attention. Two separate cells could not stay aligned as one column.
        Assert.Equal("{Binding TuningCellLabel}", cell.Attribute("Text")?.Value);
        Assert.Equal("{Binding TuningNeedsAttention}",
            cell.Attributes().Single(a => a.Name.LocalName == "Classes.Warn").Value);
    }

    /// <summary>
    ///     EXACTLY ONE Play button on the library screen, and it is inside the
    ///     detail pane. The per-row Play cluster moving into the rail is the
    ///     point of the redesign: the row is information, the rail is what you
    ///     DO.
    /// </summary>
    [Fact]
    public void TheSongLibrary_HasOnePlayButton_AndItIsInTheDetailPane()
    {
        var root = ScreenRoot("SongSelectScreenRoot");
        var plays = root.Descendants()
            .Where(e => e.Name.LocalName is "Button" or "ToggleButton")
            .Where(e => (e.Attribute("Command")?.Value ?? "").Contains("PlaySongCommand", StringComparison.Ordinal))
            .ToList();

        Assert.Single(plays);
        Assert.Equal("SongSelectPlayButton",
            plays[0].Attributes().Single(a => a.Name.LocalName == "Name").Value);
        Assert.Contains(plays[0].Ancestors(),
            a => a.Attributes().Any(x => x.Name.LocalName == "Name" && x.Value == "SongSelectDetailRail"));

        // The list's row template must carry NO button at all — a row is
        // information. (The inline LAN board it hosts is a view of its own.)
        var template = root.Descendants()
            .Single(e => e.Name.LocalName == "ListBox.ItemTemplate");
        Assert.DoesNotContain(template.Descendants(),
            e => e.Name.LocalName is "Button" or "ToggleButton");
    }

    /// <summary>
    ///     ONE animated highway behind every Play Mode screen, inside its clip — a
    ///     second instance would mean a second repaint timer.
    /// </summary>
    [Fact]
    public void TheAnimatedHighway_IsOneControlBehindEveryScreen()
    {
        var clip = ViewDoc().Descendants().Single(e =>
            e.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == "MenuBackdropClip"));

        Assert.Contains("FireBackdropClip", clip.Attribute("Classes")!.Value);
        Assert.Single(ViewDoc().Descendants(), e => e.Name.LocalName == "MenuBackdropControl");
        Assert.Contains(clip.Descendants(), e => e.Name.LocalName == "MenuBackdropControl");
    }

    // ── The pure read-models ──────────────────────────────────────────────────

    /// <summary>Artist runs are real group objects, and the header is stamped on
    /// the FIRST row of each run — never on the others, and never at all in a
    /// list that is not sorted by artist.</summary>
    [Fact]
    public void ArtistGrouping_BuildsRunsAndStampsOnlyTheFirstRowOfEach()
    {
        var rows = new[] { "A", "A", "A", "B", "C", "C" }.Select(Row).ToList();

        var groups = SongArtistGrouping.Build(rows, FakeLocalization.English());
        Assert.Equal(3, groups.Count);
        Assert.Equal(("A", 3), (groups[0].Artist, groups[0].Count));
        Assert.Equal(("B", 1), (groups[1].Artist, groups[1].Count));
        Assert.Equal(("C", 2), (groups[2].Artist, groups[2].Count));

        SongArtistGrouping.Stamp(rows, grouped: true, (_, n) => $"{n} SONGS", FakeLocalization.English());
        Assert.Equal(["A", "", "", "B", "C", ""], rows.Select(r => r.GroupHeader).ToArray());
        Assert.Equal("3 SONGS", rows[0].GroupCountLabel);

        // A title/recent/best sort must not sprout artist headers.
        SongArtistGrouping.Stamp(rows, grouped: false, (_, n) => $"{n} SONGS", FakeLocalization.English());
        Assert.All(rows, r => Assert.Equal("", r.GroupHeader));
        Assert.All(rows, r => Assert.False(r.HasGroupHeader));
        return;

        static PlaySongItem Row(string artist)
        {
            var song = PlaySongItem.FromPsarc(new DotCharter.UI.ViewModels.Panels.PsarcSongListItem(
                new DotCharter.Services.Library.LibrarySongEntry
                {
                    SongKey = Guid.NewGuid().ToString("N"), Title = "t", Artist = artist
                }, FakeLocalization.English()));
            return song;
        }
    }

    // The chip model is no longer a pure builder here: SINCE THE UNIFIED
    // BROWSER (2026-08-15) the chips are `BrowserFilterChip`s rebuilt by the
    // VM's own filter pass (one per selected facet OPTION plus the search /
    // record / updated specials), so their guards — one chip per active
    // filter, none when nothing narrows, and the ✕ round-trip for every kind —
    // live where the pass does: `SongLibraryRepublishTests`.

    /// <summary>The record ladder is derived from the scoring model, not written
    /// out here: one entry per <c>LetterGrade</c>, best first, bracketed by "any"
    /// and "full combo only".</summary>
    [Fact]
    public void TheRecordLadder_CoversEveryGradeBestFirst()
    {
        var grades = PlayRecordFilter.GradesBestFirst;
        Assert.Equal(Enum.GetValues<DotCharter.Services.Gameplay.Scoring.LetterGrade>().Length, grades.Count);
        Assert.Equal(DotCharter.Services.Gameplay.Scoring.LetterGrade.S, grades[0]);
        Assert.Equal(DotCharter.Services.Gameplay.Scoring.LetterGrade.F, grades[^1]);

        // Index 0 is "any" and the last is "full combo": neither is a grade floor.
        Assert.Null(PlayRecordFilter.MinGradeFor(0));
        Assert.Null(PlayRecordFilter.MinGradeFor(PlayRecordFilter.FullComboIndex));
        Assert.True(PlayRecordFilter.IsFullComboOnly(PlayRecordFilter.FullComboIndex));
        Assert.False(PlayRecordFilter.IsFullComboOnly(0));
        Assert.Equal(DotCharter.Services.Gameplay.Scoring.LetterGrade.S, PlayRecordFilter.MinGradeFor(1));
        Assert.Equal(PlayRecordFilter.OptionCount, PlayRecordFilter.OptionLabels(FakeLocalization.English()).Count);
        Assert.All(PlayRecordFilter.OptionLabels(FakeLocalization.English()), label => Assert.False(string.IsNullOrWhiteSpace(label)));
    }

    /// <summary>An empty snapshot claims NOTHING. Every figure has a
    /// <c>Has*</c> companion, and a screen bound to the empty one draws its
    /// chrome with no numbers rather than plausible ones.</summary>
    [Fact]
    public void FrontDoorFacts_Empty_ClaimsNothing()
    {
        var empty = FrontDoorFacts.Empty;
        Assert.False(empty.HasTuning);
        Assert.False(empty.HasOwnCharts);
        Assert.False(empty.HasLastSession);
        Assert.Null(empty.LastSession);

        // A filled one reports what it was given, and HasLastSession follows the
        // value rather than a flag someone has to remember to set.
        var filled = new FrontDoorFacts
        {
            TuningLabel = "Drop C",
            LastSession = new FrontDoorLastSession("42", "96.1%", "+2,400")
        };
        Assert.True(filled.HasTuning);
        Assert.True(filled.HasLastSession);

        Assert.StartsWith("v", FrontDoorFacts.ResolveAppVersion(), StringComparison.Ordinal);
        Assert.DoesNotContain('+', FrontDoorFacts.ResolveAppVersion());
    }

    /// <summary>
    ///     The detail rail's per-arrangement note counts are REAL: they come off
    ///     the library index entry, one tile per PATH, and an arrangement whose
    ///     package carried no count says so instead of printing a zero.
    /// </summary>
    [Fact]
    public void ArrangementFacts_ReadTheRealNoteCountsAndAdmitWhenThereAreNone()
    {
        var song = DotCharter.UI.ViewModels.PlayMode.PlaySongItem.FromPsarc(
            new DotCharter.UI.ViewModels.Panels.PsarcSongListItem(
                new DotCharter.Services.Library.LibrarySongEntry
                {
                    SongKey = "kryptonite", Title = "Kryptonite", Artist = "3 Doors Down", Year = 2000,
                    Arrangements =
                    [
                        new DotCharter.Services.Library.LibraryArrangementEntry
                            { ArrangementName = "Lead", RouteMask = 1, NoteCount = 982, SngPath = "a.sng" },
                        new DotCharter.Services.Library.LibraryArrangementEntry
                            { ArrangementName = "Rhythm", RouteMask = 2, NoteCount = 1140, SngPath = "b.sng" },
                        new DotCharter.Services.Library.LibraryArrangementEntry
                            { ArrangementName = "Vocals", RouteMask = 0, NoteCount = 0, SngPath = "c.sng" }
                    ]
                }, FakeLocalization.English()));

        var facts = new SongArrangementFacts().For(song);
        Assert.Equal(["Lead", "Rhythm", "Vocals"], facts.Select(f => f.Label).ToArray());
        // Formatted with the CURRENT culture, like every other figure in the
        // app — so the expectation is formatted the same way rather than
        // hard-coding an en-US thousands separator.
        Assert.Equal(982.ToString("N0"), facts[0].NoteCountLabel);
        Assert.Equal(1140.ToString("N0"), facts[1].NoteCountLabel);
        Assert.True(facts[0].HasNoteCount);
        // No count in the package ⇒ the tile shows the path alone.
        Assert.False(facts[2].HasNoteCount);
        Assert.Equal("", facts[2].NoteCountLabel);
        // Vocals has no tuning of its own to state (its tile printed "E Stand…").
        Assert.False(facts[2].HasTuning);

        // The song's YEAR is surfaced too — it was on the index entry all along.
        Assert.Equal(2000, song.Year);
        Assert.Equal("3 Doors Down · 2000", song.DetailSubtitle);
    }

    /// <summary>
    ///     The tuning cell holds the tuning AND the capo, and goes amber only
    ///     when there is something to DO — a non-standard tuning or a capo.
    ///     UNKNOWN (an own chart, a peer song) is never an alarm.
    /// </summary>
    [Fact]
    public void TheTuningCell_FoldsInTheCapoAndFlagsOnlyWhatNeedsDoing()
    {
        var standard = Song("E A D G B E", 0);
        Assert.Equal("E Standard", standard.TuningCellLabel);
        Assert.False(standard.TuningNeedsAttention);

        var dropD = Song("D A D G B E", 0);
        Assert.True(dropD.IsNonStandardTuning);
        Assert.True(dropD.TuningNeedsAttention);

        var capo = Song("E A D G B E", 2);
        Assert.False(capo.IsNonStandardTuning);
        Assert.True(capo.TuningNeedsAttention);
        Assert.Equal("E Standard · " + FakeLocalization.English().Format("SongBrowser_Facet_CapoAt", 2), capo.TuningCellLabel);

        // An own chart knows neither; the unknown-data policy says that is not
        // "non-standard".
        var own = DotCharter.UI.ViewModels.PlayMode.PlaySongItem.FromProject("C:\\x\\y.rscp", false, FakeLocalization.English());
        Assert.False(own.IsNonStandardTuning);
        Assert.False(own.TuningNeedsAttention);
        return;

        static DotCharter.UI.ViewModels.PlayMode.PlaySongItem Song(string tuning, int capo) =>
            DotCharter.UI.ViewModels.PlayMode.PlaySongItem.FromPsarc(
                new DotCharter.UI.ViewModels.Panels.PsarcSongListItem(
                    new DotCharter.Services.Library.LibrarySongEntry
                    {
                        SongKey = "s", TuningSummary = tuning, CapoSummary = capo
                    }, FakeLocalization.English()));
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static IEnumerable<string> SourceFiles()
    {
        string[] roots =
        [
            "DotCharter.UI", "DotCharter.Services", "DotCharter.Tone", "DotCharter.Core", "DotCharter.Localization",
            "DotCharter.Tests"
        ];
        foreach (var root in roots)
        {
            foreach (var file in UiSourceTree.EnumerateFiles(Path.Combine(TestPaths.RepoRoot(), root), "*",
                     SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                        StringComparison.Ordinal) ||
                    file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                        StringComparison.Ordinal))
                {
                    continue;
                }

                var extension = Path.GetExtension(file);
                // This test file itself NAMES the forbidden tokens; skip it.
                if (Path.GetFileName(file) == "FrontDoorAndLibraryTests.cs") continue;
                if (extension is ".cs" or ".axaml" or ".resx") yield return file;
            }
        }
    }

    // ── The library button's count ────────────────────────────────────────────

    /// <summary>The gate itself. A formatted "0" is the not-scanned-yet value
    /// the facts snapshot defaults to; any real figure counts, thousands
    /// separator and all.</summary>
    [Theory]
    [InlineData("0", false)]
    [InlineData(" 0 ", false)]
    [InlineData("", false)]
    [InlineData("1", true)]
    [InlineData("2.106", true)]
    [InlineData("2,106", true)]
    public void HasLibraryCount_IsFalseOnlyForNothingOrZero(string count, bool expected)
        => Assert.Equal(expected, new FrontDoorFacts { LibraryCount = count }.HasLibraryCount);

    private static XDocument ViewDoc() => PlayModeMarkup.Document();

    private static XElement ScreenRoot(string name)
    {
        var root = ViewDoc().Descendants().SingleOrDefault(e =>
            e.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == name));
        Assert.True(root != null,
            $"'{name}' is not in the Play Mode markup — a renamed screen root silently skips every guard here");
        return root!;
    }
}
