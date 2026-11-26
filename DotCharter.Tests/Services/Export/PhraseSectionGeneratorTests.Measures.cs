using DotCharter.Services.Export;

namespace DotCharter.Tests.Services.Export;

public sealed partial class PhraseSectionGeneratorTests
{
    private const double MeasureMs = 2000;

    [Fact]
    public void Body_phrases_start_on_bar_lines_and_run_about_four_measures()
    {
        var beats = CreateBeats(200);
        var arrangement = NewArrangement();
        for (var ms = 0.0; ms <= 80000.0; ms += 500.0) AddNote(arrangement, beats, ms);

        Assert.True(PhraseSectionGenerator.GenerateIfAbsent(arrangement, beats, 100000));

        var starts = IterationsByTime(arrangement, beats)
            .Where(i => i.Phrase != "COUNT")
            .Select(i => i.TimeMs)
            .ToList();
        Assert.All(starts.SkipLast(1), t => Assert.Equal(0, t % MeasureMs, Tolerance));
        var lengths = starts.Zip(starts.Skip(1), (a, b) => (b - a) / MeasureMs).ToList();
        Assert.True(lengths.Count >= 9, $"expected about ten phrases, got {lengths.Count}");
        // One unchanging pattern: every phrase is four measures, the last one runs on to END.
        Assert.All(lengths.SkipLast(1), measures => Assert.Equal(4, measures, 0.01));
        Assert.InRange(lengths[^1], 2, 8);
    }

    [Fact]
    public void A_pattern_change_one_measure_past_four_moves_the_cut_onto_it()
    {
        var beats = CreateBeats(200);
        var arrangement = NewArrangement();
        // Fret 5 for five measures, then fret 7: the change sits at measure 5 (10000 ms).
        for (var ms = 0.0; ms < 10000.0; ms += 500.0) AddNote(arrangement, beats, ms, fret: 5);
        for (var ms = 10000.0; ms <= 40000.0; ms += 500.0) AddNote(arrangement, beats, ms, fret: 7);

        Assert.True(PhraseSectionGenerator.GenerateIfAbsent(arrangement, beats, 60000));

        var body = IterationsByTime(arrangement, beats).Where(i => i.Phrase.StartsWith('p') && i.Phrase != "p0").ToList();
        Assert.Equal(10000, body[0].TimeMs, Tolerance);
    }

    [Fact]
    public void The_first_section_is_named_intro_and_sits_at_the_first_note()
    {
        var beats = CreateBeats();
        var arrangement = NewArrangement();
        for (var ms = 3000.0; ms <= 20000.0; ms += 500.0) AddNote(arrangement, beats, ms);

        Assert.True(PhraseSectionGenerator.GenerateIfAbsent(arrangement, beats, 32000));

        var first = Sections(arrangement, beats)[0];
        Assert.Equal("intro", first.Section);
        Assert.Equal(3000, first.TimeMs, Tolerance);
    }
}
