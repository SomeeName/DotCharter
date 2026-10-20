using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Catalog;
using DotCharter.Services.Editing.Collisions;
using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.Highway;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Selection.Patterns;
using DotCharter.Services.Editing.Targets;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Services.Editing.Tracks;

namespace DotCharter.Tests.Services.Editing;

/// <summary>
///     A match pattern is derived from a SEED (the app-wide selection) and run
///     against a SOURCE (the focused track). Nothing compared the two.
///     <para>
///         Select a shape on strings 3/4/5 of a six-string Lead, focus the Bass,
///         and <c>StringRule.Exact</c> compares literal string indices against a
///         four-string arrangement while <c>StringRule.Relative</c> computes
///         offsets from a six-string origin. Both are read-only, so nothing is
///         corrupted — but the answer is silently wrong, and the only thing the
///         Pattern Builder showed for it was a smaller number, which reads as
///         "these definitions are not useful".
///     </para>
/// </summary>
public sealed class PatternSeedArrangementTests
{
    /// <summary>The seed lives on the focused track: business as usual.</summary>
    [Fact]
    public void ASeedOnTheFocusedTrack_IsNotReportedAsForeign()
    {
        var harness = new Harness();
        var note = harness.AddLeadNote(stringIndex: 3, fret: 5, beat: 0);
        harness.AddLeadNote(stringIndex: 3, fret: 5, beat: 4);
        harness.Selection.SelectedNotes.Add(note);

        Assert.False(harness.Coordinator.PatternSeedIsFromAnotherArrangement());
        Assert.True(harness.Coordinator.CountPatternMatches(SameFrets()) > 0);
    }

    /// <summary>
    ///     …and one that does not: the six-string seed is still selected, the
    ///     focused track is the four-string bass, and the pattern must refuse
    ///     rather than compare string 5 against an arrangement that has none.
    /// </summary>
    [Fact]
    public void ASeedFromAnotherArrangement_IsReported_AndEveryVerbRefuses()
    {
        // Index 2 is the D string on a six-string guitar and the G string on a
        // four-string bass, so these WOULD match on the raw indices — which is
        // precisely the fault: identical numbers, different strings.
        var harness = new Harness();
        var leadNote = harness.AddLeadNote(stringIndex: 2, fret: 7, beat: 0);
        harness.Selection.SelectedNotes.Add(leadNote);
        harness.FocusBass();
        Assert.Empty(harness.Selection.SelectedNotes);
        // Track switching clears the selection. Inject a stale seed explicitly
        // to exercise the coordinator's independent defensive check.
        harness.Selection.SelectedNotes.Add(leadNote);
        harness.AddBassNote(stringIndex: 2, fret: 7, beat: 0);
        harness.AddBassNote(stringIndex: 2, fret: 7, beat: 4);

        Assert.True(harness.Coordinator.PatternSeedIsFromAnotherArrangement());
        Assert.Equal(0, harness.Coordinator.CountPatternMatches(SameFrets()));
        Assert.Equal(0, harness.Coordinator.SelectByPattern(SameFrets()));
        Assert.Null(harness.Coordinator.MoveToPatternMatch(SameFrets(), 0, 1));

        // …and the refusal did not touch the selection.
        Assert.Single(harness.Selection.SelectedNotes);
    }

    /// <summary>
    ///     No seed at all is a DIFFERENT state with its own readout, so this
    ///     predicate must not claim it — two reasons competing for one strip is
    ///     how a dialog ends up saying the wrong thing.
    /// </summary>
    [Fact]
    public void NoSeedAtAll_IsNotAForeignSeed()
    {
        var harness = new Harness();
        harness.AddLeadNote(stringIndex: 3, fret: 5, beat: 0);

        Assert.False(harness.Coordinator.PatternSeedIsFromAnotherArrangement());
    }

    /// <summary>
    ///     The membership test is ANY, not ALL: a selection that legitimately
    ///     reaches past the focused level still has a foot in it, and refusing
    ///     that would break the feature to fix a narrower bug.
    /// </summary>
    [Fact]
    public void ASeedThatOnlyPARTLYBelongsHere_IsStillUsable()
    {
        var harness = new Harness();
        var mine = harness.AddLeadNote(stringIndex: 3, fret: 5, beat: 0);
        harness.AddLeadNote(stringIndex: 3, fret: 5, beat: 4);
        harness.FocusBass();
        var foreign = harness.AddBassNote(stringIndex: 2, fret: 5, beat: 0);
        harness.FocusLead();

        harness.Selection.SelectedNotes.Add(mine);
        harness.Selection.SelectedNotes.Add(foreign);

        Assert.False(harness.Coordinator.PatternSeedIsFromAnotherArrangement());
    }

    private static MatchPattern SameFrets() => new MatchPattern
    {
        Scope = PatternScope.Notes,
        Fret = FretRule.Exact,
        Strings = StringRule.Exact,
        Grouping = PatternGrouping.Individually
    }.Normalized();

    private sealed class Harness
    {
        public Harness()
        {
            var logger = new FakeAppLogger();
            var history = new HistoryService(logger);
            var config = new FakeConfigurationService();
            var scope = new EditingScope(State);
            var highway = new HighwayEditingService(State, Selection, history, new StrictCollisionService(),
                config, scope, new EditTargetService(Selection, scope, config), logger);

            var project = new ChartProject();
            for (var i = 0; i < 64; i++) project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120 });

            Lead = new Arrangement { Name = "Lead", ArrangementType = ArrangementType.Lead, StringCount = 6 };
            Lead.Levels.Add(new ChartLevel());
            project.Arrangements.Add(Lead);

            Bass = new Arrangement { Name = "Bass", ArrangementType = ArrangementType.Bass, StringCount = 4 };
            Bass.Levels.Add(new ChartLevel());
            project.Arrangements.Add(Bass);

            State.ActiveProject = project;
            FocusLead();

            var tracks = new TrackContextRegistry(State, Selection);
            var catalogDir = Path.Combine(Path.GetTempPath(), "dotcharter-patternseed-tests-no-such-dir");

            Coordinator = new GlobalEditingCoordinator(State, Selection, history,
                new ClipboardService(logger), new TempoService(logger), new BeatScaleService(logger),
                highway, config, new SelectLikeService(logger), new FingeringGenerationService(),
                tracks, new PhraseCatalogService(catalogDir, new RscpChartSerializer(), logger), logger,
                FakeLocalization.English());
        }

        public ProjectState State { get; } = new();
        public SelectionService Selection { get; } = new();
        public Arrangement Lead { get; }
        public Arrangement Bass { get; }
        public GlobalEditingCoordinator Coordinator { get; }

        public void FocusLead()
        {
            State.ActiveArrangement = Lead;
            State.ActiveLevel = Lead.Levels[0];
        }

        public void FocusBass()
        {
            State.ActiveArrangement = Bass;
            State.ActiveLevel = Bass.Levels[0];
        }

        public GuitarNote AddLeadNote(int stringIndex, int fret, int beat) =>
            Add(Lead, stringIndex, fret, beat);

        public GuitarNote AddBassNote(int stringIndex, int fret, int beat) =>
            Add(Bass, stringIndex, fret, beat);

        private static GuitarNote Add(Arrangement arrangement, int stringIndex, int fret, int beat)
        {
            var note = new GuitarNote
            {
                StringIndex = stringIndex,
                Fret = fret,
                Position = new FractionalPosition(beat)
            };
            arrangement.Levels[0].Notes.Add(note);
            return note;
        }
    }
}
