using DotCharter.Core.Domain.Vocals;
using DotCharter.Services.Parsing.UltraStar;

namespace DotCharter.Tests.Services.Parsing.UltraStar;

public sealed class UltraStarVocalConverterTests
{
    // 120 BPM constant grid (500 ms/beat) matching an UltraStar #BPM:120 file,
    // whose file-beat is a quarter beat => 125 ms.
    private static List<BeatMarker> Beats(int count = 40, double bpm = 120)
    {
        var ms = 60000.0 / bpm;
        return [.. Enumerable.Range(0, count).Select(i => new BeatMarker { Time = i * ms, Bpm = bpm, IsMeasure = i % 4 == 0 })];
    }

    [Fact]
    public void ToVocalTrack_MapsTimingPitchAndWordParts()
    {
        var beats = Beats();
        var song = new UltraStarLyricsParser().Parse("""
            #BPM:120
            #GAP:0
            : 0 4 0 Hel
            : 4 4 2 lo
            - 8
            : 8 4 4 world
            E
            """);

        var track = UltraStarVocalConverter.ToVocalTrack(song, beats);

        Assert.Equal(2, track.Phrases.Count);
        var line1 = track.Phrases[0].Lyrics;
        Assert.Equal(2, line1.Count);

        // file pitch 0 -> MIDI 60; pitch 2 -> 62.
        Assert.Equal(60, line1[0].Note);
        Assert.Equal(62, line1[1].Note);

        // "Hel" (no trailing space, not last) joins the next syllable.
        Assert.True(line1[0].IsWordPart);
        Assert.False(line1[1].IsWordPart);
        Assert.True(line1[1].IsPhraseEnd);

        // file beat 4 at 120bpm => 4 * 125ms = 500ms.
        Assert.Equal(500, TimelineMath.CalculateTimeMs(line1[1].Position, beats), 1);

        Assert.Equal(64, track.Phrases[1].Lyrics[0].Note);
    }

    [Fact]
    public void ToSong_BuildsNotesBreaksHeadersAndPitch()
    {
        var beats = Beats();
        var track = new VocalTrack();
        var phrase1 = new LyricPhrase();
        phrase1.Lyrics.Add(new LyricEvent { Position = new FractionalPosition(1), EndPosition = new FractionalPosition(2), Text = "Hel", Note = 60, IsWordPart = true });
        phrase1.Lyrics.Add(new LyricEvent { Position = new FractionalPosition(2), EndPosition = new FractionalPosition(3), Text = "lo", Note = 62, IsPhraseEnd = true });
        var phrase2 = new LyricPhrase();
        phrase2.Lyrics.Add(new LyricEvent { Position = new FractionalPosition(4), EndPosition = new FractionalPosition(5), Text = "world", Note = 64, IsPhraseEnd = true });
        track.Phrases.Add(phrase1);
        track.Phrases.Add(phrase2);

        var song = UltraStarVocalConverter.ToSong(track, beats, bpm: 120, gapMs: 0, title: "T", artist: "A");

        Assert.Equal("T", song.GetHeader("TITLE"));
        Assert.Equal("A", song.GetHeader("ARTIST"));
        Assert.Equal(120, song.Bpm);
        Assert.Equal(2, song.Lines.Count);

        // beat 1 (500ms) / 125ms = file beat 4.
        Assert.Equal(4, song.Lines[0].Notes[0].StartBeat);
        Assert.Equal(0, song.Lines[0].Notes[0].Pitch); // MIDI 60 -> file 0
        Assert.Equal("Hel", song.Lines[0].Notes[0].Text); // word part -> no trailing space
        Assert.Equal("lo ", song.Lines[0].Notes[1].Text); // word end -> trailing space
        Assert.Equal(16, song.Lines[0].BreakBeat); // next line first note at beat 4 (2000ms) => file 16
    }

    [Fact]
    public void DomainRoundTrip_PreservesBeatsPitchAndText()
    {
        var beats = Beats();
        var original = new UltraStarLyricsParser().Parse("""
            #BPM:120
            #GAP:0
            : 0 4 0 Hel
            : 4 4 2 lo
            - 8
            : 8 8 4 world
            E
            """);

        var track = UltraStarVocalConverter.ToVocalTrack(original, beats);
        var round = UltraStarVocalConverter.ToSong(track, beats, bpm: 120, gapMs: 0);

        Assert.Equal(original.Lines.Count, round.Lines.Count);
        for (var li = 0; li < original.Lines.Count; li++)
        {
            var oNotes = original.Lines[li].Notes;
            var rNotes = round.Lines[li].Notes;
            Assert.Equal(oNotes.Count, rNotes.Count);
            for (var ni = 0; ni < oNotes.Count; ni++)
            {
                Assert.Equal(oNotes[ni].StartBeat, rNotes[ni].StartBeat);
                Assert.Equal(oNotes[ni].LengthBeats, rNotes[ni].LengthBeats);
                Assert.Equal(oNotes[ni].Pitch, rNotes[ni].Pitch);
                Assert.Equal(oNotes[ni].Text.Trim(), rNotes[ni].Text.Trim());
            }
        }
    }

    [Fact]
    public void ToVocalTrack_EmptyBeats_DoesNotThrow()
    {
        var song = new UltraStarLyricsParser().Parse(": 0 4 0 x\nE");
        var track = UltraStarVocalConverter.ToVocalTrack(song, []);
        Assert.Single(track.Phrases);
    }

    // ─── #RELATIVE:YES and duets (#332) ──────────────────────────────────────

    [Fact]
    public void ToVocalTracks_RelativeSong_PutsEachLineAfterThePreviousBreak()
    {
        var beats = Beats();
        // Relative mode: "- 8 12" moves the next line's zero to file beat 12.
        var song = new UltraStarLyricsParser().Parse("""
            #TITLE:Relative
            #BPM:120
            #GAP:1000
            #RELATIVE:YES
            : 0 4 0 One
            : 4 4 2 two
            - 8 12
            : 0 4 4 Three
            : 4 4 5 four
            E
            """);

        var track = Assert.Single(UltraStarVocalConverter.ToVocalTracks(song, beats));

        Assert.Equal(2, track.Phrases.Count);
        // GAP 1000 ms + file beat 12 * 125 ms = 2500 ms, not back at the song start.
        Assert.Equal(2500, TimelineMath.CalculateTimeMs(track.Phrases[1].Position, beats), 1);
        Assert.Equal(3000, TimelineMath.CalculateTimeMs(track.Phrases[1].Lyrics[1].Position, beats), 1);
        Assert.True(track.Phrases[1].Position > track.Phrases[0].EndPosition);
    }

    private const string DuetSong = """
        #TITLE:Duet
        #BPM:120
        #GAP:0
        #DUETSINGERP1:Anna
        #DUETSINGERP2:Ben
        P1
        : 0 4 0 Hel
        : 4 4 0 lo
        - 10
        : 16 4 0 there
        P2
        : 2 4 5 Hi
        - 8
        : 18 4 5 you
        E
        """;

    [Fact]
    public void ToVocalTracks_Duet_GivesEachSingerTheirOwnTrack()
    {
        var beats = Beats();
        var tracks = UltraStarVocalConverter.ToVocalTracks(new UltraStarLyricsParser().Parse(DuetSong), beats);

        Assert.Equal(2, tracks.Count);
        Assert.Equal(["Anna", "Ben"], tracks.Select(t => t.Name));
        Assert.Equal(["Hel", "lo", "there"], tracks[0].Phrases.SelectMany(p => p.Lyrics).Select(l => l.Text));
        Assert.Equal(["Hi", "you"], tracks[1].Phrases.SelectMany(p => p.Lyrics).Select(l => l.Text));

        // Within each track the phrases run one after another; the overlap is
        // only BETWEEN the singers (Ben's "Hi" starts inside Anna's first line).
        foreach (var track in tracks)
        {
            for (var i = 1; i < track.Phrases.Count; i++)
                Assert.True(track.Phrases[i].Position >= track.Phrases[i - 1].EndPosition);
        }
    }

    [Fact]
    public void ToVocalTrack_Duet_IsTheFirstSingerOnly_NeverAMerge()
    {
        var track = UltraStarVocalConverter.ToVocalTrack(new UltraStarLyricsParser().Parse(DuetSong), Beats());

        Assert.Equal(["Hel", "lo", "there"], track.Phrases.SelectMany(p => p.Lyrics).Select(l => l.Text));
    }

    [Fact]
    public void ToVocalTracks_DuetWithoutSingerNames_NamesTheTracksByVoice()
    {
        var song = new UltraStarLyricsParser().Parse("#BPM:120\nP1\n: 0 4 0 a\nP2\n: 2 4 0 b\nE");

        var tracks = UltraStarVocalConverter.ToVocalTracks(song, Beats(), "Vocals");

        Assert.Equal(["Vocals P1", "Vocals P2"], tracks.Select(t => t.Name));
    }
}
