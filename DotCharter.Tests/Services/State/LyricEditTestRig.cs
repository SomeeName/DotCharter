using System.Collections.ObjectModel;
using DotCharter.Core.Domain.Vocals;
using DotCharter.Services.Editing.Maintenance;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.Services.State;

internal sealed class LyricEditTestRig
{
    internal VocalTrack Track { get; } = new();
    internal HistoryService History { get; } = new(new FakeAppLogger());
    internal LyricTapSession Session { get; } = new();
    internal ObservableCollection<BeatMarker> Beats { get; } = [];

    internal LyricEditTestRig()
    {
        for (var i = 0; i < 128; i++)
            Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120, BeatsInMeasure = 4, IsMeasure = i % 4 == 0 });
    }

    internal LyricEvent Tap(double ms)
    {
        var before = Members();
        Assert.Equal(LyricTapOutcome.Tapped, Session.TapNext(Track, Beats, ms, History, out _));
        return Assert.Single(Members(), l => !before.Contains(l));
    }

    internal LyricEvent Insert(int beat)
    {
        var before = Members();
        Assert.True(History.Do(VocalInsertion.Create(Track, new FractionalPosition(beat),
            new FractionalPosition(beat, new Fraction(1, 2)))));
        return Assert.Single(Members(), l => !before.Contains(l));
    }

    internal HashSet<LyricEvent> Members() => new(Track.Phrases.SelectMany(p => p.Lyrics), ReferenceEqualityComparer.Instance);

    internal bool Boundary(LyricPhrase phrase, LyricEvent lyric, bool end) =>
        History.Do(new SetLyricPhraseEndAction(Track.Phrases, phrase, lyric, end));

    internal void AssertEnds()
    {
        foreach (var phrase in Track.Phrases)
        {
            for (var i = 0; i < phrase.Lyrics.Count; i++)
            {
                Assert.True(phrase.Lyrics[i].IsPhraseEnd == (i == phrase.Lyrics.Count - 1),
                    $"end flags: [{string.Join(", ", phrase.Lyrics.Select(l => l.Text + ":" + l.IsPhraseEnd))}]");
            }
        }
    }
}
