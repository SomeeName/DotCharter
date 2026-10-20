using DotCharter.Services.Editing.DraftMerge;
using static DotCharter.Tests.Services.Editing.DraftMerge.DraftMergeFixture;

namespace DotCharter.Tests.Services.Editing.DraftMerge;

/// <summary>
///     Cutting a chart-from-audio draft by the arrangement's phrases (#647). The draft's note F1 is
///     about 12 %, so by default only EMPTY phrases are offered and "replace existing notes" is an
///     explicit choice; the draft is spelled for this arrangement, so a note it cannot play is dropped.
///     Fixture: <see cref="DraftMergeFixture" /> (intro 0, verse 4, chorus 8, END 12).
/// </summary>
public sealed class DraftMergeReviewTests
{
    private readonly DraftMergeFixture _f = new();

    [Fact]
    public void Only_empty_phrases_are_offered_by_default_and_the_others_are_counted()
    {
        _f.Seed(Note(4, 0, 3));
        GuitarNote[] draft = [Note(0, 0, 3), Note(5, 1, 5), Note(9, 2, 7)];

        var review = _f.Plan(draft);

        Assert.Equal(["intro", "chorus"], review.Phrases.Select(p => p.Name));
        Assert.All(review.Phrases, p => Assert.False(p.Replaces));
        Assert.Equal(1, review.Occupied);
        Assert.False(review.ReplaceExisting);
    }

    [Fact]
    public void Replace_existing_notes_offers_the_occupied_phrase_with_its_diff()
    {
        _f.Seed(Note(4, 0, 3), Note(5, 1, 5));
        GuitarNote[] draft = [Note(4, 0, 5), Note(5, 1, 5), Note(6, 2, 2)];

        var review = _f.Plan(draft, replaceExisting: true);

        var verse = Assert.Single(review.Phrases);
        Assert.Equal("verse", verse.Name);
        Assert.True(verse.Replaces);
        Assert.Equal(new DraftPhraseDiff(1, 1, 0, 1), verse.Diff);
        Assert.Equal(0, review.Occupied);
    }

    [Fact]
    public void A_phrase_the_draft_restates_or_hears_nothing_in_is_not_offered()
    {
        _f.Seed(Note(4, 0, 3), Note(9, 0, 3));

        // The verse restates what is there; the chorus has notes but the draft heard nothing there.
        var review = _f.Plan([Note(4, 0, 3)], replaceExisting: true);

        Assert.Empty(review.Phrases);
    }

    [Fact]
    public void A_structural_phrase_takes_no_draft_notes()
    {
        var review = _f.Plan([Note(12, 0, 3), Note(13, 1, 3)]);

        Assert.Empty(review.Phrases);
    }

    [Fact]
    public void Each_phrase_spans_to_the_next_marker_with_its_times()
    {
        var review = _f.Plan([Note(3, 0, 3, half: 1), Note(4, 0, 5)]);

        Assert.Equal(["intro", "verse"], review.Phrases.Select(p => p.Name));
        Assert.Equal((0d, 2000d), (review.Phrases[0].StartMs, review.Phrases[0].EndMs!.Value));
        Assert.Equal(new FractionalPosition(8), review.Phrases[1].End);
        Assert.Same(_f.Arrangement.EventPoints[1], review.Phrases[1].Marker);
    }

    [Fact]
    public void Notes_before_the_first_phrase_and_on_a_chart_with_none_get_their_own_span()
    {
        _f.Arrangement.EventPoints[0].Position = new FractionalPosition(2);
        var before = _f.Plan([Note(1, 0, 3)]);
        Assert.Equal((FractionalPosition.Zero, new FractionalPosition(2)), (before.Phrases[0].Start, before.Phrases[0].End!.Value));
        Assert.Equal("", before.Phrases[0].Name);

        _f.Arrangement.EventPoints.Clear();
        var whole = Assert.Single(_f.Plan([Note(1, 0, 3), Note(14, 0, 3)]).Phrases);
        Assert.Null(whole.End);
        Assert.Equal(2, whole.Draft.Count);
    }

    [Fact]
    public void A_note_below_the_capo_or_off_the_strings_is_dropped()
    {
        var capoed = new DraftMergeFixture(capo: 3);
        GuitarNote[] draft = [Note(0, 0, 0), Note(0, 1, 2), Note(1, 2, 3), Note(1, 6, 5), Note(2, 3, 5)];

        var intro = Assert.Single(capoed.Plan(draft).Phrases);

        // Fret 0 IS the capo and fret 3 sits on it; fret 2 cannot sound and there is no seventh string.
        Assert.Equal([(0, 0), (2, 3), (3, 5)], intro.Draft.Select(n => (n.StringIndex, n.Fret)));
    }

    [Fact]
    public void A_phrase_carries_its_notes_mean_confidence()
    {
        GuitarNote[] draft = [Note(0, 0, 3), Note(1, 0, 3), Note(5, 0, 3)];

        var review = _f.Plan(draft, confidences: [0.2, 0.4, 0.9]);

        Assert.Equal(0.3, review.Phrases[0].Confidence, 6);
        Assert.Equal(0.9, review.Phrases[1].Confidence, 6);
    }

    [Fact]
    public void Planning_writes_nothing()
    {
        _f.Seed(Note(4, 0, 3));
        var before = ChartProjectCloner.Clone(_f.Project);

        _f.Plan([Note(0, 0, 3), Note(4, 0, 5)], replaceExisting: true);

        Assert.Empty(DeepCompare.Diff("project", before, _f.Project, new HashSet<string>()));
    }
}
