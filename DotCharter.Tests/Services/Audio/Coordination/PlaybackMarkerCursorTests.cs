using System.Collections.ObjectModel;
using DotCharter.Services.Audio.Coordination;

namespace DotCharter.Tests.Services.Audio.Coordination;

/// <summary>
///     #309: <c>AudioPlaybackCoordinator</c> walked back from its cursor to the nearest section
///     and to the nearest phrase on every ≤ 60 Hz publish — to the start of the song on a chart
///     with phrases but no sections. <see cref="PlaybackMarkerCursor" /> latches the owners; these
///     pin both the answers (the old walk's) and when the walk happens.
/// </summary>
public sealed class PlaybackMarkerCursorTests
{
    // One beat every 500 ms; event i sits on beat 2i (every second).
    private static List<BeatMarker> Beats(int count)
    {
        var beats = new List<BeatMarker>();
        for (var i = 0; i < count; i++) beats.Add(new BeatMarker { Time = i * 500.0 });
        return beats;
    }

    private static ObservableCollection<EventPoint> Events(params (string Section, string Phrase)[] markers)
    {
        var events = new ObservableCollection<EventPoint>();
        for (var i = 0; i < markers.Length; i++)
        {
            events.Add(new EventPoint
            {
                Position = new FractionalPosition(2 * i), Section = markers[i].Section, Phrase = markers[i].Phrase
            });
        }

        return events;
    }

    [Fact]
    public void ItReportsTheMarkersInForce_BeforeBetweenAndAfterThem()
    {
        var beats = Beats(40);
        var events = Events(("", ""), ("intro", "p1"), ("", "p2"), ("verse", ""), ("", "p3"));
        var cursor = new PlaybackMarkerCursor();

        Assert.Equal(("", ""), cursor.Resolve(events, beats, 500, 0));        // after event 0, which names nothing
        Assert.Equal(("intro", "p1"), cursor.Resolve(events, beats, 1_000, 0));
        Assert.Equal(("intro", "p2"), cursor.Resolve(events, beats, 2_500, 0));
        Assert.Equal(("verse", "p2"), cursor.Resolve(events, beats, 3_000, 0));
        Assert.Equal(("verse", "p3"), cursor.Resolve(events, beats, 9_000, 0));
        // A seek back re-finds its place.
        Assert.Equal(("intro", "p1"), cursor.Resolve(events, beats, 1_200, 0));
        Assert.Equal(("", ""), cursor.Resolve(new ObservableCollection<EventPoint>(), beats, 1_200, 0));
    }

    [Fact]
    public void BeforeTheFirstEvent_NothingIsInForce()
    {
        var beats = Beats(10);
        var events = new ObservableCollection<EventPoint>
        {
            new() { Position = new FractionalPosition(2), Section = "intro", Phrase = "p1" }
        };
        var cursor = new PlaybackMarkerCursor();

        Assert.Equal(("", ""), cursor.Resolve(events, beats, 500, 0));
        Assert.Equal(("intro", "p1"), cursor.Resolve(events, beats, 1_000, 0));
    }

    [Fact]
    public void APhrasesOnlyChart_IsWalkedOncePerEvent_NotOncePerPublish()
    {
        const int eventCount = 200;
        var beats = Beats(2 * eventCount + 2);
        var markers = new (string, string)[eventCount];
        for (var i = 0; i < eventCount; i++) markers[i] = ("", "p" + i);
        var events = Events(markers);
        var cursor = new PlaybackMarkerCursor();

        // Ten seconds of 60 Hz publishes from 100 s on, crossing ten events, all inside the
        // staleness bound. The old walk went back to the start of the song 600 times.
        for (var publish = 0; publish < 600; publish++)
        {
            var timeMs = 100_000 + publish * 1000.0 / 60;
            var (section, phrase) = cursor.Resolve(events, beats, timeMs, 0);
            Assert.Equal("", section);
            Assert.Equal("p" + (int)(timeMs / 1000), phrase);
        }

        Assert.Equal(10, cursor.ScanCount);
    }

    [Fact]
    public void ARename_ShowsAtOnce_AndAnInPlaceSection_WithinTheStalenessBound()
    {
        var beats = Beats(20);
        var events = Events(("intro", "p1"), ("", "p2"), ("", "p3"));
        var cursor = new PlaybackMarkerCursor();
        Assert.Equal(("intro", "p3"), cursor.Resolve(events, beats, 4_500, 1_000));

        events[0].Section = "opening";
        Assert.Equal(("opening", "p3"), cursor.Resolve(events, beats, 4_600, 1_001));

        events[1].Section = "verse";   // in place: nothing is raised
        Assert.Equal(("opening", "p3"), cursor.Resolve(events, beats, 4_700, 1_000 + PlaybackMarkerCursor.StaleAfterMs - 1));
        Assert.Equal(("verse", "p3"), cursor.Resolve(events, beats, 4_800, 1_000 + PlaybackMarkerCursor.StaleAfterMs));
    }

    [Fact]
    public void TheCoordinator_ResolvesThroughTheCursor_AndWalksNothingItself()
    {
        var src = SourceText.StripCommentLines(File.ReadAllText(Path.Combine(TestPaths.RepoRoot(),
            "DotCharter.Services", "Audio", "Coordination", "AudioPlaybackCoordinator.cs")));
        var start = src.IndexOf("private void UpdatePlaybackContext(double timeMs)", StringComparison.Ordinal);
        Assert.True(start >= 0, "UpdatePlaybackContext has moved — repoint this lint.");
        var body = src[start..src.IndexOf("var lyrics = _editor.LyricPhrases;", start, StringComparison.Ordinal)];

        Assert.Contains("_markers.Resolve(_editor.EventPoints, _editor.Beats, timeMs, Environment.TickCount64)", body);
        Assert.DoesNotContain("for (", body);
        Assert.DoesNotContain("while (", body);
        Assert.DoesNotContain("_sectionScanIndex", src);
        Assert.DoesNotContain("_phraseScanIndex", src);
    }
}
