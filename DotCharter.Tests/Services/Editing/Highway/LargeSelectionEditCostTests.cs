using System.Collections.Specialized;
using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Anchors;
using DotCharter.Services.Editing.Collisions;
using DotCharter.Services.Editing.Highway;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Targets;

namespace DotCharter.Tests.Services.Editing.Highway;

/// <summary>
///     What ONE keyboard verb costs when the selection is large.
///     <para>
///         Two shapes made that grow with the selection for no product reason, and
///         both are structural, so both are pinned here rather than by a stopwatch.
///     </para>
///     <para>
///         <b>The log line.</b> Every verb ends in an <c>_logger.Info</c> whose
///         message spells out every selected sound. <see cref="IAppLogger.Info" />
///         takes an already-interpolated <c>string</c>, so that happened
///         unconditionally, before the logger was asked whether it wanted it:
///         9.16 MB of garbage per keypress on a 5,000-note selection, measured, on
///         the UI thread.
///     </para>
///     <para>
///         <b>The reselection.</b> A committed drag re-selects what it produced.
///         Doing that one <c>Add</c> at a time raises one <c>CollectionChanged</c>
///         per sound, and every one of those fans into a full re-read of the
///         selection (the service re-derives its scalars and raises
///         <c>SelectionChanged</c>; the inspector answers with seven scans) — the
///         quadratic shape <c>SelectionBulkOps</c>' own summary says "has been
///         reintroduced three times".
///     </para>
/// </summary>
public sealed class LargeSelectionEditCostTests
{
    /// <summary>
    ///     The keyboard verbs this file sweeps, each with the prefix of the ONE log
    ///     line it is allowed to write. Every verb here is reachable from a key the
    ///     charter can hold down — and <see cref="SustainAdjust" /> is also the
    ///     timeline mouse wheel (<c>TimelineWheelAdjuster</c>), so one notch is one
    ///     invocation.
    /// </summary>
    public const string AccentToggle = "Selection toggled accent";

    public const string SustainAdjust = "Adjusted sustain";
    public const string AnchorNudge = "Nudged FHP anchor";

    public static TheoryData<string> Verbs => [AccentToggle, SustainAdjust, AnchorNudge];

    private sealed class Harness
    {
        public readonly ChartLevel Level;
        public readonly FakeAppLogger Logger = new();
        public readonly ChartProject Project;
        public readonly SelectionService Selection = new();
        public readonly HighwayEditingService Service;

        public Harness(int noteCount)
        {
            var history = new HistoryService(Logger);
            var state = new ProjectState();
            Project = new ChartProject();
            for (var i = 0; i < noteCount + 8; i++)
            {
                Project.Beats.Add(new BeatMarker
                { Time = i * 500, Bpm = 120, BeatsInMeasure = 4, IsMeasure = i % 4 == 0 });
            }

            var arr = new Arrangement { Name = "Lead", StringCount = 6 };
            Level = new ChartLevel();
            for (var i = 0; i < noteCount; i++)
            {
                Level.Notes.Add(new GuitarNote
                {
                    Position = new FractionalPosition(i + 1),
                    StringIndex = i % 6,
                    Fret = 1 + i % 20
                });
                Level.Anchors.Add(new FretAnchor(new FractionalPosition(i + 1), 1 + i % 12));
            }

            arr.Levels.Add(Level);
            Project.Arrangements.Add(arr);

            state.ActiveProject = Project;
            state.ActiveArrangement = arr;
            state.ActiveLevel = Level;

            var config = new FakeConfigurationService();
            var scope = new EditingScope(state);
            Service = new HighwayEditingService(state, Selection, history, new StrictCollisionService(),
                config, scope, new EditTargetService(Selection, scope, config), Logger);
        }

        /// <summary>Select the first <paramref name="count" /> things the verb acts on.</summary>
        public void Select(string verb, int count)
        {
            if (verb == AnchorNudge)
                SelectionBulkOps.ReplaceWith(Selection.SelectedAnchors, Level.Anchors.Take(count).ToList());
            else
                SelectionBulkOps.ReplaceWith(Selection.SelectedNotes, Level.Notes.Take(count).ToList());
        }

        public void Run(string verb)
        {
            switch (verb)
            {
                case AccentToggle:
                    Service.ToggleSelectedAccent();
                    break;
                case SustainAdjust:
                    Service.AdjustSelectedNotesSustain(1, Project.Beats[^1].Time, 4, Project.Beats);
                    break;
                case AnchorNudge:
                    Assert.True(Service.AdjustSelectedAnchors(1, AnchorNudgeTarget.Fret),
                        "the anchor nudge did not commit, so the log assertions below would be vacuous.");
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(verb), verb, "unknown verb");
            }
        }
    }

    /// <summary>
    ///     The verb's log line stays bounded while the selection grows — and still
    ///     carries the COUNTS, which are the part a log reader uses.
    /// </summary>
    [Theory]
    [MemberData(nameof(Verbs))]
    public void AMutationVerbsLogLine_doesNotGrowWithTheSelection(string verb)
    {
        var small = new Harness(400);
        small.Select(verb, 5);
        small.Run(verb);
        var smallLine = Assert.Single(small.Logger.Entries, e => e.Message.StartsWith(verb));

        var large = new Harness(4_000);
        large.Select(verb, 4_000);
        large.Run(verb);
        var largeLine = Assert.Single(large.Logger.Entries, e => e.Message.StartsWith(verb));

        // The COUNT is the part a log reader actually uses, so it has to survive
        // the cap.
        Assert.Contains("4000", largeLine.Message);
        // 800x the selection, and the line may not grow with it at all. A cap of
        // 20 sounds plus the "…and N more" tail is a few thousand characters; the
        // uncapped line was over a megabyte.
        Assert.True(largeLine.Message.Length < 8_000,
            $"'{verb}' logs {largeLine.Message.Length} chars for a 4,000-item selection — it is spelling out the selection again.");
        Assert.Contains("more", largeLine.Message);
        Assert.True(smallLine.Message.Length < largeLine.Message.Length,
            "a 5-item selection should still print its items in full.");
    }

    /// <summary>
    ///     …and the whole verb emits a bounded NUMBER of lines too, so the cost has
    ///     not simply moved from one long line to a thousand short ones.
    /// </summary>
    [Theory]
    [MemberData(nameof(Verbs))]
    public void AMutationVerb_emitsABoundedNumberOfLogLines(string verb)
    {
        var h = new Harness(4_000);
        h.Select(verb, 4_000);
        var before = h.Logger.Entries.Count;
        h.Run(verb);

        Assert.True(h.Logger.Entries.Count - before < 64,
            $"'{verb}' over a 4,000-item selection wrote {h.Logger.Entries.Count - before} log lines.");
    }

    /// <summary>
    ///     A selection replacement of N sounds raises ONE notification, not N — and
    ///     lands on exactly the same contents, in the same order.
    /// </summary>
    [Fact]
    public void ReplacingTheSelection_raisesOneNotification_andKeepsTheContents()
    {
        var h = new Harness(2_000);
        var wanted = h.Level.Notes.ToList();

        var raised = 0;
        h.Selection.SelectedNotes.CollectionChanged += (_, e) =>
        {
            raised++;
            Assert.Equal(NotifyCollectionChangedAction.Reset, e.Action);
        };

        SelectionBulkOps.ReplaceWith(h.Selection.SelectedNotes, wanted);

        Assert.Equal(1, raised);
        Assert.Equal(wanted.Count, h.Selection.SelectedNotes.Count);
        for (var i = 0; i < wanted.Count; i++)
            Assert.Same(wanted[i], h.Selection.SelectedNotes[i]);
    }

    /// <summary>
    ///     The implied-target promotion (`hand shape` / `pen` rung) appends through
    ///     the bulk helper, so it neither rescans the live selection per candidate
    ///     nor raises a notification per appended sound. Its RESULT is the union it
    ///     always was, in the order the loop produced.
    /// </summary>
    [Fact]
    public void PromotingAnImpliedTarget_appendsInOneNotification_withTheSameUnion()
    {
        var h = new Harness(600);
        var already = h.Level.Notes.Take(300).ToList();
        SelectionBulkOps.ReplaceWith(h.Selection.SelectedNotes, already);

        // The union the `if (!Contains(x)) Add(x)` loop produced: everything
        // already held, then whatever is new, in source order.
        var incoming = h.Level.Notes.Skip(200).Take(200).ToList();
        var expected = new List<GuitarNote>(already);
        foreach (var n in incoming)
        {
            if (!expected.Contains(n))
                expected.Add(n);
        }

        var raised = 0;
        h.Selection.SelectedNotes.CollectionChanged += (_, _) => raised++;
        var added = SelectionBulkOps.AddRange(h.Selection.SelectedNotes, incoming);

        Assert.Equal(1, raised);
        Assert.Equal(expected.Count - already.Count, added);
        Assert.Equal(expected.Count, h.Selection.SelectedNotes.Count);
        for (var i = 0; i < expected.Count; i++)
            Assert.Same(expected[i], h.Selection.SelectedNotes[i]);
    }
}
