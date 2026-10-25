using DotCharter.Core.Domain.Vocals;
using DotCharter.Services.Parsing.Eof;

namespace DotCharter.Tests.Services.Parsing.Eof;

/// <summary>
///     #232: an <c>.eof</c> import brought no lyrics — the vocal track was walked
///     over for stream alignment and dropped. It now becomes the project's vocal
///     track: syllables, positions, lengths and pitches as written, the trailing
///     <c>-</c> as <see cref="LyricEvent.IsWordPart" />, and each lyric LINE
///     (section type 5) as one <see cref="LyricPhrase" />.
/// </summary>
public sealed class EofVocalImportTests
{
    private static readonly List<BeatMarker> Beats = [.. Enumerable.Range(0, 64).Select(i => new BeatMarker { Time = i * 500.0 })];

    private static EofImportResult ExportAndImport(ChartProject project, Func<EofTrackInfo, bool>? pick = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcharter-eof-vocals-{Guid.NewGuid():N}.eof");
        File.WriteAllBytes(path, EofExporter.Export(project).Bytes);
        try
        {
            var importer = new EofImporter();
            var ids = importer.GetAvailableTracks(path).Where(pick ?? (_ => true)).Select(t => t.Id).ToList();
            return importer.ImportTracksWithMetadata(path, ids);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static List<EofTrackInfo> ListTracks(ChartProject project)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcharter-eof-vocals-{Guid.NewGuid():N}.eof");
        File.WriteAllBytes(path, EofExporter.Export(project).Bytes);
        try
        {
            return new EofImporter().GetAvailableTracks(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static double Ms(FractionalPosition position, IList<BeatMarker> beats) => position.GetPositionMs(beats);

    /// <summary>The exporter writes PART VOCALS; reading it back gives the same lyrics.</summary>
    [Fact]
    public void A_round_trip_keeps_every_syllable_its_timing_pitch_and_word_join()
    {
        var project = EofExportTestCharts.BuildRepresentativeProject();
        var original = project.VocalTracks.Single();

        var result = ExportAndImport(project);

        var track = Assert.Single(result.VocalTracks);
        var beats = EofImporter.BuildBeatMarkers(result.Metadata);
        var phrase = Assert.Single(track.Phrases);
        var source = original.Phrases.Single();
        Assert.Equal(Ms(source.Position, project.Beats), Ms(phrase.Position, beats), 1);
        Assert.Equal(Ms(source.EndPosition, project.Beats), Ms(phrase.EndPosition, beats), 1);
        Assert.Equal(source.Lyrics.Count, phrase.Lyrics.Count);
        for (var i = 0; i < source.Lyrics.Count; i++)
        {
            var want = source.Lyrics[i];
            var got = phrase.Lyrics[i];
            Assert.Equal(want.Text, got.Text);
            Assert.Equal(want.IsWordPart, got.IsWordPart);
            Assert.Equal(want.Note, got.Note);
            Assert.Equal(Ms(want.Position, project.Beats), Ms(got.Position, beats), 1);
            Assert.Equal(Ms(want.EndPosition, project.Beats), Ms(got.EndPosition, beats), 1);
        }

        Assert.Equal([false, true], phrase.Lyrics.Select(l => l.IsPhraseEnd));
    }

    /// <summary>Two lines stay two phrases, and an unpitched syllable stays unpitched.</summary>
    [Fact]
    public void Each_lyric_line_becomes_its_own_phrase()
    {
        var project = EofExportTestCharts.BuildRepresentativeProject();
        var second = new LyricPhrase { Position = EofExportTestCharts.Pos(10), EndPosition = EofExportTestCharts.Pos(14) };
        second.Lyrics.Add(new LyricEvent { Position = EofExportTestCharts.Pos(10), EndPosition = EofExportTestCharts.Pos(11), Text = "world", Note = 255 });
        second.Lyrics.Add(new LyricEvent { Position = EofExportTestCharts.Pos(12), EndPosition = EofExportTestCharts.Pos(13), Text = "again", Note = 64 });
        project.VocalTracks[0].Phrases.Add(second);

        var track = Assert.Single(ExportAndImport(project).VocalTracks);

        Assert.Equal(2, track.Phrases.Count);
        Assert.Equal(["Hel", "lo"], track.Phrases[0].Lyrics.Select(l => l.Text));
        Assert.Equal(["world", "again"], track.Phrases[1].Lyrics.Select(l => l.Text));
        Assert.Equal(255, track.Phrases[1].Lyrics[0].Note);
        Assert.True(track.Phrases[1].Lyrics[^1].IsPhraseEnd);
    }

    /// <summary>The dialog offers PART VOCALS once it holds a lyric, and only then.</summary>
    [Fact]
    public void The_track_list_offers_the_vocal_track_only_when_it_has_lyrics()
    {
        var withLyrics = ListTracks(EofExportTestCharts.BuildRepresentativeProject());
        var vocals = Assert.Single(withLyrics, t => t.IsVocals);
        Assert.Equal(2, vocals.NoteCount);

        var project = EofExportTestCharts.BuildRepresentativeProject();
        project.VocalTracks.Clear();
        Assert.DoesNotContain(ListTracks(project), t => t.IsVocals);
    }

    /// <summary>An unticked vocal track imports no lyrics, and the arrangements are unchanged by it.</summary>
    [Fact]
    public void An_unticked_vocal_track_imports_no_lyrics()
    {
        var project = EofExportTestCharts.BuildRepresentativeProject();

        var all = ExportAndImport(project);
        var guitarOnly = ExportAndImport(project, t => !t.IsVocals);

        Assert.Empty(guitarOnly.VocalTracks);
        Assert.Equal(all.Arrangements.Select(a => a.Name), guitarOnly.Arrangements.Select(a => a.Name));
    }

    // ── a hand-written vocal body: what EOF writes that our exporter does not ──

    private static List<VocalTrack> Parse(
        IEnumerable<(string Text, byte Set, byte Pitch, uint Pos, uint Len)> lyrics,
        IEnumerable<(uint Start, uint End)> lines)
    {
        var w = new EofPackWriter();
        var list = lyrics.ToList();
        w.WriteU8(0); // tone set
        w.WriteU32((uint)list.Count);
        foreach (var (text, set, pitch, pos, len) in list)
        {
            w.WriteString(text);
            w.WriteU8(set);
            w.WriteU8(pitch);
            w.WriteU32(pos);
            w.WriteU32(len);
            w.WriteU32(0);
        }

        var lineList = lines.ToList();
        // A star-power section first: only type 5 is a lyric line.
        w.WriteU16((ushort)(lineList.Count > 0 ? 2 : 1));
        w.WriteU16(7);
        w.WriteU32(1);
        w.WriteString(null);
        w.WriteU8(0);
        w.WriteU32(0);
        w.WriteU32(30000);
        w.WriteU32(0);
        if (lineList.Count > 0)
        {
            w.WriteU16(5);
            w.WriteU32((uint)lineList.Count);
            foreach (var (start, end) in lineList)
            {
                w.WriteString(null);
                w.WriteU8(0);
                w.WriteU32(start);
                w.WriteU32(end);
                w.WriteU32(0);
            }
        }

        w.WriteU32(0); // custom data blocks
        var reader = new EofPackReader(w.ToArray());
        var tracks = EofVocalTrackParser.Parse(reader, Beats);
        Assert.Equal(0, reader.Remaining);
        return tracks;
    }

    /// <summary>A lyric that starts outside every line still imports, in a phrase of its own.</summary>
    [Fact]
    public void A_lyric_outside_every_line_is_kept()
    {
        var track = Assert.Single(Parse(
            [("one", 0, 60, 1000, 400), ("two", 0, 62, 1500, 400), ("stray", 0, 0, 4000, 300)],
            [(1000, 2000)]));

        Assert.Equal(2, track.Phrases.Count);
        Assert.Equal(["one", "two"], track.Phrases[0].Lyrics.Select(l => l.Text));
        Assert.Equal("stray", Assert.Single(track.Phrases[1].Lyrics).Text);
        Assert.Equal(255, track.Phrases[1].Lyrics[0].Note);
        Assert.Equal(4300, Ms(track.Phrases[1].EndPosition, Beats), 1);
    }

    /// <summary>A file saved without lines falls back to the RS rule: a "+" ends the phrase.</summary>
    [Fact]
    public void Without_lines_a_plus_or_a_long_silence_ends_the_phrase()
    {
        var track = Assert.Single(Parse(
            [("Hel-", 0, 60, 1000, 400), ("lo+", 0, 62, 1500, 400), ("next", 0, 64, 2500, 400), ("far", 0, 64, 9000, 400)],
            []));

        Assert.Equal(3, track.Phrases.Count);
        Assert.Equal(["Hel", "lo"], track.Phrases[0].Lyrics.Select(l => l.Text));
        Assert.True(track.Phrases[0].Lyrics[0].IsWordPart);
        Assert.True(track.Phrases[0].Lyrics[1].IsPhraseEnd);
        Assert.Equal("next", Assert.Single(track.Phrases[1].Lyrics).Text);
        Assert.Equal("far", Assert.Single(track.Phrases[2].Lyrics).Text);
    }

    /// <summary>Inside a line only "-" is structure: a bare "+" is a pitch slide and stays a lyric.</summary>
    [Fact]
    public void A_bare_plus_inside_a_line_stays_a_lyric()
    {
        var phrase = Assert.Single(Assert.Single(Parse(
            [("ooh-", 0, 60, 1000, 400), ("+", 0, 62, 1400, 400)],
            [(1000, 2000)])).Phrases);

        Assert.Equal(["ooh", "+"], phrase.Lyrics.Select(l => l.Text));
        Assert.Equal([true, false], phrase.Lyrics.Select(l => l.IsWordPart));
    }

    /// <summary>A second lyric set (a harmony) is its own track, not mixed into the lead.</summary>
    [Fact]
    public void Each_lyric_set_is_its_own_track()
    {
        var tracks = Parse(
            [("lead", 0, 60, 1000, 400), ("harm", 1, 64, 1000, 400)],
            [(1000, 2000)]);

        Assert.Equal(["Vocals", "Vocals (set 1)"], tracks.Select(t => t.Name));
        Assert.Equal("lead", tracks[0].Phrases.Single().Lyrics.Single().Text);
        Assert.Equal("harm", tracks[1].Phrases.Single().Lyrics.Single().Text);
    }

    [Fact]
    public void A_corrupt_lyric_count_is_refused_before_anything_is_read()
    {
        var w = new EofPackWriter();
        w.WriteU8(0);
        w.WriteU32(1_000_000);

        Assert.Throws<InvalidDataException>(() => EofVocalTrackParser.Parse(new EofPackReader(w.ToArray()), Beats));
    }
}
