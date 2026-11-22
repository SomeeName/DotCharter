using System.Collections.ObjectModel;
using DotCharter.Core.Domain.Vocals;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>The tap-sync queue behind the lyrics inspector: advance, end, undo restore, redo reapply.</summary>
public sealed class LyricTapSessionTests
{
    private readonly HistoryService _history = new(new FakeAppLogger());
    private readonly VocalTrack _track = new() { Name = "Vocals" };
    private readonly ObservableCollection<BeatMarker> _beats = [];
    private readonly LyricTapSession _session = new();
    private readonly List<LyricTapChange> _changes = [];

    public LyricTapSessionTests()
    {
        for (var i = 0; i < 32; i++)
            _beats.Add(new BeatMarker { Time = i * 500, Bpm = 120, BeatsInMeasure = 4, IsMeasure = i % 4 == 0 });
        _session.Changed += _changes.Add;
    }

    private LyricTapOutcome Tap(double ms, out string text)
        => _session.TapNext(_track, _beats, ms, _history, out text);

    [Fact]
    public void TappingAndUndoing_MoveTheEndFlagWithTheLastSyllable()
    {
        _session.Load("one two");
        Tap(1000, out _);
        var first = _track.Phrases.Single().Lyrics.Single();
        Assert.True(first.IsPhraseEnd);

        Tap(1500, out _);
        var last = _track.Phrases.Single().Lyrics[^1];
        Assert.False(first.IsPhraseEnd);
        Assert.True(last.IsPhraseEnd);

        _history.Undo();
        Assert.Same(first, _track.Phrases.Single().Lyrics.Single());
        Assert.True(first.IsPhraseEnd);
        _history.Redo();
        Assert.Same(last, _track.Phrases.Single().Lyrics[^1]);
        Assert.False(first.IsPhraseEnd);
        Assert.True(last.IsPhraseEnd);
    }

    [Fact]
    public void Load_queues_syllables_and_previews_them_without_dashes()
    {
        Assert.Equal(5, _session.Load("hel-lo world\r\nsec-ond"));
        Assert.Equal("hel lo world sec ond", _session.Preview);
        Assert.Equal(5, _session.Count);
    }

    [Fact]
    public void Tap_advances_the_queue_and_groups_syllables_by_phrase()
    {
        _session.Load("hel-lo\nworld");

        Assert.Equal(LyricTapOutcome.Tapped, Tap(1000, out var first));
        Assert.Equal(LyricTapOutcome.Tapped, Tap(1500, out var second));
        Assert.Equal(LyricTapOutcome.Tapped, Tap(3000, out _));

        Assert.Equal("hel", first);
        Assert.Equal("lo", second);
        Assert.Equal(0, _session.Count);
        Assert.Equal(2, _track.Phrases.Count);
        Assert.Equal(["hel", "lo"], _track.Phrases[0].Lyrics.Select(l => l.Text));
        Assert.True(_track.Phrases[0].Lyrics[0].IsWordPart);
        Assert.Equal(["world"], _track.Phrases[1].Lyrics.Select(l => l.Text));
    }

    [Fact]
    public void Repeated_words_are_consumed_once_and_fully_restored_by_undo()
    {
        _session.Load("na na hey");

        Tap(1000, out var first);
        Assert.Equal("na", first);
        Assert.Equal(2, _session.Count);
        Assert.Equal("na hey", _session.Preview);

        _history.Undo();
        Assert.Equal(3, _session.Count);
        Assert.Equal("na na hey", _session.Preview);
        Assert.Empty(_track.Phrases);

        _history.Redo();
        Assert.Equal("na hey", _session.Preview);
        Tap(1500, out var second);
        Tap(2000, out var third);
        Assert.Equal("na", second);
        Assert.Equal("hey", third);
        Assert.Equal(0, _session.Count);
        Assert.Equal(["na", "na", "hey"], _track.Phrases.Single().Lyrics.Select(l => l.Text));

        _history.Undo();
        _history.Undo();
        _history.Undo();
        Assert.Equal(3, _session.Count);
        Assert.Equal("na na hey", _session.Preview);
        Assert.Empty(_track.Phrases);
    }

    [Fact]
    public void Tap_without_a_track_or_beats_does_not_consume_the_queue()
    {
        _session.Load("a b");

        Assert.Equal(LyricTapOutcome.NoVocalTrack,
            _session.TapNext(null, _beats, 0, _history, out _));
        Assert.Equal(LyricTapOutcome.NoBeats,
            _session.TapNext(_track, [], 0, _history, out _));
        Assert.Equal(2, _session.Count);
        Assert.Empty(_track.Phrases);
    }

    [Fact]
    public void The_last_tap_empties_the_queue()
    {
        _session.Load("only");
        Tap(1000, out _);
        Assert.Equal(0, _session.Count);
        Assert.Equal("", _session.Preview);
    }

    [Fact]
    public void Undo_puts_the_syllable_back_and_removes_the_chart_edit()
    {
        _session.Load("hel-lo");
        Tap(1000, out _);
        Tap(1500, out _);
        _changes.Clear();

        _history.Undo();

        Assert.Equal(1, _session.Count);
        Assert.Equal("lo", _session.Preview);
        Assert.Equal([LyricTapChange.Undone], _changes);
        Assert.Equal(["hel"], _track.Phrases.Single().Lyrics.Select(l => l.Text));

        _history.Undo();

        Assert.Equal(2, _session.Count);
        Assert.Empty(_track.Phrases);
    }

    [Fact]
    public void Redo_reapplies_the_tap_and_pops_the_queue_again()
    {
        _session.Load("hel-lo");
        Tap(1000, out _);
        Tap(1500, out _);
        _history.Undo();
        _history.Undo();
        _changes.Clear();

        _history.Redo();
        _history.Redo();

        Assert.Equal(0, _session.Count);
        Assert.Equal([LyricTapChange.Redone, LyricTapChange.Redone], _changes);
        Assert.Equal(["hel", "lo"], _track.Phrases.Single().Lyrics.Select(l => l.Text));
    }

    [Theory]
    [InlineData("la la", 2)]
    [InlineData("la la la", 3)]
    public void One_tap_of_a_repeated_word_consumes_exactly_one_queued_syllable(string raw, int queued)
    {
        _session.Load(raw);

        Tap(1000, out var tapped);
        Assert.Equal("la", tapped);
        Assert.Equal(["la"], _track.Phrases.Single().Lyrics.Select(l => l.Text));
        Assert.Equal(queued - 1, _session.Count);

        _history.Undo();
        Assert.Equal(queued, _session.Count);
        Assert.Empty(_track.Phrases);

        _history.Redo();
        Assert.Equal(queued - 1, _session.Count);
        Assert.Equal(["la"], _track.Phrases.Single().Lyrics.Select(l => l.Text));
    }

    [Fact]
    public void A_line_ending_in_a_word_part_joins_the_next_line_and_no_syllable_carries_both_flags()
    {
        _session.Load("la hel-\nlo\nfa mo-");

        foreach (var ms in new[] { 1000.0, 1500, 2000, 4000, 4500 })
        {
            Tap(ms, out _);
            AssertNeverBoth();
        }

        Assert.Equal(["la hel- lo", "fa mo"], Lines());
        _history.Undo();
        Assert.Equal(["la hel- lo", "fa"], Lines());
        _history.Undo();
        _history.Undo();
        Assert.Equal(["la hel"], Lines());
        AssertNeverBoth();
        _history.Redo();
        Assert.Equal(["la hel- lo"], Lines());
        AssertNeverBoth();
        _history.Redo();
        _history.Redo();
        Assert.Equal(["la hel- lo", "fa mo"], Lines());
        AssertNeverBoth();
    }

    private List<string> Lines()
        => [.. _track.Phrases.Select(p => string.Join(" ", p.Lyrics.Select(l => l.Text + (l.IsWordPart ? "-" : ""))))];

    private void AssertNeverBoth()
    {
        var lyrics = _track.Phrases.SelectMany(p => p.Lyrics).ToList();
        Assert.DoesNotContain(lyrics, l => l.IsWordPart && l.IsPhraseEnd);
        Assert.All(_track.Phrases, p => Assert.True(p.Lyrics[^1].IsPhraseEnd));
    }
}
