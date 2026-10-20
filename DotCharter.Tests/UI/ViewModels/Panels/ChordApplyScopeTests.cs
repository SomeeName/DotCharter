using DotCharter.UI.ViewModels.Panels;

namespace DotCharter.Tests.UI.ViewModels.Panels;

/// <summary>
///     The pure resolver behind the chord panel's "Apply to" ladder (#484): for
///     each rung and edit kind, exactly which chords, hand shapes and templates
///     an edit reaches. The planner writes this set and nothing else, so these
///     cases are the reach of every chord-panel edit.
/// </summary>
public sealed class ChordApplyScopeTests
{
    private readonly Arrangement _arr = new() { Name = "Lead", ArrangementType = ArrangementType.Lead };
    private readonly ChordTemplate _a5;

    public ChordApplyScopeTests()
    {
        _arr.Levels.Add(new ChartLevel());
        _arr.Levels.Add(new ChartLevel());
        _a5 = Template(0, "A5", 5);
    }

    [Fact]
    public void ThisChord_RebindsOnlyTheShownChord_AndItsHandShape()
    {
        var chords = Chords(_a5, level: 0, count: 3);
        var bar = HandShape(_a5, level: 0, start: 0);
        var scope = Scope(This(chords[0], bar), Selected([chords[0], chords[1]], [bar]));

        var targets = scope.Resolve(ChordApplyRung.ThisChord, ChordTemplateEditKind.Fingering);

        Assert.Equal(ChordApplyPath.Rebind, targets.Path);
        Assert.Equal([chords[0]], targets.Sounds.Chords);
        Assert.Equal([bar], targets.Sounds.HandShapes);
        Assert.Empty(targets.BoundChords);
        Assert.Empty(targets.GripTemplates);
    }

    /// <summary>The regression the issue asks for: "this chord" never touches
    /// another template, however many share the grip.</summary>
    [Theory]
    [InlineData(ChordTemplateEditKind.Shape)]
    [InlineData(ChordTemplateEditKind.Fingering)]
    [InlineData(ChordTemplateEditKind.Name)]
    [InlineData(ChordTemplateEditKind.Arpeggio)]
    public void ThisChord_NeverReachesAnotherTemplate(ChordTemplateEditKind kind)
    {
        var chords = Chords(_a5, level: 0, count: 2);
        Template(1, "A5 (alt)", 5);
        Template(2, "B5", 7);
        var scope = Scope(This(chords[0]), Selected([chords[0]], []));

        var targets = scope.Resolve(ChordApplyRung.ThisChord, kind);

        Assert.Empty(targets.GripTemplates);
        Assert.Equal(0, targets.GripChords);
        Assert.Equal(ChordApplyPath.Rebind, targets.Path);
        Assert.Equal([chords[0]], targets.Sounds.Chords);
    }

    [Fact]
    public void Selected_IsNarrowedToTheShownShape()
    {
        var chords = Chords(_a5, level: 0, count: 3);
        var b5 = Template(2, "B5", 7);
        var other = Chords(b5, level: 0, count: 1)[0];
        var scope = Scope(This(chords[0]), Selected([chords[0], chords[1], other], []));

        var targets = scope.Resolve(ChordApplyRung.Selected, ChordTemplateEditKind.Fingering);

        Assert.Equal(ChordApplyPath.Rebind, targets.Path);
        Assert.Equal([chords[0], chords[1]], targets.Sounds.Chords);
        Assert.Equal(2, scope.Reach(ChordApplyRung.Selected).Sounds);
    }

    /// <summary>A selection that IS every use is edited in place: the same
    /// edit, without minting a template and orphaning this one.</summary>
    [Fact]
    public void Selected_CoveringEveryUse_IsTheInPlaceEdit()
    {
        var chords = Chords(_a5, level: 0, count: 2);
        var scope = Scope(This(chords[0]), Selected(chords, []));

        var targets = scope.Resolve(ChordApplyRung.Selected, ChordTemplateEditKind.Name);

        Assert.Equal(ChordApplyPath.InPlace, targets.Path);
        Assert.Equal(chords, targets.BoundChords);
    }

    [Fact]
    public void EveryUse_IsInPlace_OverEveryLevel_AndCountsHandShapes()
    {
        var onLevel0 = Chords(_a5, level: 0, count: 2);
        var onLevel1 = Chords(_a5, level: 1, count: 3);
        HandShape(_a5, level: 0, start: 100);
        HandShape(_a5, level: 1, start: 100);
        var scope = Scope(This(onLevel0[0]), Selected([onLevel0[0]], []));

        var targets = scope.Resolve(ChordApplyRung.EveryUse, ChordTemplateEditKind.Shape);
        var reach = scope.Reach(ChordApplyRung.EveryUse);

        Assert.Equal(ChordApplyPath.InPlace, targets.Path);
        Assert.Equal([.. onLevel0, .. onLevel1], targets.BoundChords);
        Assert.Equal(2, targets.BoundHandShapes);
        Assert.Equal((5, 2, 2), (reach.Chords, reach.HandShapes, reach.Levels));
        Assert.Empty(targets.GripTemplates);
    }

    /// <summary>An unbound selected chord with the shape — a fresh import at
    /// TemplateId -1 — JOINS the shape on "every use", and so do the unbound
    /// strums under a selected hand shape (the old hand-shape rebind).</summary>
    [Fact]
    public void EveryUse_BindsUnboundSoundsOfTheShape()
    {
        var bound = Chords(_a5, level: 0, count: 1)[0];
        var loose = Chord(-1, 5, position: 40);
        var underBar = Chord(-1, 5, position: 81);
        _arr.Levels[0].Chords.Add(loose);
        _arr.Levels[0].Chords.Add(underBar);
        var bar = HandShape(_a5, level: 0, start: 80);
        var scope = Scope(This(loose), Selected([loose], [bar]));

        var targets = scope.Resolve(ChordApplyRung.EveryUse, ChordTemplateEditKind.Fingering);

        Assert.Equal(ChordApplyPath.InPlace, targets.Path);
        Assert.Equal([bound], targets.BoundChords);
        Assert.Equal([loose, underBar], targets.Sounds.Chords);
    }

    [Fact]
    public void SameGrip_Fingering_ReachesSameFretAndTransposedTemplates()
    {
        var chords = Chords(_a5, level: 0, count: 1);
        var sameFrets = Template(1, "A5 (alt)", 5);
        var moved = Template(2, "B5", 7);
        var unrelated = Template(3, "C", 8, third: 11);
        Chords(sameFrets, level: 0, count: 2);
        Chords(moved, level: 1, count: 3);
        Chords(unrelated, level: 0, count: 1);
        var scope = Scope(This(chords[0]), Selected(chords, []));

        var targets = scope.Resolve(ChordApplyRung.SameGrip, ChordTemplateEditKind.Fingering, Fingered(_a5));

        Assert.Equal(ChordApplyPath.InPlace, targets.Path);
        Assert.Equal([sameFrets, moved], targets.GripTemplates);
        Assert.Equal(5, targets.GripChords);
        Assert.Equal(3, targets.ShapeCount);
        Assert.Equal(2, scope.Reach(ChordApplyRung.SameGrip).GripTemplates);
    }

    /// <summary>A name belongs to the pitches: only a same-fret template of the
    /// same flavour shares it. A shape, arpeggio, Nop or Fingerless change never leaves its shape.</summary>
    [Theory]
    [InlineData(ChordTemplateEditKind.Name, 1)]
    [InlineData(ChordTemplateEditKind.Shape, 0)]
    [InlineData(ChordTemplateEditKind.Arpeggio, 0)]
    [InlineData(ChordTemplateEditKind.Nop, 0)]
    [InlineData(ChordTemplateEditKind.Fingerless, 0)]
    public void SameGrip_ReachesOnlyWhatTheEditKindCanCarry(ChordTemplateEditKind kind, int grips)
    {
        var chords = Chords(_a5, level: 0, count: 1);
        Template(1, "A5 (alt)", 5);
        Template(2, "B5", 7);
        var scope = Scope(This(chords[0]), Selected(chords, []));

        var targets = scope.Resolve(ChordApplyRung.SameGrip, kind);

        Assert.Equal(grips, targets.GripTemplates.Count);
        if (grips == 1) Assert.Equal(1, targets.GripTemplates[0].Id);
    }

    [Fact]
    public void SameGrip_WithNoOtherGrip_IsUnavailable_AndFallsToEveryUse()
    {
        var chords = Chords(_a5, level: 0, count: 2);
        var scope = Scope(This(chords[0]), Selected([chords[0]], []));

        Assert.False(scope.IsAvailable(ChordApplyRung.SameGrip));
        Assert.Equal(ChordApplyRung.EveryUse, scope.Effective(ChordApplyRung.SameGrip));
    }

    /// <summary>Browse mode: nothing selected, so the selection rungs fall
    /// through to "every use" — and a reshape there is refused (#483).</summary>
    [Fact]
    public void NothingSelected_FallsToEveryUse_AndRefusesAReshape()
    {
        Chords(_a5, level: 0, count: 2);
        var scope = Scope(ChordApplySounds.None, ChordApplySounds.None);

        Assert.False(scope.HasSelection);
        Assert.Equal(ChordApplyRung.EveryUse, scope.Effective(ChordApplyRung.ThisChord));
        Assert.Equal(ChordApplyPath.InPlace, scope.Resolve(ChordApplyRung.Selected, ChordTemplateEditKind.Fingering).Path);
        Assert.Equal(ChordApplyPath.Refused, scope.Resolve(ChordApplyRung.Selected, ChordTemplateEditKind.Shape).Path);
    }

    /// <summary>A template switch is a rebind on every rung — "every use" moves
    /// every use onto the picked template, never rewrites this one in place.</summary>
    [Fact]
    public void Switch_OnEveryUse_RebindsEveryUse()
    {
        var chords = Chords(_a5, level: 0, count: 2);
        var deeper = Chords(_a5, level: 1, count: 1);
        var bar = HandShape(_a5, level: 1, start: 100);
        var scope = Scope(This(chords[0]), Selected([chords[0]], []));

        var targets = scope.Resolve(ChordApplyRung.SameGrip, ChordTemplateEditKind.Switch);

        Assert.Equal(ChordApplyPath.Rebind, targets.Path);
        Assert.Equal([.. chords, .. deeper], targets.Sounds.Chords);
        Assert.Equal([bar], targets.Sounds.HandShapes);
        Assert.Empty(targets.GripTemplates);
    }

    [Fact]
    public void GripFingeringDiffers_OnlyWhenAGripIsFingeredDifferently()
    {
        var chords = Chords(_a5, level: 0, count: 1);
        var moved = Template(2, "B5", 7);
        _a5.Fingers[0] = 1;
        _a5.Fingers[1] = 3;
        _a5.Fingers[2] = 3;
        var scope = Scope(This(chords[0]), Selected(chords, []));
        Assert.True(scope.GripFingeringDiffers);

        foreach (var (s, f) in _a5.Fingers) moved.Fingers[s] = f;
        Assert.False(Scope(This(chords[0]), Selected(chords, [])).GripFingeringDiffers);
    }

    /// <summary>Review of #521, finding 1: the copy OVERWRITES, so a shape
    /// fingered on one string of three must never reach a grip that is fingered
    /// on all three — it would erase two fingers. Only a complete fingering
    /// travels, and the targets say the grips were held back.</summary>
    [Fact]
    public void SameGrip_APartialFingering_NeverReachesTheOtherGrips()
    {
        var chords = Chords(_a5, level: 0, count: 1);
        var moved = Template(2, "B5", 7);
        moved.Fingers[0] = 1;
        moved.Fingers[1] = 3;
        moved.Fingers[2] = 4;
        _a5.Fingers[0] = 1;
        var scope = Scope(This(chords[0]), Selected(chords, []));

        var partial = _a5.Clone();
        partial.Fingers[0] = 2;
        var withheld = scope.Resolve(ChordApplyRung.SameGrip, ChordTemplateEditKind.Fingering, partial);
        var complete = _a5.Clone();
        complete.Fingers[1] = 3;
        complete.Fingers[2] = 4;
        var reached = scope.Resolve(ChordApplyRung.SameGrip, ChordTemplateEditKind.Fingering, complete);

        Assert.Empty(withheld.GripTemplates);
        Assert.True(withheld.GripsWithheld);
        Assert.False(scope.GripFingeringDiffers);
        Assert.Equal([moved], reached.GripTemplates);
        Assert.False(reached.GripsWithheld);
    }

    /// <summary>Review of #521, finding 2: every rung's count is READ OFF the
    /// set the edit writes. A selection that covers every use pulls the unbound
    /// strum under its hand shape into an in-place edit, on "this chord" and
    /// "selected" as on "every use", and the ladder must count it.</summary>
    [Theory]
    [InlineData(ChordApplyRung.ThisChord)]
    [InlineData(ChordApplyRung.Selected)]
    [InlineData(ChordApplyRung.EveryUse)]
    public void Reach_IsTheSetResolveWrites_IncludingTheUnboundChordsUnderAHandShape(ChordApplyRung rung)
    {
        var bound = Chords(_a5, level: 0, count: 1)[0];
        var bar = HandShape(_a5, level: 0, start: 80);
        var underBar = Chord(-1, 5, position: 81);
        _arr.Levels[0].Chords.Add(underBar);
        var scope = Scope(This(bound, bar), Selected([bound], [bar]));

        var targets = scope.Resolve(rung, ChordTemplateEditKind.Fingering);
        var reach = scope.Reach(rung);

        Assert.Equal(ChordApplyPath.InPlace, targets.Path);
        Assert.Contains(underBar, targets.Sounds.Chords);
        Assert.Equal((targets.ChordCount, targets.HandShapeCount), (reach.Chords, reach.HandShapes));
        Assert.Equal(2, reach.Chords);
    }

    /// <summary>Review of #521, finding 3: a same-fret template of the OTHER
    /// arpeggio flavour (either flag) is a different kind of shape; a fingering
    /// copy leaves it alone, as the name copy and the transposed grips did.</summary>
    [Fact]
    public void SameGrip_Fingering_LeavesASameFretTemplateOfTheOtherFlavourAlone()
    {
        var chords = Chords(_a5, level: 0, count: 1);
        var plainTwin = Template(1, "A5 (alt)", 5);
        var forcedArpeggio = Template(2, "A5-arp", 5);
        forcedArpeggio.ForceArpeggioInRs = true;
        var arpeggio = Template(3, "A5 arp", 5);
        arpeggio.Arpeggio = true;
        Chords(forcedArpeggio, level: 0, count: 2);
        var scope = Scope(This(chords[0]), Selected(chords, []));

        var fingering = scope.Resolve(ChordApplyRung.SameGrip, ChordTemplateEditKind.Fingering, Fingered(_a5));
        var name = scope.Resolve(ChordApplyRung.SameGrip, ChordTemplateEditKind.Name);

        Assert.Equal([plainTwin], fingering.GripTemplates);
        Assert.Equal([plainTwin], name.GripTemplates);
        Assert.Equal(0, fingering.GripChords);
        Assert.Equal(1, scope.Reach(ChordApplyRung.SameGrip).GripTemplates);
    }

    /// <summary>A copy of <paramref name="shape" /> with a finger on every
    /// string: the only fingering the "same grip" rung copies.</summary>
    private static ChordTemplate Fingered(ChordTemplate shape)
    {
        var fingered = shape.Clone();
        foreach (var stringIndex in shape.Frets.Keys) fingered.Fingers[stringIndex] = stringIndex + 1;
        return fingered;
    }

    private ChordApplyScope Scope(ChordApplySounds thisSounds, ChordApplySounds selected) =>
        ChordApplyScope.Prepare(_arr, _a5, thisSounds, selected, _arr.Levels[0].Chords);

    private static ChordApplySounds This(Chord chord, HandShape? bar = null) =>
        new([chord], bar == null ? [] : [bar]);

    private static ChordApplySounds Selected(List<Chord> chords, List<HandShape> handShapes) => new(chords, handShapes);

    /// <summary>A three-string power-chord grip rooted at <paramref name="root" />.</summary>
    private ChordTemplate Template(int id, string name, int root, int? third = null)
    {
        var template = new ChordTemplate { Id = id, Name = name };
        template.Frets[0] = root;
        template.Frets[1] = root + 2;
        template.Frets[2] = third ?? root + 2;
        _arr.ChordTemplates.Add(template);
        return template;
    }

    private List<Chord> Chords(ChordTemplate template, int level, int count)
    {
        var result = new List<Chord>();
        for (var i = 0; i < count; i++)
        {
            var chord = new Chord { TemplateId = template.Id, Position = new FractionalPosition(_arr.Levels[level].Chords.Count * 4) };
            foreach (var (stringIndex, fret) in template.Frets)
                chord.ChordNotes.Add(new ChordNote { StringIndex = stringIndex, Fret = fret });
            _arr.Levels[level].Chords.Add(chord);
            result.Add(chord);
        }

        return result;
    }

    private static Chord Chord(int templateId, int root, int position)
    {
        var chord = new Chord { TemplateId = templateId, Position = new FractionalPosition(position) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = root });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = root + 2 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 2, Fret = root + 2 });
        return chord;
    }

    private HandShape HandShape(ChordTemplate template, int level, int start)
    {
        var bar = new HandShape { TemplateId = template.Id, Position = new FractionalPosition(start), EndPosition = new FractionalPosition(start + 4) };
        _arr.Levels[level].HandShapes.Add(bar);
        return bar;
    }
}
