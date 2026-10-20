using DotCharter.Services.Editing.Highway;
using DotCharter.UI.ViewModels.Editor.Inspector;
using DotCharter.UI.ViewModels.Panels;

namespace DotCharter.Tests.UI.ViewModels.Panels;

/// <summary>
///     #604: the keyboard's technique verbs take the "Apply to" ladder too. Through the REAL
///     <see cref="HighwayEditingService" /> verbs a key runs (<c>AppKeybindRegistrar</c> and the command
///     palette call exactly these), wired to the real chord panel through the inspector's own adapter
///     (<see cref="ChordTechniqueKeyboardReach" />), on every rung:
///     <list type="bullet">
///         <item>the mute key writes exactly the rung's chords — the set the inspector's click writes
///         (<c>ChordTechniqueApplyToTests</c> pins that the click writes <c>TechniqueTargets</c>) — and
///         exactly what the #485 preview marked;</item>
///         <item>a per-string key behind a one-string mask lands on that STRING of every target;</item>
///         <item>no template changes; ONE undo node puts every level back (deep compare);</item>
///         <item>on "selected" the key does EXACTLY what it did before #604 (a deep compare against the
///         same key with no ladder wired at all);</item>
///         <item>it never widens without a ladder on screen, for another selection or for an implied
///         target.</item>
///     </list>
/// </summary>
public sealed class ChordTechniqueKeyboardApplyToTests
{
    private const int MaskedString = 1;

    public static TheoryData<ChordApplyRung, int> Rungs() => ChordLadderFixture.Rungs();

    [Theory]
    [MemberData(nameof(Rungs))]
    public void TheMuteKey_WritesExactlyTheRung_TheInspectorsTargets_AndWhatThePreviewMarked(ChordApplyRung rung,
        int expected)
    {
        var f = new ChordLadderFixture();
        f.Choose(rung);
        var preview = f.Panel.Preview.Resolved;
        var inspectorTargets = f.Panel.TechniqueTargets()!.Chords;
        var templates = f.TemplateSnapshot();
        var before = f.Fingerprints();

        f.Keys.ToggleSelectedMute();

        var changed = f.ChangedSince(before);
        Assert.Equal(expected, changed.Count);
        Assert.True(changed.SetEquals(inspectorTargets), "the key wrote other chords than the inspector's click would");
        if (rung <= ChordApplyRung.Selected) Assert.True(preview.IsEmpty);
        else Assert.True(changed.SetEquals(preview.Chords), "the key wrote other chords than the preview marked");
        Assert.All(changed, c => Assert.All(c.ChordNotes, n => Assert.Equal(MuteType.Full, n.Mute)));
        Assert.DoesNotContain(f.Unrelated, changed);
        f.AssertNoTemplateChanged(templates);
        Assert.Single(f.History.GetUndoSteps());
    }

    [Theory]
    [MemberData(nameof(Rungs))]
    public void TheVibratoKey_BehindAOneStringMask_LandsOnThatStringOfEveryTarget(ChordApplyRung rung, int expected)
    {
        var f = new ChordLadderFixture();
        f.State.ToggleStringEditMask(MaskedString);
        f.Choose(rung);
        var before = f.Fingerprints();

        f.Keys.ToggleSelectedVibrato();

        var changed = f.ChangedSince(before);
        Assert.Equal(expected, changed.Count);
        Assert.True(changed.SetEquals(f.Panel.TechniqueTargets()!.Chords));
        foreach (var chord in changed)
        {
            foreach (var note in chord.ChordNotes)
                Assert.Equal(note.StringIndex == MaskedString, note.Vibrato);
        }
    }

    /// <summary>A whole-chord key (accent) and a GROUP key (every target set to the first's next
    /// value) follow the rung as well.</summary>
    [Fact]
    public void TheAccentKey_AndTheMuteGroupKey_OnEveryUse_ReachEveryUse()
    {
        var f = new ChordLadderFixture();
        f.Choose(ChordApplyRung.EveryUse);
        var targets = f.Panel.TechniqueTargets()!.Chords;

        f.Keys.ToggleSelectedAccent();
        f.Keys.ToggleSelectedMuteGroup();

        Assert.Equal(5, targets.Count);
        Assert.All(targets, c => Assert.True(c.Accent));
        Assert.All(targets, c => Assert.All(c.ChordNotes, n => Assert.Equal(MuteType.Full, n.Mute)));
        Assert.All(f.AllChords.Except(targets), c => Assert.False(c.Accent));
        Assert.Equal(2, f.History.GetUndoSteps().Count);
    }

    /// <summary>ONE history node over both levels; one undo restores the whole arrangement and redo
    /// re-applies it; the status rail is told what was reached.</summary>
    [Theory]
    [InlineData(ChordApplyRung.EveryUse)]
    [InlineData(ChordApplyRung.SameGrip)]
    public void OneUndo_RestoresEveryLevel_AndRedoReappliesIt(ChordApplyRung rung)
    {
        var f = new ChordLadderFixture();
        var before = ChartProjectCloner.CloneArrangement(f.Arrangement);
        f.Choose(rung);
        var targets = f.Panel.TechniqueTargets()!.Chords;

        f.Keys.ToggleSelectedMute();
        var after = ChartProjectCloner.CloneArrangement(f.Arrangement);

        Assert.Single(f.History.GetUndoSteps());
        Assert.All(f.Arrangement.Levels, l => Assert.Contains(l.Chords, c => c.ChordNotes[0].Mute == MuteType.Full));
        Assert.NotEmpty(DeepCompare.Diff("arr", before, after, new HashSet<string>()));
        var reported = Assert.Single(f.Reported);
        Assert.True(reported.ToHashSet<Chord>(ReferenceEqualityComparer.Instance).SetEquals(targets));

        f.History.Undo();
        Assert.Empty(DeepCompare.Diff("arr", before, ChartProjectCloner.CloneArrangement(f.Arrangement), new HashSet<string>()));

        f.History.Redo();
        Assert.Empty(DeepCompare.Diff("arr", after, ChartProjectCloner.CloneArrangement(f.Arrangement), new HashSet<string>()));
    }

    /// <summary>The regression half: on "selected" the key is the pre-#604 verb, byte for byte — the
    /// same arrangement as the same key with no ladder wired at all, one undo step, and nothing
    /// reported.</summary>
    [Fact]
    public void OnSelected_TheKeyIsExactlyThePre604Verb()
    {
        var ladder = new ChordLadderFixture();
        var plain = new ChordLadderFixture(keyboardReach: false);
        ladder.Choose(ChordApplyRung.Selected);
        plain.Choose(ChordApplyRung.Selected);

        ladder.Keys.ToggleSelectedMute();
        plain.Keys.ToggleSelectedMute();

        Assert.Empty(DeepCompare.Diff("arr", ChartProjectCloner.CloneArrangement(plain.Arrangement),
            ChartProjectCloner.CloneArrangement(ladder.Arrangement), new HashSet<string>()));
        Assert.Single(ladder.History.GetUndoSteps());
        Assert.Empty(ladder.Reported);
        Assert.All(new[] { ladder.First, ladder.Second }, c => Assert.Equal(MuteType.Full, c.ChordNotes[0].Mute));
    }

    /// <summary>With no ladder on screen the rung is invisible session state, so the key keeps to the
    /// selection — even on "every use".</summary>
    [Fact]
    public void WithNoLadderOnScreen_TheKeyKeepsToTheSelection()
    {
        var f = new ChordLadderFixture { LadderOnScreen = false };
        f.Choose(ChordApplyRung.EveryUse);
        var before = f.Fingerprints();

        f.Keys.ToggleSelectedMute();

        Assert.True(f.ChangedSince(before).SetEquals(new[] { f.First, f.Second }));
        Assert.Empty(f.Reported);
    }

    /// <summary>An implied target — the sounds under a selected hand shape, which fast-charting input
    /// edits without anybody selecting them — never widens: the ladder is not even asked.</summary>
    [Fact]
    public void AnImpliedTarget_NeverAsksTheLadder()
    {
        var f = new ChordLadderFixture(keyboardReach: false);
        f.Config.AppConfig.UseFastChartingInput = true;
        var spy = new SpyReach(f.AllChords.ToList());
        f.Keys.TechniqueReach = spy;
        f.Selection.SelectedChords.Clear();
        f.Selection.SelectedHandShapes.Add(f.Bar);
        var before = f.Fingerprints();

        f.Keys.ToggleSelectedMute();

        Assert.Equal(0, spy.Asked);
        Assert.Empty(spy.Reports);
        var changed = f.ChangedSince(before);
        Assert.NotEmpty(changed);
        Assert.All(changed, c => Assert.Contains(c, f.Arrangement.Levels[0].Chords));
        Assert.True(changed.Count < f.AllChords.Count());
    }

    /// <summary>The service only follows what the reach answers for the user's own selection; the
    /// inspector's adapter refuses a selection it did not resolve the ladder for.</summary>
    [Fact]
    public void TheAdapter_RefusesASelectionOtherThanTheOneOnScreen()
    {
        var f = new ChordLadderFixture();
        f.Choose(ChordApplyRung.EveryUse);
        var reach = f.Keys.TechniqueReach!;

        Assert.NotNull(reach.ChordsFor([f.First, f.Second]));
        Assert.Null(reach.ChordsFor([f.First]));
        Assert.Null(reach.ChordsFor([f.First, f.Unrelated]));
    }

    private sealed class SpyReach(IReadOnlyList<Chord> answer) : IChordTechniqueReach
    {
        public int Asked { get; private set; }

        public IReadOnlyList<Chord>? ChordsFor(IReadOnlyList<Chord> selectedChords)
        {
            Asked++;
            return answer;
        }

        public List<IReadOnlyList<Chord>> Reports { get; } = [];

        public void Reached(IReadOnlyList<Chord> chords) => Reports.Add(chords);
    }
}
