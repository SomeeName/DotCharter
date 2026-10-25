using DotCharter.Presentation.Contracts.Editor;
using DotCharter.Presentation.Screens.Editor;
using DotCharter.Services.Editing.ChordShapes;
using DotCharter.Tests.Presentation.Dialogs;

namespace DotCharter.Tests.Presentation.Editor;

/// <summary>
///     The Edit shape dialog over a real 16-beat chart: the scopes state their reach before anything
///     is applied, the song strip lights the uses the chosen scope reaches (and the timeline marks
///     follow), the one sentence and the Apply caption follow the scope, a grip that already exists is
///     named, and Apply is ONE undo step that closes the dialog with true.
/// </summary>
public sealed class ShapeEditViewModelTests
{
    private readonly ChartEditHarness _h = new();
    private readonly RecordingMarks _marks = new();
    private readonly ChordTemplate _am;
    private readonly ChordTemplate _other;
    private readonly List<Chord> _chords = [];

    public ShapeEditViewModelTests()
    {
        var arrangement = _h.Editor.ActiveArrangement!;
        _am = Template(0, "Am", new() { [1] = 0, [2] = 2, [3] = 2, [4] = 1, [5] = 0 });
        _other = Template(1, "Am7", new() { [1] = 0, [2] = 2, [3] = 0, [4] = 1, [5] = 0 });
        foreach (var beat in new[] { 0, 4, 9, 13 })
        {
            var chord = new Chord { Position = new FractionalPosition(beat), TemplateId = _am.Id };
            foreach (var (s, f) in _am.Frets) chord.ChordNotes.Add(new ChordNote { StringIndex = s, Fret = f });
            arrangement.Levels[0].Chords.Add(chord);
            _chords.Add(chord);
        }
    }

    private ShapeEditViewModel Open(Chord? origin)
    {
        var vm = new ShapeEditViewModel(_h.Services, _marks);
        vm.Configure(new ShapeEditRequest(_am, origin));
        return vm;
    }

    private ChordTemplate Template(int id, string name, Dictionary<int, int> frets)
    {
        var template = new ChordTemplate { Id = id, Name = name, Frets = frets };
        _h.Editor.ActiveArrangement!.ChordTemplates.Add(template);
        return template;
    }

    [Fact]
    public void TheScopesStateTheirReach_AndEveryUseIsTheDefault()
    {
        var vm = Open(_chords[1]);

        Assert.Equal(["This chord", "Selection", "Every use"], vm.Scopes.Select(s => s.Label));
        Assert.Equal([true, false, true], vm.Scopes.Select(s => s.IsAvailable));
        Assert.Equal([false, false, true], vm.Scopes.Select(s => s.IsSelected));
        Assert.Equal("4 chords", vm.Scopes[2].Detail);
        Assert.Equal("1 chord · bar 2", vm.Scopes[0].Detail);
        Assert.Equal("The chord at bar 2", vm.OpenedFromText);
        Assert.Equal(6, vm.StringCount);
        Assert.Equal(["E", "A", "D", "G", "B", "e"], vm.Strings.Select(s => s.StringName));
    }

    [Fact]
    public void ChoosingAScope_RelightsTheStripAndTheMarks_AndRewritesTheSentenceAndTheCaption()
    {
        var vm = Open(_chords[1]);
        vm.Strings[2].Fret = "0";

        Assert.Equal([true, true, true, true], vm.UseBars.Select(b => b.IsReached));
        Assert.Equal("“Am” itself changes, so every use plays the new grip.", vm.ResultText);
        Assert.Equal("Apply to every use (4)", vm.ApplyLabel);
        Assert.Equal(4, _marks.Chords.Count);

        vm.Scopes[0].SelectCommand.Execute(null);

        Assert.Equal([false, true, false, false], vm.UseBars.Select(b => b.IsReached));
        Assert.Equal([true, true, true, true], vm.UseBars.Select(b => b.IsUse));
        Assert.Equal([false, true, false, false], vm.UseBars.Select(b => b.IsOrigin));
        Assert.Equal("1 chord change · 3 keep “Am”", vm.PreviewText);
        Assert.Equal("A new shape is made for this chord; the other 3 keep “Am”.", vm.ResultText);
        Assert.Equal("Apply to this chord", vm.ApplyLabel);
        Assert.Equal([_chords[1]], _marks.Chords);
        Assert.True(vm.Scopes[0].IsSelected);
        Assert.False(vm.Scopes[2].IsSelected);
    }

    [Fact]
    public void ApplyingEveryUse_ChangesTheShapeInOneUndoStep_AndClosesWithTrue()
    {
        var vm = Open(_chords[1]);
        vm.Strings[2].Fret = "0";
        vm.Strings[3].Fret = "x";

        vm.ApplyCommand.Execute(null);

        Assert.True(ChartEditHarness.Answer(vm.Result));
        Assert.Equal(0, _am.Frets[2]);
        Assert.False(_am.Frets.ContainsKey(3));
        Assert.Single(_h.History.GetUndoSteps());
        Assert.True(_marks.Cleared);
        _h.History.Undo();
        Assert.Equal(2, _am.Frets[2]);
    }

    [Fact]
    public void ApplyingThisChord_MakesAShapeForThatChordOnly()
    {
        var vm = Open(_chords[2]);
        vm.Strings[5].Fret = "3";
        vm.Scopes[0].SelectCommand.Execute(null);

        vm.ApplyCommand.Execute(null);

        var templates = _h.Editor.ActiveArrangement!.ChordTemplates;
        Assert.Equal(3, templates.Count);
        Assert.NotEqual(_am.Id, _chords[2].TemplateId);
        Assert.Equal([_am.Id, _am.Id, _am.Id], new[] { _chords[0], _chords[1], _chords[3] }.Select(c => c.TemplateId));
        Assert.Single(_h.History.GetUndoSteps());
    }

    [Fact]
    public void ACancel_WritesNothing_ClosesWithFalse_AndTakesTheMarksAway()
    {
        var vm = Open(_chords[1]);
        vm.Strings[2].Fret = "0";

        vm.CancelCommand.Execute(null);

        Assert.False(ChartEditHarness.Answer(vm.Result));
        Assert.Equal(2, _am.Frets[2]);
        Assert.Empty(_h.History.GetUndoSteps());
        Assert.True(_marks.Cleared);
    }

    [Fact]
    public void AGripThatAlreadyExists_IsNamed_AndApplyingJoinsIt()
    {
        var vm = Open(null);
        Assert.False(vm.HasMatch);
        Assert.False(vm.Scopes[0].IsAvailable);

        vm.Strings[3].Fret = "0";

        Assert.True(vm.HasMatch);
        Assert.Equal("These frets and fingers are already “Am7” (0 uses).", vm.MatchText);
        Assert.Equal("The 4 chords move onto “Am7”; no new shape is made.", vm.ResultText);
        vm.UseMatchCommand.Execute(null);

        Assert.True(ChartEditHarness.Answer(vm.Result));
        Assert.All(_chords, c => Assert.Equal(_other.Id, c.TemplateId));
        Assert.DoesNotContain(_am, _h.Editor.ActiveArrangement!.ChordTemplates);
        Assert.Single(_h.History.GetUndoSteps());
    }

    [Fact]
    public void TheHandShapeSwitch_RewritesItsLine()
    {
        var arrangement = _h.Editor.ActiveArrangement!;
        arrangement.Levels[0].HandShapes.Add(new HandShape
        {
            Position = new FractionalPosition(4), EndPosition = new FractionalPosition(8), TemplateId = _am.Id
        });
        var vm = Open(_chords[1]);
        vm.Strings[2].Fret = "0";
        vm.Scopes[0].SelectCommand.Execute(null);

        Assert.Equal("1 hand shape over these chords follows the new grip.", vm.HandShapeSyncText);
        vm.KeepHandShapesInSync = false;
        Assert.Equal("1 hand shape keeps the old grip and will no longer match its chord.", vm.HandShapeSyncText);
    }

    private sealed class RecordingMarks : IChordUseMarks
    {
        public IReadOnlyList<Chord> Chords { get; private set; } = [];

        public bool Cleared { get; private set; }

        public void Mark(IReadOnlyList<Chord> chords, IReadOnlyList<HandShape> handShapes)
        {
            Chords = chords;
            Cleared = false;
        }

        public void Clear()
        {
            Chords = [];
            Cleared = true;
        }
    }
}
