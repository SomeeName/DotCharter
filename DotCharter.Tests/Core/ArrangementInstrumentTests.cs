using System.Collections.ObjectModel;

namespace DotCharter.Tests.Core;

/// <summary>
///     Shapes of real CDLC arrangements (#161), built by hand from what the
///     owner's library holds — no game data copied. Rocksmith has no 5/6-string
///     bass path, so authors chart one on a GUITAR path with the reference an
///     octave down (A220 = CentOffset −1200) and bass offsets against the guitar's
///     open strings. All 44 such arrangements in the library sit at exactly −1200
///     with offsets <c>-5 -5 -5 -5 -4 -4</c> (B E A D G C), a 5-string variant
///     leaving the sixth slot at 0; no other guitar path goes below −45 ¢.
/// </summary>
internal static class GuitarPathBassShapes
{
    public static Arrangement FiveStringBassOnBonusRhythm() => Make(ArrangementType.Rhythm, -1200,
        [-5, -5, -5, -5, -4, 0], ArrangementSubtype.Bonus, "Bonus Rhythm");

    public static Arrangement SixStringBassOnAltRhythm() => Make(ArrangementType.Rhythm, -1200,
        [-5, -5, -5, -5, -4, -4], ArrangementSubtype.Alternate, "Alt. Rhythm");

    public static Arrangement SixStringBassOnLead() => Make(ArrangementType.Lead, -1200, [-5, -5, -5, -5, -4, -4]);

    public static Arrangement GuitarRhythm(double centOffset = 0) =>
        Make(ArrangementType.Rhythm, centOffset, [0, 0, 0, 0, 0, 0]);

    public static Arrangement GuitarDropC() => Make(ArrangementType.Rhythm, 0, [-4, -2, -2, -2, -2, -2]);

    public static Arrangement BassPath() => Make(ArrangementType.Bass, 0, [0, 0, 0, 0, 0, 0]);

    /// <summary>A real Bass path at A220 tuned up a fifth: B E A D on four strings.</summary>
    public static Arrangement BassPathAtA220() => Make(ArrangementType.Bass, -1200, [7, 7, 7, 7, 7, 7]);

    public static Arrangement Make(ArrangementType type, double centOffset, int[] offsets,
        ArrangementSubtype subtype = ArrangementSubtype.Main, string name = "")
        => new()
        {
            ArrangementType = type,
            Subtype = subtype,
            Name = name.Length > 0 ? name : type.ToString(),
            CentOffset = centOffset,
            StringCount = 6,
            TuningPitches = new ObservableCollection<TuningOffset>(offsets.Select(o => new TuningOffset(o))),
            Tuning = TuningUtilities.GetTuningNames(6, offsets),
        };
}

public sealed class ArrangementInstrumentTests
{
    [Fact]
    public void A_five_string_bass_on_Bonus_Rhythm_at_A220_is_a_bass_part() =>
        Assert.True(ArrangementInstrument.IsBassPart(GuitarPathBassShapes.FiveStringBassOnBonusRhythm()));

    [Fact]
    public void A_six_string_bass_on_a_guitar_path_at_A220_is_a_bass_part()
    {
        Assert.True(ArrangementInstrument.IsBassPart(GuitarPathBassShapes.SixStringBassOnAltRhythm()));
        Assert.True(ArrangementInstrument.IsBassPart(GuitarPathBassShapes.SixStringBassOnLead()));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-50.0)]
    [InlineData(-45.0)]
    [InlineData(65.0)]
    public void A_guitar_Rhythm_path_near_A440_stays_guitar(double centOffset) =>
        Assert.False(ArrangementInstrument.IsBassPart(GuitarPathBassShapes.GuitarRhythm(centOffset)));

    [Fact]
    public void A_guitar_drop_tuning_at_standard_reference_stays_guitar() =>
        Assert.False(ArrangementInstrument.IsBassPart(GuitarPathBassShapes.GuitarDropC()));

    [Fact]
    public void A_bass_path_is_a_bass_part_at_any_reference()
    {
        Assert.True(ArrangementInstrument.IsBassPart(GuitarPathBassShapes.BassPath()));
        Assert.True(ArrangementInstrument.IsBassPart(GuitarPathBassShapes.BassPathAtA220()));
    }

    [Fact]
    public void The_threshold_sits_between_detuned_guitars_and_A220()
    {
        Assert.False(ArrangementInstrument.IsBassPart(GuitarPathBassShapes.GuitarRhythm(-1149)));
        Assert.True(ArrangementInstrument.IsBassPart(GuitarPathBassShapes.GuitarRhythm(-1150)));
    }

    [Fact]
    public void A_vocal_track_is_never_a_bass_part()
    {
        var vocal = GuitarPathBassShapes.Make(ArrangementType.Vocal, -1200, [0, 0, 0, 0, 0, 0]);
        Assert.False(ArrangementInstrument.IsBassPart(vocal, () => true));
    }

    [Fact]
    public void Bass_tones_make_a_guitar_path_at_A440_a_bass_part_and_are_asked_only_when_needed()
    {
        var asked = 0;
        Assert.True(ArrangementInstrument.IsBassPart(GuitarPathBassShapes.GuitarRhythm(), () => ++asked > 0));
        Assert.False(ArrangementInstrument.IsBassPart(GuitarPathBassShapes.GuitarRhythm(), () => false));
        Assert.Equal(1, asked);

        // The path and the reference answer first: the tones are not parsed.
        Assert.True(ArrangementInstrument.IsBassPart(GuitarPathBassShapes.BassPath(), () => throw new InvalidOperationException()));
        Assert.True(ArrangementInstrument.IsBassPart(GuitarPathBassShapes.SixStringBassOnLead(), () => throw new InvalidOperationException()));
    }

    [Theory]
    [InlineData(-1200.0, -12, 0.0)]
    [InlineData(-1180.0, -12, 20.0)]
    [InlineData(0.0, 0, 0.0)]
    [InlineData(-45.0, 0, -45.0)]
    public void An_A220_reference_is_an_octave_of_the_note_not_cents(double centOffset, int octave, double residual)
    {
        Assert.Equal(octave, ArrangementInstrument.ReferenceOctaveSemitones(centOffset));
        Assert.Equal(residual, ArrangementInstrument.ResidualCents(centOffset), 9);
    }

    [Fact]
    public void Tuning_names_read_a_guitar_path_bass_as_the_bass_it_is()
    {
        Assert.Equal("5-String Bass Standard", TuningNameResolver.ResolveFor(GuitarPathBassShapes.FiveStringBassOnBonusRhythm()));
        Assert.Equal("6-String Bass Standard", TuningNameResolver.ResolveFor(GuitarPathBassShapes.SixStringBassOnAltRhythm()));
        Assert.Equal("Drop C", TuningNameResolver.ResolveFor(GuitarPathBassShapes.GuitarDropC()));
        Assert.Equal("E Standard", TuningNameResolver.ResolveFor(GuitarPathBassShapes.BassPath()));
    }

    [Fact]
    public void The_same_offsets_on_a_guitar_at_A440_stay_unnamed_rather_than_called_a_bass()
    {
        var guitar = GuitarPathBassShapes.Make(ArrangementType.Rhythm, 0, [-5, -5, -5, -5, -4, 0]);
        Assert.Null(TuningNameResolver.ResolveFor(guitar));
    }

    [Fact]
    public void The_sounding_name_of_a_guitar_path_bass_is_its_bass_octave()
    {
        var five = GuitarPathBassShapes.FiveStringBassOnBonusRhythm();
        var six = GuitarPathBassShapes.SixStringBassOnAltRhythm();

        Assert.Equal("B0", SoundingPitch.NameFor(five, 0, 0));
        Assert.Equal("G2", SoundingPitch.NameFor(five, 4, 0));
        Assert.Equal("B0", SoundingPitch.NameFor(six, 0, 0));
        Assert.Equal("C3", SoundingPitch.NameFor(six, 5, 0));
        // MidiFor stays the chart's pitch at its own reference: the -1200 cents are
        // added where the sound is made, so the octave is never applied twice.
        Assert.Equal(35, SoundingPitch.MidiFor(six, 0, 0));
        Assert.Equal("E2", SoundingPitch.NameFor(GuitarPathBassShapes.GuitarRhythm(), 0, 0));
        Assert.Equal("E1", SoundingPitch.NameFor(GuitarPathBassShapes.BassPath(), 0, 0));
    }
}
