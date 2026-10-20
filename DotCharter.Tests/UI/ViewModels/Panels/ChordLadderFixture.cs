using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Collisions;
using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.Highway;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Targets;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Tone.Diagnostics;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Editor.Inspector;
using DotCharter.UI.ViewModels.Panels;

namespace DotCharter.Tests.UI.ViewModels.Panels;

/// <summary>
///     The "Apply to" ladder fixture of <c>ChordTechniqueApplyToTests</c> (#581), with the doors #604
///     adds: the REAL <see cref="HighwayEditingService" /> the keyboard's technique verbs run on,
///     wired to the real chord panel through the inspector's own adapter
///     (<see cref="ChordTechniqueKeyboardReach" />), over the same project, selection and history.
///     <para>
///         Two levels; the shape A5 with a bar shared by the two selected chords and an unbound strum
///         under it; the same frets on another template; the grip two frets up; an unrelated shape no
///         rung may touch. Expected technique reach per rung: 1 / 2 / 5 / 8 chords.
///     </para>
/// </summary>
internal sealed class ChordLadderFixture
{
    public ChordLadderFixture(bool keyboardReach = true)
    {
        var logger = new FakeAppLogger();
        History = new HistoryService(logger);
        Arrangement.Levels.Add(new ChartLevel());
        Arrangement.Levels.Add(new ChartLevel());
        for (var i = 0; i < 32; i++) Project.Beats.Add(new BeatMarker { Time = i * 500.0, Bpm = 120 });
        Project.Arrangements.Add(Arrangement);
        State.ActiveProject = Project;
        State.ActiveArrangement = Arrangement;
        State.ActiveLevel = Arrangement.Levels[0];

        var transport = new TransportViewModel(new FakeAudioPlaybackService(), State, Config, FakeLocalization.KeysOnly());
        Editor = new ChartEditorViewModel(State, transport, History, Selection, new TempoService(logger),
            new TrackContextRegistry(State, Selection), FakeLocalization.KeysOnly());

        var a5 = Template(0, "A5", 5);
        var sameFrets = Template(1, "A5 (alt)", 5);
        var movedUp = Template(2, "B5", 7);
        var unrelated = Template(3, "E5", 0);

        First = AddChord(0, a5.Id, 5, 0);
        Second = AddChord(0, a5.Id, 5, 1);
        AddChord(0, a5.Id, 5, 2);
        AddChord(0, -1, 5, 3); // an unbound strum under the shared bar
        Bar = AddHandShape(0, a5.Id, 0, 4);
        AddChord(0, sameFrets.Id, 5, 8);
        AddChord(0, movedUp.Id, 7, 10);
        AddHandShape(0, movedUp.Id, 10, 11);
        Unrelated = AddChord(0, unrelated.Id, 0, 12);
        AddChord(1, a5.Id, 5, 0);
        AddHandShape(1, a5.Id, 0, 2);
        AddChord(1, sameFrets.Id, 5, 4);

        Panel = new ChordTemplateEditorViewModel(Editor, History, new FingeringGenerationService(),
            FakeLocalization.English(), new InlineDispatcher(), projectState: State)
        {
            ApplyFamily = ChordApplyFamily.Technique
        };

        var scope = new EditingScope(State);
        Keys = new HighwayEditingService(State, Selection, History, new StrictCollisionService(), Config, scope,
            new EditTargetService(Selection, scope, Config), logger);
        if (keyboardReach)
        {
            // The inspector's wiring, with the panel's own TechniqueTargets in place of the inspector's
            // LadderTargets (which adds only "a chord is in the selection").
            Keys.TechniqueReach = new ChordTechniqueKeyboardReach(Panel.TechniqueTargets, () => LadderOnScreen,
                () => Selection.SelectedChords, chords => Reported.Add(chords));
        }

        Selection.SelectedChords.Add(First);
        Selection.SelectedChords.Add(Second);
    }

    public HistoryService History { get; }
    public SelectionService Selection { get; } = new();
    public ProjectState State { get; } = new();
    public ChartProject Project { get; } = new();
    public FakeConfigurationService Config { get; } = new();
    public Arrangement Arrangement { get; } = new() { Name = "Lead", ArrangementType = ArrangementType.Lead };
    public ChartEditorViewModel Editor { get; }
    public ChordTemplateEditorViewModel Panel { get; }
    public HighwayEditingService Keys { get; }
    public Chord First { get; }
    public Chord Second { get; }
    public Chord Unrelated { get; }
    public HandShape Bar { get; }

    /// <summary>Whether a ladder host is on screen (<c>ChordApplyPreview.IsHostShown</c> in the app).</summary>
    public bool LadderOnScreen { get; set; } = true;

    /// <summary>What the keyboard told the status rail it reached, per verb.</summary>
    public List<IReadOnlyList<Chord>> Reported { get; } = [];

    public IEnumerable<Chord> AllChords => Arrangement.Levels.SelectMany(l => l.Chords);

    public static TheoryData<ChordApplyRung, int> Rungs() => new()
    {
        { ChordApplyRung.ThisChord, 1 },
        { ChordApplyRung.Selected, 2 },
        { ChordApplyRung.EveryUse, 5 }, // 4 bound on two levels + the unbound strum under the selected bar
        { ChordApplyRung.SameGrip, 8 }  // + the same frets on both levels + the grip two frets up
    };

    public void Choose(ChordApplyRung rung) => Panel.ChooseApplyRungCommand.Execute(rung);

    /// <summary>Every chord of every level, by identity, with the state a technique or bend can change.</summary>
    public Dictionary<Chord, string> Fingerprints() =>
        AllChords.ToDictionary<Chord, Chord, string>(c => c, c =>
                $"{c.TemplateId}|{c.Accent}|" + string.Join(",", c.ChordNotes.OrderBy(n => n.StringIndex)
                    .Select(n => $"{n.StringIndex}:{n.Fret}:{n.Mute}:{n.Vibrato}:" +
                                 string.Join("/", n.BendValues.Select(b => $"{b.Position}@{b.Step}")))),
            ReferenceEqualityComparer.Instance);

    public HashSet<Chord> ChangedSince(Dictionary<Chord, string> before)
    {
        var after = Fingerprints();
        return AllChords.Where(c => before[c] != after[c]).ToHashSet<Chord>(ReferenceEqualityComparer.Instance);
    }

    public List<ChordTemplate> TemplateSnapshot() => Arrangement.ChordTemplates.Select(t => t.Clone()).ToList();

    public void AssertNoTemplateChanged(List<ChordTemplate> before)
    {
        Assert.Equal(before.Count, Arrangement.ChordTemplates.Count);
        foreach (var (was, now) in before.Zip(Arrangement.ChordTemplates))
            Assert.True(was.Equals(now) && was.Id == now.Id && was.NopInRs == now.NopInRs, $"template {now.Name} changed");
    }

    private ChordTemplate Template(int id, string name, int root)
    {
        var t = new ChordTemplate { Id = id, Name = name };
        t.Frets[0] = root;
        t.Frets[1] = root + 2;
        t.Frets[2] = root + 2;
        Arrangement.ChordTemplates.Add(t);
        return t;
    }

    private Chord AddChord(int level, int templateId, int root, int beat)
    {
        var chord = new Chord { TemplateId = templateId, Position = new FractionalPosition(beat) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = root });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = root + 2 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 2, Fret = root + 2 });
        Arrangement.Levels[level].Chords.Add(chord);
        return chord;
    }

    private HandShape AddHandShape(int level, int templateId, int start, int end)
    {
        var shape = new HandShape
        {
            TemplateId = templateId, Position = new FractionalPosition(start), EndPosition = new FractionalPosition(end)
        };
        Arrangement.Levels[level].HandShapes.Add(shape);
        return shape;
    }

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
