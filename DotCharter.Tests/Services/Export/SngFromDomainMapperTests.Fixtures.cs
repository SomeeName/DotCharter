using DotCharter.Core.Domain.Vocals;

namespace DotCharter.Tests.Services.Export;

public sealed partial class SngFromDomainMapperTests
{
    private const double Tolerance = 2.0; // ms — float-second storage granularity

    // ---- helpers -----------------------------------------------------------

    private static List<BeatMarker> CreateBeats(int count = 64, double bpm = 120.0)
    {
        // 4/4 at 120 BPM: a beat every 500 ms.
        var beats = new List<BeatMarker>(count);
        for (var i = 0; i < count; i++)
        {
            beats.Add(new BeatMarker
            {
                Time = i * 500.0,
                IsMeasure = i % 4 == 0,
                Bpm = bpm,
                BeatsInMeasure = 4,
                NoteDenominator = 4
            });
        }

        return beats;
    }

    private static FractionalPosition Pos(IList<BeatMarker> beats, double ms) => TimelineMath.PositionFromMs(ms, beats);

    private static Arrangement CreateArrangement(IList<BeatMarker> beats)
    {
        var arrangement = new Arrangement
        {
            ArrangementType = ArrangementType.Lead,
            Name = "Lead",
            Capo = 0,
            StringCount = 6
        };
        for (var i = 0; i < 6; i++) arrangement.TuningPitches.Add(new TuningOffset(0));

        arrangement.Phrases.AddRange(["COUNT", "riff", "END"]);
        arrangement.PhraseInfos["riff"] = new PhraseInfo(0, false);
        arrangement.PhraseIterations.Add(new PhraseIteration(Pos(beats, 0), 0));
        arrangement.PhraseIterations.Add(new PhraseIteration(Pos(beats, 2000), 1));
        arrangement.PhraseIterations.Add(new PhraseIteration(Pos(beats, 28000), 2));

        arrangement.EventPoints.Add(new EventPoint { Position = Pos(beats, 2000), Section = "riff" });
        arrangement.EventPoints.Add(new EventPoint { Position = Pos(beats, 20000), Section = "riff" });
        arrangement.EventPoints.Add(new EventPoint { Position = Pos(beats, 28000), Section = "noguitar" });

        arrangement.ToneA = "clean";
        arrangement.ToneB = "lead";
        arrangement.ToneBase = "clean";
        arrangement.ToneChanges.Add(new ToneChange { Position = Pos(beats, 2000), ToneName = "clean" });
        arrangement.ToneChanges.Add(new ToneChange { Position = Pos(beats, 10000), ToneName = "lead" });

        var template = new ChordTemplate { Id = 0, Name = "A5" };
        template.Frets[1] = 0;
        template.Frets[2] = 2;
        template.Fingers[2] = 1;
        arrangement.ChordTemplates.Add(template);

        var level = new ChartLevel { DifficultyIndex = 0 };

        level.Anchors.Add(new FretAnchor(Pos(beats, 2000), 1, 4));
        level.Anchors.Add(new FretAnchor(Pos(beats, 10000), 5, 4));

        // Plain note.
        level.Notes.Add(new GuitarNote
        {
            Position = Pos(beats, 2000),
            StringIndex = 0,
            Fret = 3,
            Sustain = Pos(beats, 500) // 500 ms sustain
        });
        // Technique-heavy note.
        level.Notes.Add(new GuitarNote
        {
            Position = Pos(beats, 3000),
            StringIndex = 2,
            Fret = 7,
            Accent = true,
            Mute = MuteType.Palm,
            Hopo = HopoType.HammerOn,
            Vibrato = true,
            VibratoStrength = 80,
            Tremolo = true
        });
        // Bend note.
        var bendNote = new GuitarNote
        {
            Position = Pos(beats, 4000),
            StringIndex = 1,
            Fret = 9,
            Sustain = Pos(beats, 1000)
        };
        bendNote.BendValues.Add(new BendValue(Pos(beats, 4200), 1.0));
        bendNote.BendValues.Add(new BendValue(Pos(beats, 4800), 0.5));
        level.Notes.Add(bendNote);
        // Slide note (pitched).
        level.Notes.Add(new GuitarNote
        {
            Position = Pos(beats, 5000),
            StringIndex = 3,
            Fret = 5,
            SlideTo = 9,
            Sustain = Pos(beats, 500)
        });
        // Unpitched slide.
        level.Notes.Add(new GuitarNote
        {
            Position = Pos(beats, 6000),
            StringIndex = 3,
            Fret = 9,
            SlideTo = 12,
            UnpitchedSlide = true,
            Sustain = Pos(beats, 250)
        });
        // Link-next chain.
        level.Notes.Add(new GuitarNote
        {
            Position = Pos(beats, 7000),
            StringIndex = 4,
            Fret = 5,
            Sustain = Pos(beats, 500),
            LinkNext = true
        });
        level.Notes.Add(new GuitarNote
        {
            Position = Pos(beats, 7500),
            StringIndex = 4,
            Fret = 5,
            Sustain = Pos(beats, 250)
        });
        // Harmonic + ignore.
        level.Notes.Add(new GuitarNote
        {
            Position = Pos(beats, 8000),
            StringIndex = 5,
            Fret = 12,
            Harmonic = HarmonicType.Normal,
            Ignore = true
        });

        // Chord with chord notes.
        var chord = new Chord { Position = Pos(beats, 10000), TemplateId = 0, Accent = true };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 0, Sustain = Pos(beats, 500) });
        chord.ChordNotes.Add(new ChordNote
        {
            StringIndex = 2, Fret = 2, Sustain = Pos(beats, 500), Mute = MuteType.Palm
        });
        level.Chords.Add(chord);

        // Second identical-notes chord: chord notes must deduplicate.
        var chord2 = new Chord { Position = Pos(beats, 11000), TemplateId = 0 };
        chord2.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 0, Sustain = Pos(beats, 500) });
        chord2.ChordNotes.Add(new ChordNote
        {
            StringIndex = 2, Fret = 2, Sustain = Pos(beats, 500), Mute = MuteType.Palm
        });
        level.Chords.Add(chord2);

        level.HandShapes.Add(new HandShape
        {
            Position = Pos(beats, 10000),
            EndPosition = Pos(beats, 12000),
            TemplateId = 0,
            Name = "A5"
        });

        arrangement.Levels.Add(level);
        return arrangement;
    }

    private static double TimeMs(IList<BeatMarker> beats, FractionalPosition position) => position.GetPositionMs(beats);

    private const string PinnedInstrumentalBody = "d3d2743073fe559ce7708f20636dc6a293a538663805e507b4a298c9f22444eb";
    private const string PinnedVocalsBody = "ca5ae467166a5691cc45328efe548d0c025e9379450afd61b5d271994476e323";

    /// <summary>The two-syllable track of <c>Vocals_track_builds_a_valid_vocals_sng</c>.</summary>
    private static VocalTrack HelloVocals(List<BeatMarker> created, out List<BeatMarker> beats)
    {
        beats = created;
        var track = new VocalTrack { Name = "Vocals" };
        var phrase = new LyricPhrase { Position = Pos(beats, 4000) };
        phrase.Lyrics.Add(new LyricEvent
        {
            Position = Pos(beats, 4000),
            EndPosition = Pos(beats, 4400),
            Text = "hel",
            IsWordPart = true,
            Note = 60
        });
        phrase.Lyrics.Add(new LyricEvent
        {
            Position = Pos(beats, 4500),
            EndPosition = Pos(beats, 5000),
            Text = "lo ",
            IsPhraseEnd = true,
            Note = 62
        });
        track.Phrases.Add(phrase);
        return track;
    }

    /// <summary>The reference rule, read straight off the file: carry the last
    /// <c>measure</c> attribute forward over the beats that have none.</summary>
    private static List<short> ExpectedMeasureNumbers(string xmlPath)
    {
        var ebeats = System.Xml.Linq.XDocument.Load(xmlPath).Root?.Element("ebeats");
        if (ebeats == null) return [];

        var result = new List<short>();
        short current = 0;
        foreach (var ebeat in ebeats.Elements("ebeat"))
        {
            var attr = ebeat.Attribute("measure")?.Value;
            if (short.TryParse(attr, out var measure) && measure >= 0) current = measure;
            result.Add(current);
        }

        return result;
    }

    private static List<string> EofRsXmlCorpus()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "TestData", "EOF");
            if (Directory.Exists(candidate))
            {
                return Directory.EnumerateFiles(candidate, "*_RS2.xml", SearchOption.AllDirectories)
                    .Where(f => !f.Contains("debug", StringComparison.OrdinalIgnoreCase))
                    .Where(f => !f.Contains("VOCAL", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => f, StringComparer.Ordinal)
                    .ToList();
            }

            dir = dir.Parent;
        }

        return [];
    }

    private static string? FindEofPsarcFixture()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "TestData", "EOF");
            if (Directory.Exists(candidate))
            {
                return Directory
                    .EnumerateFiles(candidate, "*_p.psarc", SearchOption.AllDirectories)
                    .OrderBy(p => new FileInfo(p).Length)
                    .FirstOrDefault();
            }

            dir = dir.Parent;
        }

        return null;
    }
}
