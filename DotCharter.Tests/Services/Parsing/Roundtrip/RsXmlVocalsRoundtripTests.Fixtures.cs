using System.Xml.Linq;
using DotCharter.Core.Domain.Vocals;
using DotCharter.Services.Parsing.Xml.Vocals;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Parsing.Roundtrip;

public sealed partial class RsXmlVocalsRoundtripTests : IDisposable
{
    private readonly string _tempDir;

    public RsXmlVocalsRoundtripTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DotCharterVocalsTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() => BestEffortDelete.Directory(_tempDir);

    private string TempPath(string name) => Path.Combine(_tempDir, name);

    private static ChartProject BuildVocalsProject()
    {
        var project = new ChartProject();
        project.Chart.SongName = "Test Song";

        // 16 beats at 120 BPM
        for (var i = 0; i < 16; i++)
            project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120, IsMeasure = i % 4 == 0 });

        var track = new VocalTrack { Name = "Vocals" };
        var phrase = new LyricPhrase
        {
            Position = FractionalPosition.Zero,
            EndPosition = new FractionalPosition(8)
        };

        phrase.Lyrics.Add(new LyricEvent
        {
            Position = FractionalPosition.Zero,
            EndPosition = new FractionalPosition(2),
            Text = "Hello",
            Note = 64
        });
        phrase.Lyrics.Add(new LyricEvent
        {
            Position = new FractionalPosition(2),
            EndPosition = new FractionalPosition(4),
            Text = "World",
            Note = 67
        });
        phrase.Lyrics.Add(new LyricEvent
        {
            Position = new FractionalPosition(4),
            EndPosition = new FractionalPosition(6),
            Text = "word",
            Note = 60,
            IsWordPart = true
        });

        track.Phrases.Add(phrase);
        project.VocalTracks.Add(track);
        return project;
    }

    // ─── RS encodes vocal structure inside the lyric string ───────────────────
    // A trailing "-" joins the syllable to the next one; a trailing "+" ends the
    // phrase and IS the in-game line break. Neither was written, so an exported
    // song showed one continuous run of separated syllables.

    private static ChartProject BuildTwoPhraseProject()
    {
        var project = new ChartProject();
        for (var i = 0; i < 16; i++)
            project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120, IsMeasure = i % 4 == 0 });

        var track = new VocalTrack { Name = "Vocals" };

        var first = new LyricPhrase { Position = FractionalPosition.Zero, EndPosition = new FractionalPosition(4) };
        first.Lyrics.Add(new LyricEvent
        {
            Position = FractionalPosition.Zero, EndPosition = new FractionalPosition(1),
            Text = "hel", Note = 60, IsWordPart = true
        });
        first.Lyrics.Add(new LyricEvent
        {
            Position = new FractionalPosition(1), EndPosition = new FractionalPosition(2),
            Text = "lo", Note = 62
        });

        var second = new LyricPhrase
        {
            Position = new FractionalPosition(4), EndPosition = new FractionalPosition(8)
        };
        second.Lyrics.Add(new LyricEvent
        {
            Position = new FractionalPosition(4), EndPosition = new FractionalPosition(5),
            Text = "there", Note = 64
        });

        track.Phrases.Add(first);
        track.Phrases.Add(second);
        project.VocalTracks.Add(track);
        return project;
    }

    private static List<string> WrittenLyrics(ChartProject project)
    {
        return new RsXmlVocalsWriter().Write(project).Root!
            .Elements("vocal")
            .Select(v => v.Attribute("lyric")!.Value)
            .ToList();
    }

    // ─── Reading: the marker is syntax, never stored text (#324) ──────────────
    // `-` joins the next syllable, `+` ends the line. The XML reader used to keep
    // both in the text and put the whole song in one phrase, while the psarc path
    // stripped them and split lines.

    private const string MarkedVocalsXml = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <vocals count="5">
          <vocal time="1.000" note="60" length="0.250" lyric="Hel-"/>
          <vocal time="1.500" note="62" length="0.250" lyric="lo"/>
          <vocal time="2.000" note="64" length="0.750" lyric="world+"/>
          <vocal time="4.000" note="65" length="0.250" lyric="Next"/>
          <vocal time="4.500" note="67" length="0.750" lyric="line+"/>
        </vocals>
        """;

    private static List<BeatMarker> Beats120()
        => [.. Enumerable.Range(0, 16).Select(i => new BeatMarker { Time = i * 500, Bpm = 120, IsMeasure = i % 4 == 0 })];

    private static VocalTrack ReadMarked(IList<BeatMarker> beats)
        => new RsXmlVocalsReader().ReadDocument(XDocument.Parse(MarkedVocalsXml), "vocals.xml", beats).VocalTracks.Single();

    private static List<string> Describe(VocalTrack track)
    {
        var lines = new List<string>();
        foreach (var phrase in track.Phrases)
        {
            lines.Add($"phrase {phrase.Position}..{phrase.EndPosition}");
            lines.AddRange(phrase.Lyrics.Select(l =>
                $"  '{l.Text}' {l.Position}..{l.EndPosition} note={l.Note} word={l.IsWordPart} end={l.IsPhraseEnd}"));
        }

        return lines;
    }
}
