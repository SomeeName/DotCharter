using DotCharter.Tone.Audio.ChartSynth;
using DotCharter.UI.ViewModels.Editor.Inspector;
using JuceSharp;
using static DotCharter.Tests.Services.Audio.ChartSynth.ChartNoteEventBuilderTests;

namespace DotCharter.Tests.Services.Audio.ChartSynth;

/// <summary>
///     The song's playing style (#160): each rule the chart synth plays by can be
///     switched off or given another note value, separately for single notes and
///     chords, and separately for guitar and bass. 120 bpm throughout, so a quarter
///     note is 500 ms and the legato damp lands 8 ms before the next attack.
/// </summary>
public sealed class ChartSynthPlayStyleTests
{
    private const JuceChartNoteFlags EndFlags =
        JuceChartNoteFlags.EndDamped | JuceChartNoteFlags.LetRing | JuceChartNoteFlags.EndShape;

    private static GuitarNote At(int beat, int num, int den, int stringIndex, int fret)
        => new() { Position = new FractionalPosition(beat, new Fraction(num, den)), StringIndex = stringIndex, Fret = fret };

    private static Chord ChordAt(int beat, params (int String, int Fret)[] notes)
    {
        var c = new Chord { Position = new FractionalPosition(beat) };
        foreach (var (str, fret) in notes) c.ChordNotes.Add(new ChordNote { StringIndex = str, Fret = fret });
        return c;
    }

    private static HandShape ShapeTo(int fromBeat, int toBeat, int num, int den)
        => new() { Position = new FractionalPosition(fromBeat), EndPosition = new FractionalPosition(toBeat, new Fraction(num, den)) };

    private static ChartNoteUpload Build(IList<GuitarNote> notes, ChartSynthPlayStyle? style, IList<Chord>? chords = null,
        IList<HandShape>? shapes = null, Arrangement? arr = null)
        => ChartNoteEventBuilder.Build(arr ?? new Arrangement(), notes, chords ?? [], Beats(), shapes, 120, style);

    private static ChartSynthPlayStyle Guitar(Action<ChartSynthPlayRules> edit)
    {
        var style = new ChartSynthPlayStyle();
        edit(style.Guitar);
        return style;
    }

    // ----- single notes -----------------------------------------------------------

    /// <summary>A note 375 ms before the next: under the default quarter it holds up
    /// to it; switched off, or with "short" set to an eighth (250 ms), it ends at its
    /// own end (an eighth after its attack) with a natural release.</summary>
    [Fact]
    public void ASingleNoteGap_IsHeldByDefault_AndNotWhenSwitchedOffOrShorterThanTheGap()
    {
        static GuitarNote[] Run() => [At(2, 0, 4, 2, 7), At(2, 3, 4, 3, 5)];   // 1.0 s, 1.375 s

        var held = Build(Run(), null).Notes[0];
        Assert.Equal(1.375 - 0.008, held.ReleaseSeconds, 6);
        Assert.True(held.Flags.HasFlag(JuceChartNoteFlags.EndDamped));
        Assert.Equal(held.ReleaseSeconds, Build(Run(), new ChartSynthPlayStyle()).Notes[0].ReleaseSeconds, 9);

        foreach (var style in new[] { Guitar(r => r.HoldSingleNoteGaps = false), Guitar(r => r.SingleNoteGapDivisor = 8) })
        {
            var cut = Build(Run(), style).Notes[0];
            Assert.Equal(1.25, cut.ReleaseSeconds, 6);
            Assert.Equal(JuceChartNoteFlags.None, cut.Flags & EndFlags);
        }
    }

    /// <summary>A half note as "short" holds a 750 ms gap the default lets go.</summary>
    [Fact]
    public void ALongerNoteValue_HoldsAWiderGap()
    {
        static GuitarNote[] Run() => [At(2, 0, 4, 2, 7), At(3, 1, 2, 3, 5)];   // 1.0 s, 1.75 s

        Assert.Equal(1.25, Build(Run(), null).Notes[0].ReleaseSeconds, 6);
        var held = Build(Run(), Guitar(r => r.SingleNoteGapDivisor = 2)).Notes[0];
        Assert.Equal(1.75 - 0.008, held.ReleaseSeconds, 6);
        Assert.True(held.Flags.HasFlag(JuceChartNoteFlags.EndDamped));
    }

    // ----- chords -----------------------------------------------------------------

    /// <summary>A chord's notes follow the CHORD rule and a lone note the
    /// single-note rule: switching one off leaves the other holding.</summary>
    [Fact]
    public void ChordsAndSingleNotes_HaveTheirOwnHoldRule()
    {
        // The chord at 1.0 s and a note 375 ms later: past the chord's own eighth
        // (1.25 s), so only the hold can carry the chord up to it.
        static ChartNoteUpload Wide(ChartSynthPlayStyle style)
            => Build([At(2, 3, 4, 4, 5)], style, [ChordAt(2, (0, 3), (1, 2))]);   // the note 375 ms after the chord
        Assert.All(Wide(Guitar(r => r.HoldSingleNoteGaps = false)).Notes.Where(n => n.ChordId >= 0),
            n => Assert.Equal(1.375 - 0.008, n.ReleaseSeconds, 6));
        Assert.All(Wide(Guitar(r => r.HoldChordGaps = false)).Notes.Where(n => n.ChordId >= 0), n =>
        {
            Assert.Equal(1.25, n.ReleaseSeconds, 6);
            Assert.Equal(JuceChartNoteFlags.None, n.Flags & EndFlags);
        });
        Assert.All(Wide(Guitar(r => r.ChordGapDivisor = 16)).Notes.Where(n => n.ChordId >= 0),
            n => Assert.Equal(1.25, n.ReleaseSeconds, 6));
    }

    /// <summary>The #90 hold past a shape's end follows the chord rule too: a shape
    /// ending 250 ms before the next chord holds by default, and keeps its shape end
    /// when the hold is off or "short" is an eighth (250 ms is not under 250 ms).</summary>
    [Fact]
    public void TheHoldPastAShapeEnd_FollowsTheChordRule()
    {
        static ChartNoteUpload Run(ChartSynthPlayStyle? style) => Build([], style,
            [ChordAt(2, (1, 10), (2, 7)), ChordAt(6, (1, 10), (2, 7))],
            [ShapeTo(2, 5, 1, 2), new HandShape { Position = new FractionalPosition(6), EndPosition = new FractionalPosition(9) }]);

        Assert.All(Run(null).Notes.Where(n => n.StartSeconds < 2.0), n => Assert.Equal(3.0 - 0.008, n.ReleaseSeconds, 6));
        foreach (var style in new[] { Guitar(r => r.HoldChordGaps = false), Guitar(r => r.ChordGapDivisor = 8) })
        {
            Assert.All(Run(style).Notes.Where(n => n.StartSeconds < 2.0), n =>
            {
                Assert.Equal(2.75, n.ReleaseSeconds, 6);
                Assert.True(n.Flags.HasFlag(JuceChartNoteFlags.EndShape), n.Flags.ToString());
            });
        }
    }

    // ----- hand shapes --------------------------------------------------------------

    /// <summary>With hand shapes not ringing, a chord inside one ends like any other
    /// chord: at an eighth, no let-ring, no shape end.</summary>
    [Fact]
    public void HandShapesOff_AChordInAShape_DoesNotRingToItsEnd()
    {
        var shape = new HandShape { Position = new FractionalPosition(2), EndPosition = new FractionalPosition(8) };
        var ringing = Build([], null, [ChordAt(2, (0, 3), (1, 2))], [shape]);
        Assert.All(ringing.Notes, n => Assert.Equal(4.0, n.ReleaseSeconds, 6));

        var dry = Build([], Guitar(r => r.HandShapesRing = false), [ChordAt(2, (0, 3), (1, 2))], [shape]);
        Assert.All(dry.Notes, n =>
        {
            Assert.Equal(1.25, n.ReleaseSeconds, 6);
            Assert.Equal(JuceChartNoteFlags.None, n.Flags & EndFlags);
        });
    }

    /// <summary>A hammer-on is how the string SOUNDS, not a taste: with every rule
    /// off the note before it still rings into it.</summary>
    [Fact]
    public void EveryRuleOff_AHammerOnStillContinuesTheString()
    {
        var hammer = At(2, 1, 4, 2, 9);
        hammer.Hopo = HopoType.HammerOn;
        var off = Guitar(r =>
        {
            r.HandShapesRing = false;
            r.HoldSingleNoteGaps = false;
            r.HoldChordGaps = false;
        });
        var up = Build([At(2, 0, 4, 2, 7), hammer], off);
        Assert.Equal(1.125 + 0.030, up.Notes[0].ReleaseSeconds, 6);
    }

    // ----- guitar vs bass -------------------------------------------------------------

    /// <summary>A bass arrangement plays by the BASS rules and ignores the guitar's.</summary>
    [Fact]
    public void ABassArrangement_PlaysByTheBassRules()
    {
        var bass = new Arrangement { ArrangementType = ArrangementType.Bass, StringCount = 4 };
        static GuitarNote[] Run() => [At(2, 0, 4, 0, 3), At(2, 3, 4, 1, 5)];

        Assert.Equal(1.375 - 0.008, Build(Run(), Guitar(r => r.HoldSingleNoteGaps = false), arr: bass).Notes[0].ReleaseSeconds, 6);

        var style = new ChartSynthPlayStyle();
        style.Bass.HoldSingleNoteGaps = false;
        Assert.Equal(1.25, Build(Run(), style, arr: bass).Notes[0].ReleaseSeconds, 6);
        Assert.Equal(1.375 - 0.008, Build(Run(), style).Notes[0].ReleaseSeconds, 6);   // a guitar part: untouched
    }

    /// <summary>A 5/6-string bass charted on a guitar path at A220 (#161) is a bass
    /// PART: it plays by the bass rules too, and its fingerprint follows them.</summary>
    [Fact]
    public void AGuitarPathBassAtA220_PlaysByTheBassRules()
    {
        var bass = DotCharter.Tests.Core.GuitarPathBassShapes.SixStringBassOnAltRhythm();
        static GuitarNote[] Run() => [At(2, 0, 4, 0, 3), At(2, 3, 4, 1, 5)];

        var style = new ChartSynthPlayStyle();
        style.Bass.HoldSingleNoteGaps = false;
        Assert.Equal(1.25, Build(Run(), style, arr: bass).Notes[0].ReleaseSeconds, 6);
        Assert.Equal(1.375 - 0.008, Build(Run(), Guitar(r => r.HoldSingleNoteGaps = false), arr: bass).Notes[0].ReleaseSeconds, 6);

        var notes = Run();
        var beats = Beats();
        Assert.NotEqual(ChartNoteEventBuilder.Fingerprint(bass, notes, [], beats, null, 120),
            ChartNoteEventBuilder.Fingerprint(bass, notes, [], beats, null, 120, style));
    }

    /// <summary>The fingerprint moves when the arrangement's OWN rules change (so the
    /// synth re-uploads) and not when the other family's do.</summary>
    [Fact]
    public void TheFingerprint_FollowsTheArrangementsOwnRules()
    {
        var arr = new Arrangement();
        var notes = new List<GuitarNote> { At(2, 0, 4, 2, 7) };
        var beats = Beats();
        var style = new ChartSynthPlayStyle();
        long Fp() => ChartNoteEventBuilder.Fingerprint(arr, notes, [], beats, null, 120, style);

        var baseline = Fp();
        Assert.Equal(baseline, ChartNoteEventBuilder.Fingerprint(arr, notes, [], beats, null, 120));
        style.Bass.ChordGapDivisor = 16;
        Assert.Equal(baseline, Fp());

        var edits = new (string What, Action<ChartSynthPlayRules> Do)[]
        {
            ("shapes", r => r.HandShapesRing = false),
            ("hold notes", r => r.HoldSingleNoteGaps = false),
            ("note gap", r => r.SingleNoteGapDivisor = 8),
            ("hold chords", r => r.HoldChordGaps = false),
            ("chord gap", r => r.ChordGapDivisor = 32),
        };
        foreach (var (what, edit) in edits)
        {
            style.Guitar.CopyFrom(new ChartSynthPlayRules());
            edit(style.Guitar);
            Assert.True(Fp() != baseline, $"changing '{what}' did not move the fingerprint");
        }
    }

    // ----- the undoable edit and the Mixer rows -------------------------------------------

    [Fact]
    public void TheAction_SetsTheRulesInPlace_AndUndoes()
    {
        var style = new ChartSynthPlayStyle();
        var target = style.Guitar;
        var next = new ChartSynthPlayRules { HoldChordGaps = false, ChordGapDivisor = 16 };
        var action = new SetSynthPlayStyleAction(target, next);
        Assert.True(action.HasEffect);
        next.ChordGapDivisor = 2;   // the action copied it; a later write to the source changes nothing

        action.Execute();
        Assert.Same(target, style.Guitar);
        Assert.False(style.Guitar.HoldChordGaps);
        Assert.Equal(16, style.Guitar.ChordGapDivisor);
        action.Undo();
        Assert.True(style.Guitar.IsDefault);
        Assert.False(new SetSynthPlayStyleAction(target, new ChartSynthPlayRules()).HasEffect);
    }

    private static (ChartSynthPlayStyleViewModel Vm, ProjectState State, HistoryService History, ChartProject Project) Rows()
    {
        var state = new ProjectState();
        var project = new ChartProject();
        state.ActiveProject = project;
        var history = new HistoryService(new FakeAppLogger());
        return (new ChartSynthPlayStyleViewModel(state, history, FakeLocalization.KeysOnly()), state, history, project);
    }

    [Fact]
    public void TheRows_EditTheShownFamily_AsUndoableSteps()
    {
        var (vm, _, history, project) = Rows();
        Assert.True(vm.HasProject);
        Assert.True(vm.EditingGuitar);
        Assert.Equal(4, vm.SingleNoteGap!.Value);
        Assert.False(vm.CanReset);

        vm.ChordGap = vm.GapChoices.Single(c => c.Value == 16);
        vm.HoldSingleNoteGaps = false;
        Assert.Equal(16, project.SynthPlayStyle.Guitar.ChordGapDivisor);
        Assert.False(project.SynthPlayStyle.Guitar.HoldSingleNoteGaps);
        Assert.True(project.SynthPlayStyle.Bass.IsDefault);
        Assert.True(vm.CanReset);

        history.Undo();
        Assert.True(project.SynthPlayStyle.Guitar.HoldSingleNoteGaps);
        Assert.True(vm.HoldSingleNoteGaps);

        vm.EditingBass = true;
        Assert.False(vm.EditingGuitar);
        Assert.Equal(4, vm.ChordGap!.Value);   // the bass rules, still the defaults
        vm.HandShapesRing = false;
        Assert.False(project.SynthPlayStyle.Bass.HandShapesRing);

        vm.ResetToDefaultsCommand.Execute(null);
        Assert.True(project.SynthPlayStyle.Bass.IsDefault);
        Assert.Equal(16, project.SynthPlayStyle.Guitar.ChordGapDivisor);   // reset touches the shown family only
        vm.Dispose();
    }

    [Fact]
    public void TheRows_FollowTheArrangementBeingEdited_AndAreOffWithoutAProject()
    {
        var (vm, state, _, _) = Rows();
        state.ActiveArrangement = new Arrangement { ArrangementType = ArrangementType.Bass, StringCount = 4 };
        Assert.True(vm.EditingBass);
        state.ActiveArrangement = new Arrangement();
        Assert.True(vm.EditingGuitar);
        state.ActiveArrangement = DotCharter.Tests.Core.GuitarPathBassShapes.FiveStringBassOnBonusRhythm();
        Assert.True(vm.EditingBass);   // a guitar-path bass at A220 edits the rules it plays by (#161)

        state.ActiveProject = null;
        Assert.False(vm.HasProject);
        vm.HandShapesRing = false;   // nothing to edit: ignored, not thrown
        Assert.True(vm.HandShapesRing);
        vm.Dispose();
    }
}
