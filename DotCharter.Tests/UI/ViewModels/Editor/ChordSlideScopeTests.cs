using System.Text.RegularExpressions;
using DotCharter.Services.Editing;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Editor.Inspector;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     #530: the Inspector's chord slide field treated every string alike. For
///     the owner's 5+8 where only the 5 slides (to 6) it read "6" under a scope
///     bar saying "all strings", and typing 7 wrote fret 7 to BOTH strings — the
///     8 slid DOWN to 7. The rule is now EOF's and rscharter's: one number, the
///     target of the lowest-fretted string in scope, with every other string in
///     scope moving by the same number of frets; a readout that shows nothing
///     when the strings disagree; and a target that would push a string off the
///     fretboard refused whole.
/// </summary>
public sealed class ChordSlideScopeTests
{
    // String 1 (index 1) plays 5 and slides to 6; string 2 holds 8 — the
    // owner's GpPartialChordSlideTests shape, one string up.
    private static List<ChordNote> PartialSlide() =>
    [
        new() { StringIndex = 1, Fret = 5, SlideTo = 6 },
        new() { StringIndex = 2, Fret = 8 },
    ];

    private static IReadOnlyList<ChordNote> InScope(IEnumerable<ChordNote> notes, StringEditMask mask) =>
        InspectorViewModel.MaskedChordNotes(notes, mask);

    private static Dictionary<int, int?> Apply(List<ChordNote> notes, StringEditMask mask, int? typed)
    {
        var plan = ChordSlideScope.Plan(InScope(notes, mask), typed);
        Assert.Null(plan.Refusal);
        plan.ApplyTo(notes);
        return notes.ToDictionary(n => n.StringIndex, n => n.SlideTo);
    }

    // ---- the readout ---------------------------------------------------------

    [Fact]
    public void APartialSlideChord_ReadsAsMixed_NotAsTheLowestStringsTarget()
    {
        var readout = ChordSlideScope.Read(InScope(PartialSlide(), StringEditMask.Empty));

        Assert.True(readout.IsMixed, "a chord where one string slides and one holds read as one agreed slide.");
        Assert.Null(readout.Target);
        Assert.Equal(new[] { 1, 2 }, readout.Strings.Select(n => n.StringIndex));
    }

    [Fact]
    public void StringsThatSlideByTheSameAmount_ReadAsTheLowestFrettedStringsTarget()
    {
        // 5→7 and 8→10: one slide of two frets, named by the 5's end — EOF's
        // chord slideend.
        List<ChordNote> chord =
        [
            new() { StringIndex = 1, Fret = 8, SlideTo = 10 },
            new() { StringIndex = 2, Fret = 5, SlideTo = 7 },
        ];

        var readout = ChordSlideScope.Read(InScope(chord, StringEditMask.Empty));

        Assert.False(readout.IsMixed);
        Assert.Equal(7, readout.Target);
    }

    [Fact]
    public void StringsSlidingToTheSameFretFromDifferentFrets_AreMixed()
    {
        // The state the OLD absolute write produced: 5→7 and 8→7 is two
        // different slides, and must not read as a single "7".
        List<ChordNote> chord =
        [
            new() { StringIndex = 1, Fret = 5, SlideTo = 7 },
            new() { StringIndex = 2, Fret = 8, SlideTo = 7 },
        ];

        var readout = ChordSlideScope.Read(InScope(chord, StringEditMask.Empty));

        Assert.True(readout.IsMixed);
        Assert.Null(readout.Target);
    }

    [Fact]
    public void AChordWithNoSlide_ReadsEmptyAndNotMixed()
    {
        List<ChordNote> chord = [new() { StringIndex = 1, Fret = 5 }, new() { StringIndex = 2, Fret = 7 }];

        var readout = ChordSlideScope.Read(InScope(chord, StringEditMask.Empty));

        Assert.False(readout.IsMixed);
        Assert.Null(readout.Target);
    }

    // ---- a typed target --------------------------------------------------------

    [Fact]
    public void ATypedTargetOnTwoStringsWithDifferentFrets_MovesEachByTheSameOffset()
    {
        var slides = Apply(PartialSlide(), StringEditMask.Empty, 7);

        // 7 is the 5's target (+2); the 8 moves +2 as well. The old write made
        // it 7, a slide DOWN.
        Assert.Equal(7, slides[1]);
        Assert.Equal(10, slides[2]);
    }

    [Fact]
    public void ATypedTarget_IsAnchoredOnTheLowestFRET_NotTheLowestString()
    {
        // rscharter's setSlide takes the minimum fret, not the first string.
        List<ChordNote> chord =
        [
            new() { StringIndex = 0, Fret = 7 },
            new() { StringIndex = 1, Fret = 5 },
        ];

        var slides = Apply(chord, StringEditMask.Empty, 3);

        Assert.Equal(5, slides[0]);
        Assert.Equal(3, slides[1]);
    }

    [Fact]
    public void AnOpenString_NeitherAnchorsNorMoves()
    {
        List<ChordNote> chord =
        [
            new() { StringIndex = 0, Fret = 0 },
            new() { StringIndex = 1, Fret = 5 },
            new() { StringIndex = 2, Fret = 7 },
        ];

        var slides = Apply(chord, StringEditMask.Empty, 7);

        Assert.Null(slides[0]);
        Assert.Equal(7, slides[1]);
        Assert.Equal(9, slides[2]);
    }

    [Fact]
    public void ClearingTheField_ClearsEveryStringInScope()
    {
        List<ChordNote> chord =
        [
            new() { StringIndex = 1, Fret = 5, SlideTo = 7 },
            new() { StringIndex = 2, Fret = 8, SlideTo = 10 },
        ];

        var slides = Apply(chord, StringEditMask.Empty, null);

        Assert.All(slides.Values, Assert.Null);
    }

    [Theory]
    [InlineData(22, 2, 25)] // the 8 would end on 25
    [InlineData(0, 1, 0)]   // the 5 would end on the nut: not a slide target
    public void ATargetThatPushesAStringOffTheFretboard_IsRefusedWhole(int typed, int offString, int wouldEndOn)
    {
        var notes = PartialSlide();

        var plan = ChordSlideScope.Plan(InScope(notes, StringEditMask.Empty), typed);

        Assert.NotNull(plan.Refusal);
        Assert.Equal(offString, plan.Refusal!.Value.StringIndex);
        Assert.Equal(wouldEndOn, plan.Refusal.Value.WouldEndOn);
        Assert.Empty(plan.Writes);
        plan.ApplyTo(notes);
        Assert.Equal(6, notes[0].SlideTo);
        Assert.Null(notes[1].SlideTo);
    }

    // ---- the string-edit mask --------------------------------------------------

    [Fact]
    public void MaskingTheSlidingString_ReadsItsTarget_AndATypedTargetMovesOnlyIt()
    {
        var notes = PartialSlide();
        var mask = StringEditMask.FromStrings(1);

        var readout = ChordSlideScope.Read(InScope(notes, mask));
        Assert.False(readout.IsMixed);
        Assert.Equal(6, readout.Target);

        var slides = Apply(notes, mask, 7);
        Assert.Equal(7, slides[1]);
        Assert.Null(slides[2]);
    }

    [Fact]
    public void MaskingTheHeldString_ReadsNoSlide_AndATypedTargetLeavesTheOtherString()
    {
        var notes = PartialSlide();
        var mask = StringEditMask.FromStrings(2);

        var readout = ChordSlideScope.Read(InScope(notes, mask));
        Assert.False(readout.IsMixed);
        Assert.Null(readout.Target);

        var slides = Apply(notes, mask, 9);
        Assert.Equal(6, slides[1]);
        Assert.Equal(9, slides[2]);
    }

    [Fact]
    public void AMaskThatMissesTheChord_ReadsNothingAndWritesNothing()
    {
        var notes = PartialSlide();
        var mask = StringEditMask.FromStrings(5);

        Assert.False(ChordSlideScope.Read(InScope(notes, mask)).IsMixed);
        var plan = ChordSlideScope.Plan(InScope(notes, mask), 9);
        Assert.Null(plan.Refusal);
        Assert.Empty(plan.Writes);
    }

    // ---- "Apply techniques to all strings" -------------------------------------

    [Fact]
    public void CopyingASlideToTheOtherStrings_CopiesItsOffset()
    {
        var notes = PartialSlide();

        var plan = ChordSlideScope.PlanOffset(ChordSlideScope.Movers(notes), ChordSlideScope.Offset(notes[0])!.Value);
        plan.ApplyTo(notes);

        Assert.Equal(6, notes[0].SlideTo);
        Assert.Equal(9, notes[1].SlideTo);
    }

    // ---- the inspector asks this rule ------------------------------------------

    /// <summary>
    ///     The rule above is only worth anything while the inspector's two slide
    ///     writers and its readout go through it, with the MASK — rather than
    ///     an absolute <c>cn.SlideTo = value</c> broadcast.
    /// </summary>
    [Fact]
    public void TheInspectorsSlideWritersAndReadout_AskThisRule()
    {
        var source = InspectorSource();

        var typed = BodyOf(source, "partial void OnChordNoteSlideToChanged(int? value)");
        Assert.Contains("ChordSlideScope.Plan(MaskedChordNotes(", typed);
        Assert.Contains("StringEditMask", typed);
        Assert.DoesNotContain("cn.SlideTo = value", typed);

        var broadcast = BodyOf(source, "private void ApplyTechniquesToAllStrings()");
        Assert.Contains("CopiedSlide(", broadcast);
        Assert.DoesNotContain("cn.SlideTo =", broadcast);

        var readout = BodyOf(source, "private void SyncChordSlide()");
        Assert.Contains("ChordSlideScope.Read(MaskedChordNotes(", readout);
        Assert.DoesNotContain("ChordNoteSlideTo = src.SlideTo", source);
    }

    private static string InspectorSource()
    {
        var dir = UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "ViewModels", "Editor"));
        var files = UiSourceTree.GetFiles(dir, "InspectorViewModel*.cs");
        Assert.NotEmpty(files);
        return Regex.Replace(string.Join("\n", files.Select(UiSourceTree.ReadAllText)), @"^\s*//.*$", "",
            RegexOptions.Multiline);
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
}
