using DotCharter.Services.Editing.FretAnchors;

namespace DotCharter.Tests.Services.Editing.FretAnchors;

/// <summary>
///     <see cref="PhraseStartAnchors" />, the one E06 reading for code that ADDS the anchor a phrase
///     needs (the auto-fix and the AI chat's sections &amp; phrases job, #606): when a phrase needs
///     none, the anchor in force restated with the <c>capo + 1</c> floor, and the FHP generator's
///     only where no anchor is in force.
///     <para>
///         The chart: phrases "a" at beat 0 and "b" at beat 8, one level; each test adds the notes and
///         anchors it is about.
///     </para>
/// </summary>
public sealed class PhraseStartAnchorsTests
{
    private readonly Arrangement _arrangement = new() { Name = "Lead", Tuning = ["E", "A", "D", "G", "B", "e"] };
    private readonly ChartLevel _level = new();

    public PhraseStartAnchorsTests()
    {
        _arrangement.EventPoints.Add(new EventPoint { Position = At(0), Phrase = "a" });
        _arrangement.EventPoints.Add(new EventPoint { Position = At(8), Phrase = "b" });
        _arrangement.Levels.Add(_level);
    }

    private static FractionalPosition At(int beat) => new(beat);

    private void Note(int beat, int fret = 5) =>
        _level.Notes.Add(new GuitarNote { Position = At(beat), StringIndex = 2, Fret = fret });

    private static string? Text(FretAnchor? a) => a is null ? null : $"{a.Position.BeatId}:{a.Fret}/{a.Width}";

    private FretAnchor? NeededForB(IFretAnchorGenerationService? generator = null) =>
        PhraseStartAnchors.Needed(_arrangement, _level, PhraseMarkers.Playable(_arrangement), 1,
            generator ?? new Generator([]));

    [Fact]
    public void A_phrase_that_plays_nothing_on_the_level_needs_no_anchor()
    {
        Note(2);
        _level.Anchors.Add(new FretAnchor(At(0), 5, 4));

        Assert.Null(NeededForB());
    }

    [Fact]
    public void An_anchor_anywhere_from_the_phrase_start_to_its_first_sound_covers_it()
    {
        _level.Anchors.Add(new FretAnchor(At(0), 5, 4));
        Note(10);
        _level.Anchors.Add(new FretAnchor(At(9), 7, 4));

        Assert.Null(NeededForB());

        // ...but one AFTER the first sound does not.
        _level.Anchors.RemoveAt(1);
        _level.Anchors.Add(new FretAnchor(At(11), 7, 4));

        Assert.Equal("8:5/4", Text(NeededForB()));
    }

    [Fact]
    public void The_anchor_in_force_is_restated_at_the_phrase_start_and_the_generator_is_not_asked()
    {
        _level.Anchors.Add(new FretAnchor(At(0), 5, 4));
        _level.Anchors.Add(new FretAnchor(At(4), 9, 5));
        Note(10);
        var generator = new Generator([new FretAnchor(At(10), 12, 4)]);

        var needed = NeededForB(generator);

        Assert.Equal("8:9/5", Text(needed));
        Assert.Equal(0, generator.Calls);
    }

    [Fact]
    public void A_restated_anchor_is_floored_at_capo_plus_one()
    {
        _arrangement.Capo = 5;
        _level.Anchors.Add(new FretAnchor(At(0), 3, 4));
        Note(10, 7);

        Assert.Equal("8:6/4", Text(NeededForB()));
    }

    [Fact]
    public void With_none_in_force_the_generators_anchor_at_the_first_sound_is_placed_at_the_phrase_start()
    {
        Note(10);
        var generator = new Generator([new FretAnchor(At(9), 3, 4), new FretAnchor(At(10), 7, 5), new FretAnchor(At(12), 1, 4)]);

        Assert.Equal("8:7/5", Text(NeededForB(generator)));
        Assert.Equal(1, generator.Calls);
    }

    [Fact]
    public void With_none_in_force_and_none_generated_by_the_first_sound_there_is_nothing_to_place()
    {
        Note(10, 0);

        Assert.Null(NeededForB(new Generator([new FretAnchor(At(12), 5, 4)])));
    }

    [Fact]
    public void The_last_phrase_runs_to_the_end_of_the_level()
    {
        _level.Anchors.Add(new FretAnchor(At(0), 5, 4));
        Note(40);

        Assert.Equal(At(40), PhraseStartAnchors.FirstSound(_level, At(8), null));
        Assert.Null(PhraseStartAnchors.FirstSound(_level, At(8), At(40)));
        Assert.Equal("8:5/4", Text(NeededForB()));
    }

    private sealed class Generator(IReadOnlyList<FretAnchor> anchors) : IFretAnchorGenerationService
    {
        public int Calls { get; private set; }

        public IReadOnlyList<FretAnchor> GenerateAnchors(ChartLevel level, FractionalPosition? rangeStart = null,
            FractionalPosition? rangeEnd = null, IReadOnlyList<ChordTemplate>? chordTemplates = null, int capo = 0,
            bool bassArrangement = false, Func<ChordTemplate, ChordTemplate?>? freeGrip = null, Arrangement? tuning = null)
        {
            Calls++;
            return anchors;
        }
    }
}
