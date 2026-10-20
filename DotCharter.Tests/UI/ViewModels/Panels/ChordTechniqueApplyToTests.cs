using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.Highway;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Services.Export.Rocksmith;
using DotCharter.Tone.Diagnostics;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Panels;

namespace DotCharter.Tests.UI.ViewModels.Panels;

/// <summary>
///     #581: technique and playback edits take the chord panel's "Apply to" ladder. Through the
///     real panel view model (its scope, its ladder, its preview) and the inspector's write
///     (<see cref="ChordTechniqueEdit" />, the ONE action builder the inspector commits through),
///     on every rung, for a whole-chord technique (palm mute on every string) and a per-string one
///     (vibrato behind a one-string mask):
///     <list type="bullet">
///         <item>the chords written are exactly the rung's — and exactly what the preview marked;</item>
///         <item>a per-string technique lands on the masked STRING of every target chord, no other;</item>
///         <item>no template changes, ever;</item>
///         <item>ONE undo node, which puts every level back (deep compare), and redo re-applies it;</item>
///         <item>the export's repeat verdict follows the change.</item>
///     </list>
///     The fixture (as <c>ChordApplyPreviewParityTests</c>'): two levels, the shape A5 with a bar shared
///     by the two selected chords and an unbound strum under it, the same frets on another template,
///     the grip two frets up, and an unrelated shape no rung may touch.
/// </summary>
public sealed class ChordTechniqueApplyToTests
{
    private const int MaskedString = 1;

    private readonly HistoryService _history = new(new FakeAppLogger());
    private readonly SelectionService _selection = new();
    private readonly ProjectState _state = new();
    private readonly ChartProject _project = new();
    private readonly Arrangement _arr = new() { Name = "Lead", ArrangementType = ArrangementType.Lead };
    private readonly ChartEditorViewModel _editor;
    private readonly ChordTemplateEditorViewModel _panel;
    private readonly Chord _first;
    private readonly Chord _second;
    private readonly Chord _unrelated;

    public ChordTechniqueApplyToTests()
    {
        var logger = new FakeAppLogger();
        _arr.Levels.Add(new ChartLevel());
        _arr.Levels.Add(new ChartLevel());
        for (var i = 0; i < 32; i++) _project.Beats.Add(new BeatMarker { Time = i * 500.0, Bpm = 120 });
        _project.Arrangements.Add(_arr);
        _state.ActiveProject = _project;
        _state.ActiveArrangement = _arr;
        _state.ActiveLevel = _arr.Levels[0];

        var config = new FakeConfigurationService();
        var transport = new TransportViewModel(new FakeAudioPlaybackService(), _state, config, FakeLocalization.KeysOnly());
        _editor = new ChartEditorViewModel(_state, transport, _history, _selection, new TempoService(logger),
            new TrackContextRegistry(_state, _selection), FakeLocalization.KeysOnly());

        var a5 = Template(0, "A5", 5);
        var sameFrets = Template(1, "A5 (alt)", 5);
        var movedUp = Template(2, "B5", 7);
        var unrelated = Template(3, "E5", 0);

        _first = AddChord(0, a5.Id, 5, 0);
        _second = AddChord(0, a5.Id, 5, 1);
        AddChord(0, a5.Id, 5, 2);
        AddChord(0, -1, 5, 3); // an unbound strum under the shared bar
        AddHandShape(0, a5.Id, 0, 4);
        AddChord(0, sameFrets.Id, 5, 8);
        AddChord(0, movedUp.Id, 7, 10);
        AddHandShape(0, movedUp.Id, 10, 11);
        _unrelated = AddChord(0, unrelated.Id, 0, 12);
        AddChord(1, a5.Id, 5, 0);
        AddHandShape(1, a5.Id, 0, 2);
        AddChord(1, sameFrets.Id, 5, 4);

        _panel = new ChordTemplateEditorViewModel(_editor, _history, new FingeringGenerationService(),
            FakeLocalization.English(), new InlineDispatcher(), projectState: _state)
        {
            ApplyFamily = ChordApplyFamily.Technique
        };
        _selection.SelectedChords.Add(_first);
        _selection.SelectedChords.Add(_second);
    }

    public static TheoryData<ChordApplyRung, int, int> Rungs() => new()
    {
        { ChordApplyRung.ThisChord, 1, 1 },
        { ChordApplyRung.Selected, 2, 1 },
        { ChordApplyRung.EveryUse, 5, 2 },  // 4 bound on two levels + the unbound strum under the selected bar
        { ChordApplyRung.SameGrip, 8, 2 }   // + the same frets on both levels + the grip two frets up
    };

    /// <summary>Each rung reaches a different set, and the ladder counts exactly it: chords only
    /// (a hand shape carries no technique), with "all levels" from "every use" on.</summary>
    [Theory]
    [MemberData(nameof(Rungs))]
    public void EachRung_ReachesItsChords_AndTheLadderCountsThem(ChordApplyRung rung, int chords, int levels)
    {
        _panel.ChooseApplyRungCommand.Execute(rung);

        var targets = _panel.TechniqueTargets()!;
        var row = _panel.Ladder.Rows.Single(r => r.Rung == rung);

        Assert.Equal(rung, targets.Rung);
        Assert.Equal(chords, targets.Chords.Count);
        Assert.Equal(chords, row.Reach.Chords);
        Assert.Equal(0, row.Reach.HandShapes);
        Assert.Equal(rung >= ChordApplyRung.EveryUse ? levels : 0, row.Reach.Levels);
        Assert.Equal(rung, _panel.Ladder.Effective);
        Assert.DoesNotContain(_unrelated, targets.Chords);
    }

    /// <summary>The family switch is load-bearing: over the Shape segment the same rungs count
    /// the hand shapes a shape edit rewrites.</summary>
    [Fact]
    public void OverTheShape_TheSameRungCountsHandShapes()
    {
        _panel.ChooseApplyRungCommand.Execute(ChordApplyRung.EveryUse);
        Assert.Equal(0, _panel.Ladder.EveryUse.Reach.HandShapes);

        _panel.ApplyFamily = ChordApplyFamily.Shape;

        Assert.Equal(2, _panel.Ladder.EveryUse.Reach.HandShapes);
        Assert.Equal(5, _panel.Ladder.EveryUse.Reach.Chords);
    }

    /// <summary>"selected" is the WHOLE selection for a technique, as the Technique segment always
    /// edited — a selected chord of another shape included — and the inspector writes it through
    /// its own selection path (<see cref="ChordTechniqueTargets.IsSelection" />).</summary>
    [Fact]
    public void Selected_IsTheWholeSelection_AndTheInspectorsOwnPath()
    {
        _selection.SelectedChords.Add(_unrelated);
        _panel.ChooseApplyRungCommand.Execute(ChordApplyRung.Selected);

        var targets = _panel.TechniqueTargets()!;

        Assert.True(targets.IsSelection);
        Assert.Equal(new[] { _first, _second, _unrelated }, targets.Chords);
        Assert.Equal(3, _panel.Ladder.Selected.Reach.Chords);
    }

    [Theory]
    [MemberData(nameof(Rungs))]
    public void PalmMute_OnEveryString_WritesExactlyTheRung_AndThePreviewMarkedIt(ChordApplyRung rung, int chords,
        int levels)
    {
        _ = levels;
        AssertCommitsExactlyTheRung(rung, chords, StringEditMask.Empty,
            n => n.Mute = MuteType.Palm, n => n.Mute == MuteType.Palm);
    }

    [Theory]
    [MemberData(nameof(Rungs))]
    public void Vibrato_OnOneMaskedString_LandsOnThatStringOfEveryTarget(ChordApplyRung rung, int chords, int levels)
    {
        _ = levels;
        AssertCommitsExactlyTheRung(rung, chords, StringEditMask.FromStrings(MaskedString),
            n => InspectorViewModel.SetVibrato(n, true), n => n.Vibrato);
    }

    /// <summary>ONE history node, on two levels; one undo puts the whole arrangement back
    /// (templates, chords, notes, bars — a deep compare), and redo re-applies the same state.</summary>
    [Theory]
    [InlineData(ChordApplyRung.EveryUse)]
    [InlineData(ChordApplyRung.SameGrip)]
    public void OneUndo_RestoresEveryLevel_AndRedoReappliesIt(ChordApplyRung rung)
    {
        var before = ChartProjectCloner.CloneArrangement(_arr);
        _panel.ChooseApplyRungCommand.Execute(rung);
        var ranOn = Commit(StringEditMask.FromStrings(MaskedString), n => InspectorViewModel.SetVibrato(n, true));
        var after = ChartProjectCloner.CloneArrangement(_arr);

        Assert.Single(_history.GetUndoSteps());
        Assert.Equal(_arr.Levels.Count, ranOn.Distinct().Count());
        Assert.NotEmpty(DeepCompare.Diff("arr", before, after, new HashSet<string>()));

        _history.Undo();
        Assert.Empty(DeepCompare.Diff("arr", before, ChartProjectCloner.CloneArrangement(_arr), new HashSet<string>()));

        _history.Redo();
        Assert.Empty(DeepCompare.Diff("arr", after, ChartProjectCloner.CloneArrangement(_arr), new HashSet<string>()));
    }

    /// <summary>The export's repeat verdict follows a technique the ladder wrote (#571): the second
    /// A5 strum ships as a repeat of the first until vibrato reaches it, and again after the undo.</summary>
    [Fact]
    public void TheRepeatVerdict_FollowsTheTechnique()
    {
        _selection.SelectedChords.Clear();
        _selection.SelectedChords.Add(_second);
        Assert.Equal(RepeatChordReason.Repeat, Verdict(_second)?.Reason);

        _panel.ChooseApplyRungCommand.Execute(ChordApplyRung.ThisChord);
        Commit(StringEditMask.FromStrings(MaskedString), n => InspectorViewModel.SetVibrato(n, true));
        Assert.Equal(new RepeatChordVerdict(RepeatChordReason.Vibrato, MaskedString), Verdict(_second));

        _history.Undo();
        Assert.Equal(RepeatChordReason.Repeat, Verdict(_second)?.Reason);
    }

    private void AssertCommitsExactlyTheRung(ChordApplyRung rung, int expected, StringEditMask mask,
        Action<ChordNote> technique, Func<ChordNote, bool> carries)
    {
        _panel.ChooseApplyRungCommand.Execute(rung);
        var preview = _panel.Preview.Resolved;
        var templates = _arr.ChordTemplates.Select(t => t.Clone()).ToList();
        var before = Fingerprints();

        Commit(mask, technique);

        var after = Fingerprints();
        var changed = _arr.Levels.SelectMany(l => l.Chords).Where(c => before[c] != after[c])
            .ToHashSet<Chord>(ReferenceEqualityComparer.Instance);
        Assert.Equal(expected, changed.Count);
        Assert.True(changed.SetEquals(_panel.TechniqueTargets()!.Chords), "the commit wrote other chords than the rung reaches");
        // The preview marked what the commit changed — nothing while the rung is the selection itself.
        if (rung <= ChordApplyRung.Selected) Assert.True(preview.IsEmpty);
        else Assert.True(changed.SetEquals(preview.Chords), "the preview marked other chords than the commit changed");
        Assert.Empty(preview.HandShapes); // a technique never reaches a bar, so none is marked
        foreach (var chord in changed)
        {
            foreach (var note in chord.ChordNotes)
                Assert.Equal(mask.Includes(note.StringIndex), carries(note));
        }

        // A technique lives on the chord: no template moved.
        foreach (var (was, now) in templates.Zip(_arr.ChordTemplates))
            Assert.True(was.Equals(now) && was.Id == now.Id && was.NopInRs == now.NopInRs, $"template {now.Name} changed");
        Assert.Single(_history.GetUndoSteps());
    }

    /// <summary>The inspector's write: <c>ApplyChordNoteMutation</c>'s mask rule inside
    /// <c>ApplyChordMutationOnLadder</c>'s builder. Answers the levels whose post-pass ran.</summary>
    private List<ChartLevel> Commit(StringEditMask mask, Action<ChordNote> technique)
    {
        var ranOn = new List<ChartLevel>();
        var targets = _panel.TechniqueTargets()!;
        var action = ChordTechniqueEdit.Build(_arr.Levels, _editor.PlacedChords, targets.Chords, c =>
            {
                foreach (var note in InspectorViewModel.MaskedChordNotes(c.ChordNotes, mask)) technique(note);
            }, () => ranOn.Add(_arr.Levels[0]),
            level => () => ranOn.Add(level));
        Assert.NotNull(action);
        _history.Do(action);
        return ranOn;
    }

    private RepeatChordVerdict? Verdict(Chord chord) =>
        InspectorViewModel.ExplainRepeat(_arr.ChordTemplates, _editor.PlacedNotes, _editor.PlacedChords,
            _editor.HandShapes, _editor.Beats, chord);

    private Dictionary<Chord, string> Fingerprints() =>
        _arr.Levels.SelectMany(l => l.Chords).ToDictionary<Chord, Chord, string>(c => c, c =>
                $"{c.TemplateId}|{c.Accent}|" + string.Join(",", c.ChordNotes.OrderBy(n => n.StringIndex)
                    .Select(n => $"{n.StringIndex}:{n.Fret}:{n.Mute}:{n.Vibrato}")),
            ReferenceEqualityComparer.Instance);

    private ChordTemplate Template(int id, string name, int root)
    {
        var t = new ChordTemplate { Id = id, Name = name };
        t.Frets[0] = root;
        t.Frets[1] = root + 2;
        t.Frets[2] = root + 2;
        _arr.ChordTemplates.Add(t);
        return t;
    }

    private Chord AddChord(int level, int templateId, int root, int beat)
    {
        var chord = new Chord { TemplateId = templateId, Position = new FractionalPosition(beat) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = root });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = root + 2 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 2, Fret = root + 2 });
        _arr.Levels[level].Chords.Add(chord);
        return chord;
    }

    private void AddHandShape(int level, int templateId, int start, int end) =>
        _arr.Levels[level].HandShapes.Add(new HandShape
        {
            TemplateId = templateId, Position = new FractionalPosition(start), EndPosition = new FractionalPosition(end)
        });

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;
        public void Post(Action action) => action();
        public void PostNormal(Action action) => action();

        public Task InvokeAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }

        public Task<T> InvokeAsync<T>(Func<T> func) => Task.FromResult(func());
        public Task<T> InvokeAsync<T>(Func<Task<T>> func) => func();
    }
}
