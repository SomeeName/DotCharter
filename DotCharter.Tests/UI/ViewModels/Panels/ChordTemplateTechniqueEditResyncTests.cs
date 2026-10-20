using System.Text.RegularExpressions;
using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Tracks;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Panels;

namespace DotCharter.Tests.UI.ViewModels.Panels;

/// <summary>
///     #552: after ANY chord technique edit (a vibrato tick) the Shape card read
///     "Custom" and the "Apply to" ladder read "selected (0)", with the chord
///     still selected and its shape unchanged.
///     <para>
///         Real, not a headless artifact: the inspector's <c>HistoryChanged</c>
///         handler re-synced the panel with <c>SyncChordTemplate</c>, which for a
///         chord naming no real template (TemplateId -1 — every chord of a fresh
///         import until the first save, and every chord of the headless fixture)
///         loaded a fabricated, FRETLESS <c>ChordTemplate { Name = "Custom" }</c>.
///         It ran after the panel's own history handler, so it had the last word,
///         and <c>ChordApplyScope</c> narrows the selection to the chords that
///         carry the shown shape — none carry an empty one.
///     </para>
///     <para>
///         <see cref="InspectorViewModel" /> cannot be built outside the app
///         session, so these drive the panel exactly as the app does — the panel
///         subscribes to history first, the inspector's re-sync after it — with
///         the inspector's own <c>MutateChordsAction</c>, and a source lint pins
///         that the inspector's re-sync IS <see cref="ChordTemplateEditorViewModel.ReloadForSelectedChord" />.
///     </para>
/// </summary>
public sealed class ChordTemplateTechniqueEditResyncTests
{
    [Fact]
    public void AVibratoOnAnUntemplatedChord_KeepsItsShapeAndItsSelection()
    {
        var h = new Harness();
        var chord = h.AddChord(templateId: -1, (2, 7), (3, 9), (4, 9));
        var vm = h.CreateChordEditor();
        h.Selection.SelectedChords.Add(chord);

        Assert.Equal("selected (1)", vm.Ladder.Selected.Label);
        var shownName = vm.ChordName;

        h.ApplyVibrato(chord);

        Assert.True(chord.ChordNotes.All(n => n.Vibrato), "the technique edit did not land — the test drives nothing.");
        Assert.NotNull(vm.Template);
        Assert.Equal(new Dictionary<int, int> { { 2, 7 }, { 3, 9 }, { 4, 9 } }, vm.Template!.Frets);
        Assert.Equal(shownName, vm.ChordName);
        Assert.NotEqual("Custom", vm.ChordName);
        Assert.Equal("selected (1)", vm.Ladder.Selected.Label);
        Assert.Equal(1, vm.Ladder.Selected.Reach.Sounds);
    }

    [Fact]
    public void AVibratoOnATemplatedChord_KeepsItsTemplateAndItsSelection()
    {
        var h = new Harness();
        var a5 = new ChordTemplate
        {
            Id = 4, Name = "A5",
            Frets = new Dictionary<int, int> { { 1, 7 }, { 2, 7 } },
            Fingers = new Dictionary<int, int> { { 1, 1 }, { 2, 3 } }
        };
        h.Arrangement.ChordTemplates.Add(a5);
        var chord = h.AddChord(templateId: 4, (1, 7), (2, 7));
        var vm = h.CreateChordEditor();
        h.Selection.SelectedChords.Add(chord);

        h.ApplyVibrato(chord);

        Assert.Same(a5, vm.Template);
        Assert.Equal("A5", vm.ChordName);
        Assert.Equal("selected (1)", vm.Ladder.Selected.Label);
    }

    /// <summary>The same handler loaded a BLANK template whenever no chord was
    /// selected, so any edit with a hand shape selected emptied its Shape card.</summary>
    [Fact]
    public void AnEditWithAHandShapeSelected_KeepsTheShapesTemplate()
    {
        var h = new Harness();
        var e5 = new ChordTemplate
        {
            Id = 2, Name = "E5",
            Frets = new Dictionary<int, int> { { 0, 0 }, { 1, 2 } }
        };
        h.Arrangement.ChordTemplates.Add(e5);
        var shape = new HandShape { TemplateId = 2, Position = new FractionalPosition(8), EndPosition = new FractionalPosition(9) };
        h.Arrangement.Levels[0].HandShapes.Add(shape);
        var elsewhere = h.AddChord(templateId: -1, (3, 5), (4, 5));
        var vm = h.CreateChordEditor();
        h.Selection.SelectedHandShapes.Add(shape);
        Assert.Same(e5, vm.Template);

        h.ApplyVibrato(elsewhere);

        Assert.Same(e5, vm.Template);
        Assert.Equal("E5", vm.ChordName);
    }

    /// <summary>The behaviour above only protects the app while the inspector's
    /// re-sync is that method and nothing of its own.</summary>
    [Fact]
    public void TheInspectorsResync_IsThePanelsOwn()
    {
        var src = InspectorSource();

        Assert.Matches(@"private void SyncChordTemplate\(\)\s*=>\s*ChordTemplateEditor\.ReloadForSelectedChord\(\);", src);
        Assert.DoesNotContain("new ChordTemplate", src);
        Assert.Contains("SyncChordTemplate();", BodyOf(src, "private void OnHistoryChanged()"));
    }

    private static string InspectorSource()
    {
        var dir = UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "ViewModels", "Editor"));
        var files = UiSourceTree.GetFiles(dir, "InspectorViewModel*.cs");
        Assert.NotEmpty(files);
        var src = string.Join("\n", files.Select(UiSourceTree.ReadAllText));
        src = Regex.Replace(src, @"^\s*//.*$", "", RegexOptions.Multiline);
        return Regex.Replace(src, @"/\*.*?\*/", "", RegexOptions.Singleline);
    }

    private static string BodyOf(string src, string signature)
    {
        var start = src.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"InspectorViewModel no longer declares '{signature}' — repoint this lint.");

        var open = src.IndexOf('{', start);
        var depth = 0;
        for (var i = open; i < src.Length; i++)
        {
            if (src[i] == '{') depth++;
            else if (src[i] == '}' && --depth == 0) return src[open..i];
        }

        throw new InvalidOperationException($"Unbalanced braces after '{signature}'.");
    }

    private sealed class Harness
    {
        public Harness()
        {
            var logger = new FakeAppLogger();
            History = new HistoryService(logger);
            var tempo = new TempoService(logger);
            var config = new FakeConfigurationService();
            var project = new ChartProject();
            Arrangement.Levels.Add(new ChartLevel());
            project.Arrangements.Add(Arrangement);

            State.ActiveProject = project;
            State.ActiveArrangement = Arrangement;
            State.ActiveLevel = Arrangement.Levels[0];

            var transport = new TransportViewModel(Audio, State, config, FakeLocalization.KeysOnly());
            var tracks = new TrackContextRegistry(State, Selection);
            Editor = new ChartEditorViewModel(State, transport, History, Selection, tempo, tracks, FakeLocalization.KeysOnly());
        }

        public Arrangement Arrangement { get; } = new() { Name = "Lead", ArrangementType = ArrangementType.Lead };
        public ProjectState State { get; } = new();
        public SelectionService Selection { get; } = new();
        public FakeAudioPlaybackService Audio { get; } = new();
        public HistoryService History { get; }
        public ChartEditorViewModel Editor { get; }

        public Chord AddChord(int templateId, params (int String, int Fret)[] notes)
        {
            var chord = new Chord { TemplateId = templateId, Position = new FractionalPosition(Arrangement.Levels[0].Chords.Count * 2) };
            foreach (var (s, f) in notes) chord.ChordNotes.Add(new ChordNote { StringIndex = s, Fret = f });
            Arrangement.Levels[0].Chords.Add(chord);
            return chord;
        }

        /// <summary>The panel subscribes to history in its constructor; the
        /// inspector, built after it, subscribes its re-sync second — the order
        /// in the app, where the re-sync has the last word.</summary>
        public ChordTemplateEditorViewModel CreateChordEditor()
        {
            var vm = new ChordTemplateEditorViewModel(Editor, History, new FingeringGenerationService(),
                FakeLocalization.English(), new OnboardingWizardRig.InlineDispatcher());
            History.HistoryChanged += vm.ReloadForSelectedChord;
            return vm;
        }

        /// <summary>What the inspector's vibrato box does: one
        /// <c>MutateChordsAction</c> over clones, through history.</summary>
        public void ApplyVibrato(Chord chord)
        {
            var before = chord.Clone();
            var after = chord.Clone();
            foreach (var note in after.ChordNotes) note.Vibrato = true;
            History.Do(new MutateChordsAction(Editor.PlacedChords, [chord], [before], [after]));
        }
    }
}
