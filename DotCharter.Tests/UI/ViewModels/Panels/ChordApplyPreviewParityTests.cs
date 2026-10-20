using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Tracks;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Panels;

namespace DotCharter.Tests.UI.ViewModels.Panels;

/// <summary>
///     The bulk-edit preview (#485) marks EXACTLY what the edit then changes, on
///     every rung of the "Apply to" ladder: the set <see cref="ChordApplyScope.Preview" />
///     answers before a commit is compared with the sounds the commit actually
///     changed, found by fingerprinting every chord and hand shape of every level
///     before and after <c>ChordTemplateEditPlanner.ApplyEdit</c> — an observation
///     of the write, not a second reading of the resolver.
///     <para>
///         The fixture has two levels, a hand shape shared by the selected chords
///         and an unbound strum under it, the same grip on another template and the
///         grip moved two frets up (each with a chord and, the moved one, a bar),
///         and a chord of an unrelated shape that no rung may touch. The edit is a
///         complete re-fingering — the edit that reaches furthest, which is what
///         the ladder's counts and the preview describe.
///     </para>
/// </summary>
public sealed class ChordApplyPreviewParityTests
{
    private readonly HistoryService _history = new(new FakeAppLogger());
    private readonly SelectionService _selection = new();
    private readonly ProjectState _state = new();
    private readonly Arrangement _arr = new() { Name = "Lead", ArrangementType = ArrangementType.Lead };
    private readonly ChordTemplateEditPlanner _planner;
    private readonly ChordTemplate _a5;
    private readonly Chord _first;
    private readonly Chord _second;
    private readonly HandShape _sharedBar;

    public ChordApplyPreviewParityTests()
    {
        var logger = new FakeAppLogger();
        var project = new ChartProject();
        _arr.Levels.Add(new ChartLevel());
        _arr.Levels.Add(new ChartLevel());
        project.Arrangements.Add(_arr);
        _state.ActiveProject = project;
        _state.ActiveArrangement = _arr;
        _state.ActiveLevel = _arr.Levels[0];

        var config = new FakeConfigurationService();
        var transport = new TransportViewModel(new FakeAudioPlaybackService(), _state, config, FakeLocalization.KeysOnly());
        var editor = new ChartEditorViewModel(_state, transport, _history, _selection, new TempoService(logger),
            new TrackContextRegistry(_state, _selection), FakeLocalization.KeysOnly());
        _planner = new ChordTemplateEditPlanner(editor, _history, new FingeringGenerationService(), config, logger);

        _a5 = Template(0, "A5", 5);
        var sameFrets = Template(1, "A5 (alt)", 5);
        var movedUp = Template(2, "B5", 7);
        var unrelated = Template(3, "E5", 0);

        _first = AddChord(0, _a5.Id, 5, 0);
        _second = AddChord(0, _a5.Id, 5, 1);
        AddChord(0, _a5.Id, 5, 2);
        AddChord(0, -1, 5, 3);                       // an unbound strum under the shared bar
        _sharedBar = AddHandShape(0, _a5.Id, 0, 4);
        AddChord(0, sameFrets.Id, 5, 8);
        AddChord(0, movedUp.Id, 7, 10);
        AddHandShape(0, movedUp.Id, 10, 11);
        AddChord(0, unrelated.Id, 0, 12);
        AddChord(1, _a5.Id, 5, 0);
        AddHandShape(1, _a5.Id, 0, 2);
        AddChord(1, sameFrets.Id, 5, 4);
    }

    [Theory]
    [InlineData(ChordApplyRung.ThisChord, 2)]
    [InlineData(ChordApplyRung.Selected, 3)]
    [InlineData(ChordApplyRung.EveryUse, 7)]
    [InlineData(ChordApplyRung.SameGrip, 11)]
    public void ThePreview_IsExactlyWhatTheEditChanges(ChordApplyRung rung, int expected)
    {
        var scope = Scope();
        var preview = Keys(scope.Preview(rung));
        var before = Fingerprints();

        var result = _planner.ApplyEdit(_a5, Refingered(), scope, rung, ChordTemplateEditKind.Fingering);

        Assert.NotNull(result);
        var after = Fingerprints();
        var changed = before.Keys.Where(k => !after.TryGetValue(k, out var now) || now != before[k]).ToHashSet();
        Assert.Equal(expected, preview.Count);
        Assert.Equal(changed.OrderBy(k => k.ToString()), preview.OrderBy(k => k.ToString()));
        Assert.Equal(result.Targets.ChordCount + result.Targets.HandShapeCount, changed.Count);
    }

    /// <summary>The rungs reach different sets — so the parity above is not a
    /// fixture where every rung happens to change the same things.</summary>
    [Fact]
    public void EachRung_PreviewsMoreThanTheOneBelowIt()
    {
        var scope = Scope();
        var sizes = Enum.GetValues<ChordApplyRung>().Select(r => scope.Preview(r).Count).ToList();

        Assert.Equal(sizes.OrderBy(s => s), sizes);
        Assert.Equal(sizes.Count, sizes.Distinct().Count());
    }

    /// <summary>A chord of an unrelated shape is never marked, on any rung.</summary>
    [Fact]
    public void NoRung_PreviewsASoundOfAnotherShape()
    {
        var scope = Scope();
        foreach (var rung in Enum.GetValues<ChordApplyRung>())
            Assert.DoesNotContain(scope.Preview(rung).Chords, c => c.TemplateId == 3);
    }

    /// <summary>The panel's grouping keeps every member: the timeline shows one
    /// level, so the uses on the other one are counted apart.</summary>
    [Fact]
    public void TheHighlight_SplitsThePreviewByLevel()
    {
        var sounds = Scope().Preview(ChordApplyRung.SameGrip);

        var highlight = ChordUseHighlight.From(sounds.Chords, sounds.HandShapes, _arr);

        Assert.Equal(sounds.Count, highlight.Count);
        Assert.Equal(8, highlight.On(_arr.Levels[0].Chords).Count);
        Assert.Equal(3, highlight.ElsewhereThan(_arr.Levels[0].Chords));
    }

    private ChordApplyScope Scope() =>
        _planner.ResolveReach(_a5, new ChordApplySounds([_first], [_sharedBar]),
            new ChordApplySounds([_first, _second], [_sharedBar]))!;

    private ChordTemplate Refingered()
    {
        var state = _a5.Clone();
        state.Fingers[0] = 1;
        state.Fingers[1] = 3;
        state.Fingers[2] = 4;
        return state;
    }

    /// <summary>Every sound of every level, keyed by (level, kind, position) — which
    /// a commit keeps even where it swaps the object for a clone — and printed as
    /// its template id, the template's frets, fingers and name, and its notes.</summary>
    private Dictionary<(int Level, char Kind, FractionalPosition Position), string> Fingerprints()
    {
        var result = new Dictionary<(int, char, FractionalPosition), string>();
        for (var level = 0; level < _arr.Levels.Count; level++)
        {
            foreach (var chord in _arr.Levels[level].Chords)
            {
                var notes = string.Join(",", chord.ChordNotes.OrderBy(n => n.StringIndex).Select(n => $"{n.StringIndex}:{n.Fret}"));
                result[(level, 'c', chord.Position)] = $"{TemplateState(chord.TemplateId)}|{notes}";
            }

            foreach (var handShape in _arr.Levels[level].HandShapes)
                result[(level, 'h', handShape.Position)] = TemplateState(handShape.TemplateId);
        }

        return result;
    }

    private HashSet<(int Level, char Kind, FractionalPosition Position)> Keys(ChordApplySounds sounds)
    {
        var keys = new HashSet<(int, char, FractionalPosition)>();
        for (var level = 0; level < _arr.Levels.Count; level++)
        {
            foreach (var chord in sounds.Chords.Where(c => _arr.Levels[level].Chords.Contains(c)))
                keys.Add((level, 'c', chord.Position));
            foreach (var handShape in sounds.HandShapes.Where(h => _arr.Levels[level].HandShapes.Contains(h)))
                keys.Add((level, 'h', handShape.Position));
        }

        return keys;
    }

    private string TemplateState(int id)
    {
        var t = _arr.ChordTemplates.FirstOrDefault(x => x.Id == id);
        if (t == null) return $"{id}";
        static string Dict(IDictionary<int, int> d) => string.Join(",", d.OrderBy(p => p.Key).Select(p => $"{p.Key}:{p.Value}"));
        return $"{id}|{Dict(t.Frets)}|{Dict(t.Fingers)}|{t.Name}";
    }

    /// <summary>A fully fingered three-string power-chord grip rooted at <paramref name="root" />.</summary>
    private ChordTemplate Template(int id, string name, int root)
    {
        var t = new ChordTemplate { Id = id, Name = name };
        t.Frets[0] = root;
        t.Frets[1] = root + 2;
        t.Frets[2] = root + 2;
        t.Fingers[0] = 1;
        t.Fingers[1] = 3;
        t.Fingers[2] = 3;
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

    private HandShape AddHandShape(int level, int templateId, int start, int end)
    {
        var bar = new HandShape
        {
            TemplateId = templateId, Position = new FractionalPosition(start), EndPosition = new FractionalPosition(end)
        };
        _arr.Levels[level].HandShapes.Add(bar);
        return bar;
    }
}
