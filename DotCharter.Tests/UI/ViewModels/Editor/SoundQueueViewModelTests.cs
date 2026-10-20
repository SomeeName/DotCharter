using DotCharter.Services.Editing.Selection;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     The work queue (04-inspector-redesign.md §8) — EOF's <c>F</c> dialog,
///     generalised. A STANDALONE view model with injected seams, so it runs in
///     the default gate without booting the app session.
/// </summary>
public class SoundQueueViewModelTests
{
    private sealed class Harness
    {
        public readonly Arrangement Arrangement = new();
        public readonly List<ArrangementIssue> Issues = [];
        public readonly SelectionService Selection = new();
        public readonly List<double> Seeks = [];

        public Harness()
        {
            Arrangement.Levels.Add(new ChartLevel());
            Arrangement.Tuning = ["E", "A", "D", "G", "B", "e"];
            ActiveLevel = Arrangement.Levels[0];
        }

        /// <summary>The level the editor is on — the seam the queue reads.</summary>
        public ChartLevel? ActiveLevel;

        /// <summary>How often each seam has been read. A refresh derives seven
        /// filter counts plus the current position and must still read each
        /// seam ONCE — see <c>ARefresh_ReadsEachSeamExactlyOnce</c>.</summary>
        public int LevelReads;

        public int IssueReads;

        public void ResetSeamCounts() => LevelReads = IssueReads = 0;

        public ChartLevel Level => Arrangement.Levels[0];

        public ChartLevel AddLevel()
        {
            var level = new ChartLevel();
            Arrangement.Levels.Add(level);
            return level;
        }

        public SoundQueueViewModel Create() => new(
            () => Arrangement,
            () =>
            {
                LevelReads++;
                return ActiveLevel;
            },
            () => [],
            () =>
            {
                IssueReads++;
                return Issues;
            },
            Selection,
            ms => Seeks.Add(ms),
            FakeLocalization.English());
    }

    private static GuitarNote Note(int beat, int stringIdx = 0, int fret = 5) => new()
    {
        Position = new FractionalPosition(beat), StringIndex = stringIdx, Fret = fret
    };

    private static Chord Chord(int beat, int templateId, params (int s, int f)[] notes)
    {
        var c = new Chord { Position = new FractionalPosition(beat), TemplateId = templateId };
        foreach (var (s, f) in notes) c.ChordNotes.Add(new ChordNote { StringIndex = s, Fret = f });
        return c;
    }

    [Fact]
    public void AnySoundOrdersNotesAndChordsTogetherByPosition()
    {
        var h = new Harness();
        h.Level.Chords.Add(Chord(2, 0, (0, 3), (1, 3)));
        h.Level.Notes.Add(Note(0));
        h.Level.Notes.Add(Note(4));

        var vm = h.Create();
        var matches = vm.Matches(SoundQueueFilter.AnySound);

        Assert.Equal(3, matches.Count);
        Assert.Equal(
            new[] { 0, 2, 4 },
            matches.Select(m => SoundQueueViewModel.PositionOf(m).BeatId));
    }

    [Fact]
    public void MissingFingeringMeansAFrettedStringWithNoFinger()
    {
        var h = new Harness();
        h.Arrangement.ChordTemplates.Add(new ChordTemplate
        {
            Id = 0,
            Frets = new Dictionary<int, int> { { 0, 3 }, { 1, 5 } },
            Fingers = new Dictionary<int, int> { { 0, 1 } }
        });
        // Fully fingered — must NOT match.
        h.Arrangement.ChordTemplates.Add(new ChordTemplate
        {
            Id = 1,
            Frets = new Dictionary<int, int> { { 0, 3 } },
            Fingers = new Dictionary<int, int> { { 0, 1 } }
        });
        h.Level.Chords.Add(Chord(0, 0, (0, 3), (1, 5)));
        h.Level.Chords.Add(Chord(4, 1, (0, 3)));

        var vm = h.Create();
        var matches = vm.Matches(SoundQueueFilter.MissingFingering);

        Assert.Single(matches);
        Assert.Equal(0, SoundQueueViewModel.PositionOf(matches[0]).BeatId);
    }

    [Fact]
    public void AnOpenStringWithNoFingerIsNotMissingAFingering()
    {
        // A finger on an OPEN string is inspector rule L06 — the absence of one
        // is correct, not work to do.
        var h = new Harness();
        h.Arrangement.ChordTemplates.Add(new ChordTemplate
        {
            Id = 0,
            Frets = new Dictionary<int, int> { { 0, 0 }, { 1, 0 } }
        });
        h.Level.Chords.Add(Chord(0, 0, (0, 0), (1, 0)));

        Assert.Empty(h.Create().Matches(SoundQueueFilter.MissingFingering));
    }

    [Fact]
    public void UntemplatedIsTheMinusOneSentinelAndNotTemplateZero()
    {
        // 0 is a REAL template id; -1 is the sentinel. Reading 0 as "unset" is
        // the bug this repo has paid for more than once.
        var h = new Harness();
        h.Arrangement.ChordTemplates.Add(new ChordTemplate { Id = 0, Name = "A5" });
        h.Level.Chords.Add(Chord(0, 0, (0, 3)));
        h.Level.Chords.Add(Chord(4, -1, (0, 3)));

        var matches = h.Create().Matches(SoundQueueFilter.Untemplated);
        Assert.Single(matches);
        Assert.Equal(4, SoundQueueViewModel.PositionOf(matches[0]).BeatId);
    }

    [Fact]
    public void BlocksExportIsAStrictSubsetOfHasIssue()
    {
        var h = new Harness();
        var blocking = Note(0);
        var advisory = Note(4);
        h.Level.Notes.Add(blocking);
        h.Level.Notes.Add(advisory);
        h.Issues.Add(new ArrangementIssue(ArrangementIssueType.NoteFretBelowCapo, "", Note: blocking));
        h.Issues.Add(new ArrangementIssue(ArrangementIssueType.HopoIntoSameNote, "", Note: advisory));

        var vm = h.Create();
        Assert.Equal(2, vm.Matches(SoundQueueFilter.HasIssue).Count);
        Assert.Single(vm.Matches(SoundQueueFilter.BlocksExport));
    }

    [Fact]
    public void SteppingSelectsTheSoundRatherThanOnlySeekingToIt()
    {
        // The difference from the SelectNextMissingFingering button it subsumes:
        // the panel's fields have to be live and the per-string verbs need a
        // target, so it SELECTS.
        var h = new Harness();
        var first = Note(0);
        h.Level.Notes.Add(first);
        h.Level.Notes.Add(Note(4));

        var vm = h.Create();
        vm.NextMatch();

        Assert.Contains(first, h.Selection.SelectedNotes);
        Assert.Same(first, h.Selection.SelectedNote);
        Assert.Equal(1, vm.CurrentIndex);
    }

    [Fact]
    public void SteppingPastTheEndWrapsVisiblyInsteadOfDoingNothing()
    {
        var h = new Harness();
        h.Level.Notes.Add(Note(0));
        h.Level.Notes.Add(Note(4));

        var vm = h.Create();
        vm.NextMatch();
        vm.NextMatch();
        Assert.Equal(2, vm.CurrentIndex);
        Assert.False(vm.Wrapped);

        vm.NextMatch();
        Assert.Equal(1, vm.CurrentIndex);
        Assert.True(vm.Wrapped);
    }

    /// <summary>
    ///     #601: a sound CLICKED in the chart (a selection change with no edit, so no refresh) moves
    ///     the counter to its own place, and ‹ › step on from there - not from where the last step
    ///     had stopped.
    /// </summary>
    [Fact]
    public void AClickedSound_MovesTheCounter_AndTheNextStepGoesOnFromIt()
    {
        var h = new Harness();
        for (var beat = 0; beat < 10; beat++) h.Level.Notes.Add(Note(beat * 4));
        var vm = h.Create();
        vm.NextMatch();
        vm.NextMatch();
        Assert.Equal(2, vm.CurrentIndex);

        // The click: what the highway's select tool does - replace the selection, no edit.
        h.Selection.ClearSelections();
        h.Selection.SelectedNotes.Add(h.Level.Notes[6]);
        Assert.Equal(7, vm.CurrentIndex);
        Assert.Equal("7 / 10", vm.PositionLabel);

        vm.NextMatch();
        Assert.Same(h.Level.Notes[7], h.Selection.SelectedNote);
        Assert.Equal(8, vm.CurrentIndex);

        h.Selection.ClearSelections();
        h.Selection.SelectedNotes.Add(h.Level.Notes[3]);
        vm.PreviousMatch();
        Assert.Same(h.Level.Notes[2], h.Selection.SelectedNote);
        Assert.Equal(3, vm.CurrentIndex);
    }

    /// <summary>#601: a multi-selection, nothing, or a sound the filter excludes shows "—", not a stale number.</summary>
    [Fact]
    public void TheCounter_ShowsNoPosition_ForAMultiSelectionOrASoundOutsideTheFilter()
    {
        var h = new Harness();
        for (var beat = 0; beat < 4; beat++) h.Level.Notes.Add(Note(beat * 4));
        var untemplated = Chord(20, -1, (0, 3), (1, 5));
        h.Level.Chords.Add(untemplated);
        var vm = h.Create();
        vm.NextMatch();
        Assert.Equal(1, vm.CurrentIndex);

        h.Selection.SelectedNotes.Add(h.Level.Notes[2]);
        Assert.Equal(0, vm.CurrentIndex);
        Assert.StartsWith("—", vm.PositionLabel);

        h.Selection.ClearSelections();
        Assert.Equal(0, vm.CurrentIndex);

        vm.SelectedFilter = vm.Filters.Single(f => f.Filter == SoundQueueFilter.Untemplated);
        h.Selection.SelectedNotes.Add(h.Level.Notes[1]);
        Assert.Equal(0, vm.CurrentIndex);
        h.Selection.ClearSelections();
        h.Selection.SelectedChords.Add(untemplated);
        Assert.Equal(1, vm.CurrentIndex);
    }

    [Fact]
    public void AForwardStepFromNothingSelectedLandsOnTheFirstMatch()
    {
        // CurrentIndex is 1-based with 0 meaning "not in the queue", so this is
        // the off-by-one that would otherwise skip the first sound.
        var h = new Harness();
        var first = Note(0);
        h.Level.Notes.Add(first);
        h.Level.Notes.Add(Note(4));

        var vm = h.Create();
        Assert.Equal(0, vm.CurrentIndex);
        vm.NextMatch();
        Assert.Same(first, h.Selection.SelectedNote);
    }

    [Fact]
    public void TheHealthTilesAreTheQueuesOwnFiltersPreCounted()
    {
        var h = new Harness();
        h.Arrangement.ChordTemplates.Add(new ChordTemplate
        {
            Id = 0, Name = "", Frets = new Dictionary<int, int> { { 0, 3 } }
        });
        h.Level.Chords.Add(Chord(0, 0, (0, 3)));

        var vm = h.Create();
        Assert.Equal(1, vm.MissingFingeringCount);
        Assert.Equal(1, vm.UnnamedChordCount);
        Assert.Equal(0, vm.BlocksExportCount);

        // Clicking a tile sets the filter AND jumps to the first match.
        vm.TriageFilter(SoundQueueFilter.UnnamedChord);
        Assert.Equal(SoundQueueFilter.UnnamedChord, vm.SelectedFilter!.Filter);
        Assert.Equal(1, vm.CurrentIndex);
    }

    [Fact]
    public void AnEmptyQueueReportsItRatherThanThrowing()
    {
        var vm = new Harness().Create();
        vm.TriageFilter(SoundQueueFilter.BlocksExport);
        Assert.False(vm.HasMatches);
        Assert.Equal(0, vm.CurrentIndex);
        vm.NextMatch();
        Assert.Equal(0, vm.CurrentIndex);
    }

    // === The level the queue triages (0.41) ===================================
    //
    // It used to be `arr.Levels.FirstOrDefault()` — level 0, the THINNEST
    // difficulty. On any multi-level chart (every official psarc import, and
    // everything after Tools -> Generate Levels, which deliberately leaves the
    // editor on level N-1) that is not the level anyone is looking at.

    [Fact]
    public void TheQueueTriagesTheACTIVELevelRatherThanTheFirstOne()
    {
        var h = new Harness();
        var thin = h.Level;
        var full = h.AddLevel();

        // The thin level carries one note; the level being edited carries three.
        thin.Notes.Add(Note(0));
        full.Notes.Add(Note(0));
        full.Notes.Add(Note(4));
        full.Notes.Add(Note(8));

        h.ActiveLevel = full;
        var vm = h.Create();

        Assert.Equal(3, vm.Matches(SoundQueueFilter.AnySound).Count);
    }

    [Fact]
    public void SteppingSelectsASoundFromTheACTIVELevel()
    {
        // Stepping SELECTS, so a queue over the wrong level aims every technique
        // verb at a sound the charter cannot see.
        var h = new Harness();
        var thinOnly = Note(0);
        h.Level.Notes.Add(thinOnly);

        var full = h.AddLevel();
        var editing = Note(0);
        full.Notes.Add(editing);
        h.ActiveLevel = full;

        var vm = h.Create();
        vm.NextMatch();

        Assert.Same(editing, h.Selection.SelectedNote);
        Assert.DoesNotContain(thinOnly, h.Selection.SelectedNotes);
    }

    [Fact]
    public void TheHealthTilesCountTheACTIVELevel()
    {
        var h = new Harness();
        h.Arrangement.ChordTemplates.Add(new ChordTemplate
        {
            Id = 0, Name = "", Frets = new Dictionary<int, int> { { 0, 3 } }
        });
        // Level 0 has one problem chord; the level being edited has two.
        h.Level.Chords.Add(Chord(0, 0, (0, 3)));

        var full = h.AddLevel();
        full.Chords.Add(Chord(0, 0, (0, 3)));
        full.Chords.Add(Chord(4, 0, (0, 3)));
        h.ActiveLevel = full;

        var vm = h.Create();
        Assert.Equal(2, vm.UnnamedChordCount);
        Assert.Equal(2, vm.MissingFingeringCount);
    }

    [Fact]
    public void ASingleLevelChartIsUnchanged()
    {
        // The overwhelming majority of charts have one level; this must stay
        // byte-for-byte what it always was.
        var h = new Harness();
        h.Level.Notes.Add(Note(0));
        h.Level.Notes.Add(Note(4));

        Assert.Equal(2, h.Create().Matches(SoundQueueFilter.AnySound).Count);
    }

    [Fact]
    public void ALevelFromAnotherArrangementFallsBackToTheFirstOne()
    {
        // The arrangement and the level are read as two independent seams and
        // can disagree for a dispatcher turn during an arrangement switch. A
        // queue over a level this arrangement does not contain would offer
        // matches that cannot be selected here.
        var h = new Harness();
        h.Level.Notes.Add(Note(0));
        h.Level.Notes.Add(Note(4));

        var foreign = new ChartLevel();
        foreign.Notes.Add(Note(0));
        h.ActiveLevel = foreign;

        Assert.Equal(2, h.Create().Matches(SoundQueueFilter.AnySound).Count);
    }

    [Fact]
    public void ANullActiveLevelFallsBackToTheFirstOne()
    {
        var h = new Harness();
        h.Level.Notes.Add(Note(0));
        h.ActiveLevel = null;

        Assert.Single(h.Create().Matches(SoundQueueFilter.AnySound));
    }

    /// <summary>
    ///     Switching level RE-TRIAGES: the seam is read on every refresh, not
    ///     captured at construction. `InspectorViewModel` already routes
    ///     `ProjectState.ActiveLevel` into `ScheduleIssueRefresh` →
    ///     `Queue.Refresh()`.
    /// </summary>
    [Fact]
    public void ChangingTheActiveLevelRetriagesOnTheNextRefresh()
    {
        var h = new Harness();
        h.Level.Notes.Add(Note(0));
        var full = h.AddLevel();
        full.Notes.Add(Note(0));
        full.Notes.Add(Note(4));

        var vm = h.Create();
        Assert.Equal(1, vm.MatchCount);

        h.ActiveLevel = full;
        vm.Refresh();

        Assert.Equal(2, vm.MatchCount);
    }

    /// <summary>
    ///     …and the HOST must hand it the ambient active level. The seam is a
    ///     delegate, so every test above passes just as well with the host wiring
    ///     `() =&gt; null` — that gap is how the level-0 bug would come straight
    ///     back with a green suite.
    ///     <para>
    ///         A source lint because constructing <c>InspectorViewModel</c> needs
    ///         fourteen services and the app session; same instrument as
    ///         <c>StartupWorkDeferralTests</c>.
    ///     </para>
    /// </summary>
    [Fact]
    public void TheInspectorWiresTheQueueToTheAmbientActiveLevel()
    {
        var source = InspectorSource();

        var start = source.IndexOf("new SoundQueueViewModel(", StringComparison.Ordinal);
        Assert.True(start >= 0, "The inspector must still compose the work queue.");

        var end = source.IndexOf(';', start);
        var construction = source[start..end];

        Assert.Contains("_projectState.ActiveLevel", construction, StringComparison.Ordinal);
        Assert.DoesNotContain("Levels.FirstOrDefault", construction, StringComparison.Ordinal);
        Assert.DoesNotContain("Levels[0]", construction, StringComparison.Ordinal);
    }

    /// <summary>
    ///     One refresh, one read of each seam — and therefore ONE ordered sound
    ///     list and ONE index of the issue back-references.
    ///     <para>
    ///         It used to be eight of each: <c>Refresh</c> asked
    ///         <c>Matches(filter).Count</c> for all seven filters and then
    ///         <c>RecomputePosition</c> asked once more, and every one of those
    ///         calls rebuilt the level's sound list from scratch. This runs at
    ///         <c>Background</c> priority on the UI thread on every committed
    ///         edit, whether or not the Queue tab is visible.
    ///     </para>
    ///     <para>
    ///         Structural rather than timed on purpose. The eighth read is
    ///         invisible from the outside — the answers were always right, it
    ///         just cost eight times what it had to.
    ///     </para>
    /// </summary>
    [Fact]
    public void ARefresh_ReadsEachSeamExactlyOnce()
    {
        var h = new Harness();
        for (var i = 0; i < 40; i++) h.Level.Notes.Add(Note(i));
        h.Issues.Add(new ArrangementIssue(ArrangementIssueType.NoteFretBelowCapo, "", Note: h.Level.Notes[0]));

        var vm = h.Create();
        h.ResetSeamCounts();

        vm.Refresh();

        Assert.Equal(1, h.LevelReads);
        Assert.Equal(1, h.IssueReads);
    }

    /// <summary>
    ///     …and the counts are still right, which is what makes the single read
    ///     a refactor rather than a behaviour change. Three of the seven filters
    ///     read the issue list; all three have to agree with the per-filter scan
    ///     they replaced.
    /// </summary>
    [Fact]
    public void ARefresh_StillCountsEveryFilterCorrectly()
    {
        var h = new Harness();
        h.Arrangement.ChordTemplates.Add(new ChordTemplate
        {
            Id = 0,
            Frets = new Dictionary<int, int> { { 0, 3 }, { 1, 5 } },
            Fingers = new Dictionary<int, int> { { 0, 1 } }
        });
        var blocking = Note(0);
        var unanchored = Note(4);
        var clean = Note(8);
        h.Level.Notes.Add(blocking);
        h.Level.Notes.Add(unanchored);
        h.Level.Notes.Add(clean);
        h.Level.Chords.Add(Chord(2, 0, (0, 3), (1, 5)));   // missing a finger, unnamed
        h.Level.Chords.Add(Chord(6, -1, (0, 3)));          // untemplated
        h.Issues.Add(new ArrangementIssue(ArrangementIssueType.NoteFretBelowCapo, "", Note: blocking));
        h.Issues.Add(new ArrangementIssue(ArrangementIssueType.NoteWithoutAnchor, "", Note: unanchored));
        // Advisory: counts under HasIssue and nowhere else, so the three
        // issue-reading filters cannot be satisfied by one shared answer.
        h.Issues.Add(new ArrangementIssue(ArrangementIssueType.HopoIntoSameNote, "", Note: clean));

        var vm = h.Create();
        vm.Refresh();

        int CountOf(SoundQueueFilter filter) => vm.Filters.Single(f => f.Filter == filter).Count;

        Assert.Equal(5, CountOf(SoundQueueFilter.AnySound));
        Assert.Equal(1, CountOf(SoundQueueFilter.MissingFingering));
        Assert.Equal(1, CountOf(SoundQueueFilter.UnnamedChord));
        Assert.Equal(1, CountOf(SoundQueueFilter.Untemplated));
        Assert.Equal(1, CountOf(SoundQueueFilter.NoAnchorCoverage));
        Assert.Equal(3, CountOf(SoundQueueFilter.HasIssue));
        // NoteWithoutAnchor is an export blocker too — the subset is
        // {below-capo, no-anchor}, not {below-capo}.
        Assert.Equal(2, CountOf(SoundQueueFilter.BlocksExport));
    }

    /// <summary>
    ///     The allocation half of the same claim, metered on THIS thread. The old
    ///     shape allocated eight copies of the whole sound list plus one closure
    ///     per sound per issue-reading filter; what is pinned is that the cost no
    ///     longer scales with (sounds x filters).
    /// </summary>
    [Fact]
    public void ARefresh_DoesNotAllocateACopyOfTheLevelPerFilter()
    {
        var h = new Harness();
        for (var i = 0; i < 4_000; i++) h.Level.Notes.Add(Note(i));
        for (var i = 0; i < 200; i++)
            h.Issues.Add(new ArrangementIssue(ArrangementIssueType.HopoIntoSameNote, "", Note: h.Level.Notes[i]));

        var vm = h.Create();
        vm.Refresh(); // warm the localization lookups and the filter records

        var before = GC.GetAllocatedBytesForCurrentThread();
        vm.Refresh();
        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;

        // Eight ordered 4,000-element lists alone are ~256 KB of references on
        // x64 before the OrderBy buffers; one list plus three filter results is
        // comfortably under a quarter of that.
        Assert.True(bytes < 512 * 1024,
            $"one refresh over 4,000 sounds allocated {bytes} bytes — the per-filter rebuild is back.");
    }

    /// <summary>
    ///     A refresh raises NO <c>CollectionChanged</c> on <c>Filters</c> after
    ///     the first build.
    ///     <para>
    ///         It used to be <c>Clear()</c> + seven <c>Add</c>s — eight
    ///         notifications into a bound <c>ComboBox</c> per committed edit,
    ///         with the popup's containers torn down and rebuilt each time. The
    ///         counts are updated on the SAME seven instances now, so only
    ///         <c>Display</c> changes.
    ///     </para>
    /// </summary>
    [Fact]
    public void ARefresh_DoesNotRebuildTheFilterList()
    {
        var h = new Harness();
        h.Level.Notes.Add(Note(0));
        var vm = h.Create();

        var options = vm.Filters.ToList();
        Assert.Equal(Enum.GetValues<SoundQueueFilter>().Length, options.Count);

        var raised = 0;
        vm.Filters.CollectionChanged += (_, _) => raised++;

        // A real change: one more sound, so AnySound's count moves.
        h.Level.Notes.Add(Note(4));
        vm.Refresh();

        Assert.Equal(0, raised);
        // Same instances, in the same order — that is what keeps the ComboBox's
        // SelectedItem pointing at something the list still contains.
        for (var i = 0; i < options.Count; i++) Assert.Same(options[i], vm.Filters[i]);
        Assert.Equal(2, vm.Filters.Single(f => f.Filter == SoundQueueFilter.AnySound).Count);
    }

    /// <summary>…and the count change is still VISIBLE: <c>Display</c> is what
    /// the picker's item template binds, so the option has to say it changed.</summary>
    [Fact]
    public void AChangedCount_RaisesDisplayOnTheOption()
    {
        var h = new Harness();
        h.Level.Notes.Add(Note(0));
        var vm = h.Create();

        var any = vm.Filters.Single(f => f.Filter == SoundQueueFilter.AnySound);
        var changed = new List<string?>();
        any.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        h.Level.Notes.Add(Note(4));
        vm.Refresh();

        Assert.Contains(nameof(SoundQueueFilterOption.Display), changed);
        Assert.Contains("(2)", any.Display);
    }

    /// <summary>
    ///     Marked stale, the queue does no work at all — not even a seam read —
    ///     until something brings it current. The host marks it while the Sound
    ///     tab is hidden.
    /// </summary>
    [Fact]
    public void AStaleQueue_DoesNoWorkUntilItIsBroughtCurrent()
    {
        var h = new Harness();
        h.Level.Notes.Add(Note(0));
        var vm = h.Create();

        h.Level.Notes.Add(Note(4));
        h.ResetSeamCounts();
        vm.MarkStale();

        Assert.True(vm.IsStale);
        Assert.Equal(0, h.LevelReads);
        Assert.Equal(0, h.IssueReads);
        Assert.Equal(1, vm.MatchCount);

        vm.EnsureCurrent();

        Assert.False(vm.IsStale);
        Assert.Equal(2, vm.MatchCount);
        Assert.True(h.LevelReads > 0);

        // …and a second EnsureCurrent is free.
        h.ResetSeamCounts();
        vm.EnsureCurrent();
        Assert.Equal(0, h.LevelReads);
    }

    /// <summary>
    ///     The two step KEYBINDS fire from any tab, so a deferral may not change
    ///     what they do: a stale queue settles itself before stepping.
    /// </summary>
    [Fact]
    public void SteppingFromAStaleQueue_SettlesItFirst()
    {
        var h = new Harness();
        var first = Note(0);
        var second = Note(4);
        h.Level.Notes.Add(first);

        var vm = h.Create();
        vm.NextMatch();
        Assert.Same(first, h.Selection.SelectedNote);

        h.Level.Notes.Add(second);
        vm.MarkStale();
        vm.NextMatch();

        Assert.False(vm.IsStale);
        Assert.Same(second, h.Selection.SelectedNote);
        Assert.Equal(2, vm.MatchCount);
    }

    /// <summary>The host's half of the deferral, as a source lint for the same
    /// reason the level-seam guard above is one.</summary>
    [Fact]
    public void TheInspectorDefersTheQueueRefreshUntilTheSoundTabIsShowing()
    {
        // Comments stripped: the paragraph above each of these lines explains
        // the rule, and a comment must not be able to satisfy the lint.
        var source = System.Text.RegularExpressions.Regex.Replace(
            InspectorSource(), @"^\s*//.*$", "",
            System.Text.RegularExpressions.RegexOptions.Multiline);
        Assert.Contains("if (IsSoundTabSelected) Queue.Refresh();", source);
        Assert.Contains("else Queue.MarkStale();", source);
        Assert.Contains("if (value == SoundTabIndex) Queue.EnsureCurrent();", source);
    }

    /// <summary>The whole <c>InspectorViewModel</c>, which is a set of topic
    /// partials — the composition this guard reads sits in the root today, and
    /// reading only the root is how a lint goes blind when it moves.</summary>
    private static string InspectorSource()
    {
        var dir = UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "ViewModels", "Editor"));
        var files = UiSourceTree.GetFiles(dir, "InspectorViewModel*.cs");
        Assert.NotEmpty(files);
        return string.Join("\n", files.Select(UiSourceTree.ReadAllText));
    }
}
