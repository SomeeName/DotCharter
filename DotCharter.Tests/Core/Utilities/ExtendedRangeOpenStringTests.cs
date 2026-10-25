using DotCharter.Services.Audio.LivePlay;
using DotCharter.Tone.Audio.Tuner;
using DotCharter.Services.Export.GuitarPro;
using DotCharter.Tone.Audio.Tuning;
using DotCharter.UI.Controls;

namespace DotCharter.Tests.Core.Utilities;

/// <summary>
///     A 7/8-string guitar's extra strings extend DOWN (#742): 7 = B1 E2 A2 D3 G3 B3 E4,
///     8 = F#1 B1 E2 …. Five private copies of <c>[0, 5, 10, 15, 19, 24, 29]</c> read
///     string 0 as E2 and the 7th as a high A4, so the gutter named a 7-string's lanes
///     E A D G B E A, the tuner asked for those notes and live judging graded every
///     string a fourth too high. Every consumer now reads <see cref="StandardOpenStrings" />;
///     these pin the answer each of them gives, and that 6-string guitar and 4/5/6-string
///     bass did not move.
/// </summary>
public sealed class ExtendedRangeOpenStringTests
{
    private static readonly int[] SevenStandard = [35, 40, 45, 50, 55, 59, 64];
    private static readonly int[] EightStandard = [30, 35, 40, 45, 50, 55, 59, 64];
    private static readonly int[] NineStandard = [25, 30, 35, 40, 45, 50, 55, 59, 64];

    private static Arrangement Instrument(ArrangementType type, int stringCount, params int[] steps)
    {
        var arrangement = new Arrangement { ArrangementType = type, StringCount = stringCount };
        for (var i = 0; i < stringCount; i++)
            arrangement.TuningPitches.Add(new TuningOffset(i < steps.Length ? steps[i] : 0));
        arrangement.Levels.Add(new ChartLevel());
        return arrangement;
    }

    private static Arrangement Guitar(int stringCount, params int[] steps) =>
        Instrument(ArrangementType.Lead, stringCount, steps);

    private static List<BeatMarker> Beats(int count = 16, double bpm = 120)
    {
        var ms = 60000.0 / bpm;
        return [.. Enumerable.Range(0, count).Select(i => new BeatMarker { Time = i * ms, Bpm = bpm })];
    }

    // ---- the table ----------------------------------------------------------

    [Fact]
    public void TheTable_ExtendsSevenAndEightStringsDown_AndKeepsSixAndFewerAsTheyWere()
    {
        Assert.Equal([0, 5, 10, 15], StandardOpenStrings.SemitonesAboveLowE(4));
        Assert.Equal([0, 5, 10, 15, 19], StandardOpenStrings.SemitonesAboveLowE(5));
        Assert.Equal([0, 5, 10, 15, 19, 24], StandardOpenStrings.SemitonesAboveLowE(6));
        Assert.Equal([-5, 0, 5, 10, 15, 19, 24], StandardOpenStrings.SemitonesAboveLowE(7));
        Assert.Equal([-10, -5, 0, 5, 10, 15, 19, 24], StandardOpenStrings.SemitonesAboveLowE(8));
        Assert.Equal([-15, -10, -5, 0, 5, 10, 15, 19, 24], StandardOpenStrings.SemitonesAboveLowE(9));
        Assert.Equal(9, StandardOpenStrings.MaxStringCount);

        // A string index past a <=6-string instrument keeps climbing the six-string
        // neck exactly as the old copies answered (19, 24, 29, then fourths).
        Assert.Equal(19, StandardOpenStrings.SemitonesAboveLowE(4, 4));
        Assert.Equal(29, StandardOpenStrings.SemitonesAboveLowE(6, 6));
        Assert.Equal(35, StandardOpenStrings.SemitonesAboveLowE(6, 7));
    }

    [Fact]
    public void TheReferencePitches_TheImportersSubtract_AreUnchanged()
    {
        Assert.Equal([28, 33, 38, 43], TuningUtilities.GetStandardMidiPitches(4));
        Assert.Equal([40, 45, 50, 55, 59], TuningUtilities.GetStandardMidiPitches(5));
        Assert.Equal([40, 45, 50, 55, 59, 64], TuningUtilities.GetStandardMidiPitches(6));
        Assert.Equal(SevenStandard, TuningUtilities.GetStandardMidiPitches(7));
        Assert.Equal(EightStandard, TuningUtilities.GetStandardMidiPitches(8));
        Assert.Equal(NineStandard, TuningUtilities.GetStandardMidiPitches(9));
        Assert.Equal([25, 30, 35, 40, 45, 50, 55, 59, 64, 64], TuningUtilities.GetStandardMidiPitches(10));
        Assert.Equal([40, 45, 50], TuningUtilities.GetStandardMidiPitches(3));
        Assert.Empty(TuningUtilities.GetStandardMidiPitches(0));
    }

    // ---- gutter + tuner -----------------------------------------------------

    [Fact]
    public void SevenString_GutterAndTunerRead_B1ToE4()
    {
        var seven = Guitar(7);

        Assert.Equal(["B", "E", "A", "D", "G", "B", "E"], TimelineGutterControl.StringNamesFor(seven, 7));

        var targets = TuningCheckTargets.Build(seven);
        Assert.Equal(SevenStandard, targets.Select(t => t.Midi));
        Assert.Equal(["B1", "E2", "A2", "D3", "G3", "B3", "E4"], targets.Select(t => t.NoteName));
        Assert.Equal(61.74, targets[0].FrequencyHz, 2);

        var logic = new ArrangementTunerLogic();
        logic.Load(ArrangementType.Lead, 7, new int[7]);
        Assert.Equal(SevenStandard, logic.Targets.Select(t => t.Midi));
    }

    [Fact]
    public void SevenString_DropA_MovesOnlyTheLowString()
    {
        var dropA = Guitar(7, -2);

        Assert.Equal(["A", "E", "A", "D", "G", "B", "E"], TimelineGutterControl.StringNamesFor(dropA, 7));
        Assert.Equal(["A1", "E2", "A2", "D3", "G3", "B3", "E4"],
            TuningCheckTargets.Build(dropA).Select(t => t.NoteName));
    }

    [Fact]
    public void EightString_GutterAndTunerRead_FSharp1ToE4()
    {
        var eight = Guitar(8);

        Assert.Equal(["F#", "B", "E", "A", "D", "G", "B", "E"], TimelineGutterControl.StringNamesFor(eight, 8));
        Assert.Equal(EightStandard, TuningCheckTargets.Build(eight).Select(t => t.Midi));
        Assert.Equal("F#1", TuningCheckTargets.Build(eight)[0].NoteName);
    }

    [Fact]
    public void NineString_GutterAndTunerRead_CSharp1ToE4()
    {
        var nine = Guitar(9);

        Assert.Equal(["C#", "F#", "B", "E", "A", "D", "G", "B", "E"], TimelineGutterControl.StringNamesFor(nine, 9));
        Assert.Equal(NineStandard, TuningCheckTargets.Build(nine).Select(t => t.Midi));
        Assert.Equal("C#1", TuningCheckTargets.Build(nine)[0].NoteName);
        Assert.Equal(NineStandard, Enumerable.Range(0, 9).Select(s => LivePitchAcceptance.SoundingMidi(nine, s, 0) ?? -1));
        Assert.Equal(NineStandard, Enumerable.Range(0, 9).Select(s => SoundingPitch.MidiFor(nine, s, 0) ?? -1));
    }

    // ---- live judging -------------------------------------------------------

    [Fact]
    public void SevenString_LiveJudgingExpectsWhatTheTunerTunesTo()
    {
        var seven = Guitar(7);
        var targets = TuningCheckTargets.Build(seven);

        for (var s = 0; s < 7; s++)
        {
            Assert.Equal(targets[s].Midi, LivePitchAcceptance.SoundingMidi(seven, s, 0));
            Assert.Equal(targets[s].Midi, SoundingPitch.MidiFor(seven, s, 0));
        }

        Assert.Equal(EightStandard, Enumerable.Range(0, 8).Select(s => LivePitchAcceptance.SoundingMidi(Guitar(8), s, 0) ?? -1));
    }

    [Theory]
    [InlineData(0, 35, true)]  // the low 7th string open: B1
    [InlineData(0, 40, false)] // …not the E2 the old table read string 0 as
    [InlineData(6, 64, true)]  // the top string open: E4
    [InlineData(6, 69, false)] // …not the A4 the old table expected there
    public void SevenString_OpenStrings_AreJudgedAtTheirRealPitch(int stringIndex, int played, bool hit)
    {
        var seven = Guitar(7);
        seven.Levels[0].Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(2),
            StringIndex = stringIndex,
            Fret = 0
        });

        var matcher = LiveHitWindows.CreateMatcher(LiveHitWindowPreset.Normal,
            LiveChordVoicingStrictness.Standard, stringAware: true);
        matcher.Load(seven, seven.Levels[0], Beats());

        var result = matcher.OfferNote(midi: played, timeMs: 1000);

        Assert.Equal(hit, result is { Verdict: LivePlayVerdict.Hit });
        Assert.Equal(hit ? 1 : 0, matcher.Stats.Hits);
    }

    // ---- unchanged instruments ---------------------------------------------

    [Theory]
    [InlineData(ArrangementType.Lead, 6, new[] { 40, 45, 50, 55, 59, 64 })]
    [InlineData(ArrangementType.Bass, 4, new[] { 28, 33, 38, 43 })]
    [InlineData(ArrangementType.Bass, 5, new[] { 28, 33, 38, 43, 47 })]
    [InlineData(ArrangementType.Bass, 6, new[] { 28, 33, 38, 43, 47, 52 })]
    public void SixStringGuitarAndBass_AreUnchanged(ArrangementType type, int stringCount, int[] expected)
    {
        var arrangement = Instrument(type, stringCount);

        Assert.Equal(expected, TuningCheckTargets.Build(arrangement).Select(t => t.Midi));
        Assert.Equal(expected, Enumerable.Range(0, stringCount)
            .Select(s => LivePitchAcceptance.SoundingMidi(arrangement, s, 0) ?? -1));
        Assert.Equal(expected, Enumerable.Range(0, stringCount).Select(s => SoundingPitch.MidiFor(arrangement, s, 0) ?? -1));

        // Off the instrument, the rule still stops at the sixth string.
        Assert.Null(SoundingPitch.MidiFor(arrangement, 6, 0));
    }

    // ---- export -------------------------------------------------------------

    [Fact]
    public void GpExport_WritesTheExtendedStringsLow()
    {
        Assert.Equal([64, 59, 55, 50, 45, 40, 35], GpExporter.BuildTuningHighToLow(Guitar(7)));
        Assert.Equal([64, 59, 55, 50, 45, 40, 35, 30], GpExporter.BuildTuningHighToLow(Guitar(8)));
        Assert.Equal([64, 59, 55, 50, 45, 40, 35, 30, 25], GpExporter.BuildTuningHighToLow(Guitar(9)));
        Assert.Equal([64, 59, 55, 50, 45, 40], GpExporter.BuildTuningHighToLow(Guitar(6)));
    }
}
