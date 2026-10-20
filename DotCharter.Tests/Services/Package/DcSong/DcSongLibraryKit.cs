using DotCharter.Core.Domain.Vocals;
using DotCharter.Services.Package.DcSong;
using DotCharter.Services.Library.Cache;
using static DotCharter.Tests.Services.Package.DcSong.DcSongTestKit;

namespace DotCharter.Tests.Services.Package.DcSong;

/// <summary>
///     Synthetic <c>.dcsong</c> packages laid out as the writer lays them out, with a chart that
///     carries what the Library and Play Mode read from one (#708): a Lead in drop D with a capo,
///     a four-string Bass, lyrics, sections and a beat grid. No ffmpeg: the audio and stems are
///     seeded bytes and the decoder is <see cref="FakeDecoder" />.
/// </summary>
internal static class DcSongLibraryKit
{
    public static readonly string[] Sections = ["intro", "verse", "chorus"];

    public static ChartProject Song(string title, string artist)
    {
        var project = new ChartProject { Bpm = 120 };
        project.Chart.SongName = title;
        project.Chart.Artist = artist;
        project.Chart.AlbumName = "Around the Fur";
        project.Chart.AlbumYear = "1997";
        project.Chart.Charter = "Alex";
        for (var i = 0; i < 64; i++)
            project.Beats.Add(new BeatMarker { Time = i * 500.0, Bpm = 120, IsMeasure = i % 4 == 0 });

        var lead = new Arrangement { Name = "Lead", ArrangementType = ArrangementType.Lead, Capo = 2 };
        foreach (var step in new[] { -2, 0, 0, 0, 0, 0 }) lead.TuningPitches.Add(new TuningOffset(step));
        var leadLevel = new ChartLevel { DifficultyIndex = 0 };
        for (var beat = 0; beat < 12; beat++)
            leadLevel.Notes.Add(new GuitarNote { Position = new FractionalPosition(beat * 4), StringIndex = 2, Fret = 5 + (beat % 3) });
        lead.Levels.Add(leadLevel);
        for (var i = 0; i < Sections.Length; i++)
            lead.EventPoints.Add(new EventPoint { Position = new FractionalPosition(i * 16), Section = Sections[i], Phrase = Sections[i] });
        project.Arrangements.Add(lead);

        var bass = new Arrangement { Name = "Bass", ArrangementType = ArrangementType.Bass, StringCount = 4, Tuning = ["E", "A", "D", "G"] };
        foreach (var _ in Enumerable.Range(0, 4)) bass.TuningPitches.Add(new TuningOffset(0));
        var bassLevel = new ChartLevel { DifficultyIndex = 0 };
        for (var beat = 0; beat < 7; beat++)
            bassLevel.Notes.Add(new GuitarNote { Position = new FractionalPosition(beat * 8), StringIndex = 0, Fret = 3 });
        bass.Levels.Add(bassLevel);
        project.Arrangements.Add(bass);

        var phrase = new LyricPhrase();
        phrase.Lyrics.Add(new LyricEvent { Position = new FractionalPosition(4), Text = "Hey" });
        project.VocalTracks.Add(new VocalTrack { Phrases = [phrase] });
        return project;
    }

    /// <summary>
    ///     Writes <paramref name="project" /> as a package at <paramref name="path" /> (FLAC or
    ///     "Opus" audio, optionally a cover and a drums stem the chart declares with mixer state).
    /// </summary>
    public static string Write(
        string path, ChartProject project, string key, bool lossless, bool cover = false, bool stems = false, byte[]? audio = null)
    {
        var art = cover ? CoverPng : [];
        var codec = lossless ? DcSongAudioCodec.Flac : DcSongAudioCodec.Opus;
        var audioPath = DcSongManifestJson.AudioPath(codec);
        var song = audio ?? PackagedSong;
        if (stems) project.Stems.Add(new Stem { Name = "drums", Volume = 0.25, Muted = true });
        var packaged = DcSongChart.ForPackage(project, audioPath, cover ? "images/cover.png" : null,
            stems ? [("drums", DcSongManifestJson.StemPath("drums"))] : []);

        var dir = Path.GetDirectoryName(path)!;
        var members = new List<Member> { new("chart.rscp", ChartBytes(dir, packaged), Deflate: true), new(audioPath, song) };
        var files = new List<DcSongFileEntry>
        {
            new("chart.rscp", DcSongFileRole.Chart, members[0].Bytes.Length, Sha(members[0].Bytes)),
            new(audioPath, DcSongFileRole.Audio, song.Length, Sha(song))
        };
        if (cover)
        {
            members.Add(new Member("images/cover.png", art));
            files.Add(new DcSongFileEntry("images/cover.png", DcSongFileRole.Cover, art.Length, Sha(art), MediaType: DcSongImages.Png));
        }

        if (stems)
        {
            var stemPath = DcSongManifestJson.StemPath("drums");
            members.Add(new Member(stemPath, PackagedDrums));
            files.Add(new DcSongFileEntry(stemPath, DcSongFileRole.Stem, PackagedDrums.Length, Sha(PackagedDrums), StemName: "drums"));
        }

        var chart = project.Chart;
        var manifest = new DcSongManifest(1, 0, "DotCharter test",
            new DcSongInfo(key, project.ProjectId, chart.SongName, chart.Artist, chart.AlbumName, chart.AlbumYear, chart.Charter,
                181_234.5, true, []),
            new DcSongAudioInfo(codec, lossless, lossless ? null : 192, 0, 0),
            files);
        BuildPackage(path, members, files, DcSongManifestJson.Write(manifest));
        return path;
    }

    /// <summary>A real 4x4 PNG (the album-art cache keeps only an image Skia can decode).</summary>
    public static readonly byte[] CoverPng = EncodeCover();

    private static byte[] EncodeCover()
    {
        using var bitmap = new SkiaSharp.SKBitmap(4, 4);
        bitmap.Erase(SkiaSharp.SKColors.OrangeRed);
        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>A library source over a cache in <paramref name="cacheRoot" />, never the user's.</summary>
    public static DcSongLibrarySource Source(string cacheRoot, FakeDecoder decoder, IAppLogger? logger = null) =>
        new(logger ?? new FakeAppLogger(), new LibraryAudioCacheStore(rootOverride: cacheRoot), () => decoder);
}
