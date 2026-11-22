using DotCharter.Services.Editing.Fingering;
using DotCharter.Tests.Presentation.Dialogs;
using DotCharter.Tone.Diagnostics;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Editor.Inspector;
using DotCharter.UI.ViewModels.Panels;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>The Shape page's grip editor over a real chart editor, undo history and Apply-to ladder (#751).</summary>
internal sealed class GripInspectorRig
{
    private readonly ChartEditHarness _harness = new();

    public GripInspectorRig()
    {
        Ladder = new ChordTemplateEditorViewModel(_harness.Editor, _harness.History, new FingeringGenerationService(),
            FakeLocalization.English(), new InlineDispatcher());
        Vm = new GripInspectorViewModel(_harness.Editor, _harness.State, _harness.History, FakeLocalization.English(),
            Sections, Ladder);
    }

    public HistoryService History => _harness.History;
    public ProjectState State => _harness.State;
    public ChartProject Project => _harness.Project;
    public ChartEditorViewModel Editor => _harness.Editor;
    public SoundSectionsViewModel Sections { get; } = new();
    public ChordTemplateEditorViewModel Ladder { get; }
    public GripInspectorViewModel Vm { get; }
    public Arrangement Arrangement => _harness.Project.Arrangements[0];
    public ChartLevel Level => Arrangement.Levels[0];

    public ChordTemplate Grip(Dictionary<int, int> frets)
    {
        var grip = new ChordTemplate { Id = Arrangement.ChordTemplates.Count, Name = "g", Frets = frets };
        Arrangement.ChordTemplates.Add(grip);
        return grip;
    }

    public HandShape Shape(ChordTemplate grip, int start, int end, int? ghost = null)
    {
        var shape = new HandShape { TemplateId = grip.Id, GhostMask = ghost, Position = new(start), EndPosition = new(end) };
        Level.HandShapes.Add(shape);
        return shape;
    }

    /// <summary>A chord striking every string of <paramref name="grip" /> at <paramref name="position" />.</summary>
    public Chord Strum(FractionalPosition position, ChordTemplate grip)
    {
        var chord = new Chord { Position = position, TemplateId = grip.Id };
        foreach (var (s, fret) in grip.Frets) chord.ChordNotes.Add(new ChordNote { StringIndex = s, Fret = fret });
        Level.Chords.Add(chord);
        return chord;
    }

    public void Select(SoundSelectionKind kind, HandShape[] shapes, Chord[] chords)
    {
        Sections.Kind = kind;
        Sections.ChooseCommand.Execute(SoundSection.Shape);
        var selection = _harness.Editor.Selection;
        selection.ClearSelections();
        foreach (var chord in chords) selection.SelectedChords.Add(chord);
        foreach (var shape in shapes) selection.SelectedHandShapes.Add(shape);
        Assert.True(Vm.IsActive);
        Assert.True(Vm.IsAvailable);
    }

    public GripRow Row(int stringIndex) => Vm.Rows.Single(row => row.StringIndex == stringIndex);

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;
        public void Post(Action action) => action();
        public void PostNormal(Action action) => action();
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
        public Task<T> InvokeAsync<T>(Func<T> func) => Task.FromResult(func());
        public Task<T> InvokeAsync<T>(Func<Task<T>> func) => func();
    }
}
