using DotCharter.Core.Domain.Vocals;
using DotCharter.Tone.Audio.Dsp.Tones;

namespace DotCharter.Tests.Tools.ReadmeMedia;

/// <summary>One demo song: its words, its tempo and the four chords its verse and chorus walk through.</summary>
/// <param name="Title">Invented title.</param>
/// <param name="Artist">Invented artist.</param>
/// <param name="Album">Invented album.</param>
/// <param name="Year">Release year shown in the library.</param>
/// <param name="Genre">Genre shown in the library.</param>
/// <param name="Bpm">Tempo; the whole song is 20 bars of 4/4.</param>
/// <param name="Chords">Four chord names out of <see cref="ReadmeDemoSong.Shapes" />.</param>
/// <param name="SoloShift">Frets the E-minor-pentatonic solo is moved by, so it sits in the song's key.</param>
/// <param name="Hue">Cover colour, 0-360.</param>
/// <param name="WithSevenString">Adds the 7-string arrangement.</param>
internal sealed record ReadmeDemoVariant(
    string Title, string Artist, string Album, int Year, string Genre, double Bpm,
    string[] Chords, int SoloShift, int Hue, bool WithSevenString);

/// <summary>
///     The README demo content (issue #912): a short chart that is entirely ours - own title, own
///     words, own tune, a cover drawn in code and audio rendered by <see cref="ReadmeDemoAudio" />.
///     Nothing here comes from a real song. <see cref="Build" /> returns the project and the audio
///     events it was built from, so the notes on screen and the notes in the file are the same ones.
/// </summary>
internal sealed class ReadmeDemoSong
{
    public const int Bars = 20;

    public static readonly ReadmeDemoVariant Flagship = new(
        "DotCharter Demo", "Demo Band", "Demo Songs", 2026, "Rock", 110,
        ["Em", "C", "G", "D"], 0, 24, true);

    public static readonly IReadOnlyList<ReadmeDemoVariant> Extras =
    [
        new("Paper Lanterns", "The Sample Trio", "Open Source Sessions", 2025, "Indie", 96,
            ["Am", "F", "C", "G"], -7, 200, false),
        new("Static Bloom", "Example Parade", "Placeholder Hours", 2024, "Alternative", 124,
            ["Em", "Am", "D", "G"], 0, 285, false),
        new("Orbit Tides", "Test Pattern", "Signal and Noise", 2025, "Pop Rock", 104,
            ["C", "G", "Am", "F"], -7, 160, false),
        new("Low Tide Parade", "Nobody Special", "Demo Reel", 2023, "Folk", 88,
            ["Dm", "Am", "C", "G"], -2, 330, false)
    ];

    /// <summary>Chord shapes: fret per string (0 = low string) and the fretting finger (1-4).</summary>
    public static readonly IReadOnlyDictionary<string, (int[] Frets, (int String, int Finger)[] Fingers, int BassMidi)> Shapes =
        new Dictionary<string, (int[], (int, int)[], int)>
        {
            ["Em"] = ([0, 2, 2, 0, 0, 0], [(1, 2), (2, 3)], 28),
            ["C"] = ([-1, 3, 2, 0, 1, 0], [(1, 3), (2, 2), (4, 1)], 36),
            ["G"] = ([3, 2, 0, 0, 0, 3], [(0, 2), (1, 1), (5, 3)], 31),
            ["D"] = ([-1, -1, 0, 2, 3, 2], [(3, 1), (4, 3), (5, 2)], 38),
            ["Am"] = ([-1, 0, 2, 2, 1, 0], [(2, 2), (3, 3), (4, 1)], 33),
            ["F"] = ([1, 3, 3, 2, 1, 1], [(0, 1), (1, 3), (2, 4), (3, 2), (4, 1), (5, 1)], 29),
            ["Dm"] = ([-1, -1, 0, 2, 3, 1], [(3, 2), (4, 3), (5, 1)], 38)
        };

    /// <summary>Open-string MIDI notes of the guitar tuning (E standard).</summary>
    private static readonly int[] GuitarOpen = [40, 45, 50, 55, 59, 64];

    /// <summary>Open-string MIDI notes of the 7-string tuning (B E A D G B E).</summary>
    private static readonly int[] SevenOpen = [35, 40, 45, 50, 55, 59, 64];

    private static readonly string[] SectionNames = ["intro", "verse", "chorus", "solo", "outro"];

    private readonly ReadmeDemoVariant _variant;
    private readonly List<ReadmeDemoNote> _audio = [];
    private ChartProject _project = new();

    private ReadmeDemoSong(ReadmeDemoVariant variant) => _variant = variant;

    public static (ChartProject Project, IReadOnlyList<ReadmeDemoNote> Audio) Build(ReadmeDemoVariant variant)
    {
        var song = new ReadmeDemoSong(variant);
        song.BuildCore();
        return (song._project, song._audio);
    }

    /// <summary>Two bars of silence before the music, so the first phrase is empty and the count-in has room.</summary>
    private const double Pad = 8;

    private static readonly int[] SectionStarts = [0, 16, 32, 48, 64];

    /// <summary>A position <paramref name="beats" /> beats into the MUSIC (the pad comes first), on a sixteenth grid.</summary>
    private static FractionalPosition At(double beats) => Raw(beats + Pad);

    private static FractionalPosition Raw(double beats)
    {
        var whole = (int)Math.Floor(beats + 1e-9);
        return new FractionalPosition(whole, new Fraction((long)Math.Round((beats - whole) * 4), 4));
    }

    private static FractionalPosition Len(double beats) => Raw(beats);

    /// <summary>A fixed project id per title, so the demo's AI chat history finds its chart again after a regeneration.</summary>
    internal static string StableId(string title) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("dcdemo:" + title)))[..ProjectIdentity.Length];

    private void BuildCore()
    {
        var v = _variant;
        _project = new ChartProject { Bpm = v.Bpm, AudioFileName = "song.wav", ProjectId = StableId(v.Title) };
        _project.Chart.SongName = v.Title;
        _project.Chart.Artist = v.Artist;
        _project.Chart.ArtistSort = v.Artist;
        _project.Chart.AlbumName = v.Album;
        _project.Chart.AlbumYear = v.Year.ToString();
        _project.Chart.Genre = v.Genre;
        _project.Chart.Charter = "DotCharter";
        _project.Chart.BeatsPerMinute = v.Bpm;
        _project.Chart.CoverArtPath = "cover.png";
        for (var i = 0; i <= ((Bars + 2) * 4); i++)
        {
            _project.Beats.Add(new BeatMarker
            {
                Time = i * 60000.0 / v.Bpm, Bpm = v.Bpm, IsMeasure = i % 4 == 0, BeatsInMeasure = 4, IsAnchor = i == 0
            });
        }

        foreach (var tone in new[] { "Clean", "Crunch", "Lead" })
        {
            _project.ToneDefinitions.Add(new ToneDefinition
            {
                Key = tone, Name = tone,
                Json = RsToneJsonWriter.Write(RsToneAuthoring.CreateDefaultTone(tone, tone, RsGearCatalog.Shared))
            });
        }

        _project.Arrangements.Add(BuildGuitar());
        if (v.WithSevenString) _project.Arrangements.Add(BuildSevenString());
        _project.VocalTracks.Add(BuildVocals());
        AddDrumsAndBass();
        AddShowlights();
    }

    /// <summary>Stage lights per section, so the 3D shots show fog, beams and (in the solo) lasers
    /// and the editor's show-light chip names them. RS-XML notes: FOG_* 24-35, BEAMS_* 48-59
    /// (42 off), LASERS 66 on / 67 off.</summary>
    private void AddShowlights()
    {
        (int Fog, int Beams, int Lasers)[] looks =
        [
            (35, 42, 67), // intro: purple fog, no beams
            (28, 55, 67), // verse: blue fog, teal beams
            (30, 55, 67), // chorus: magenta fog, teal beams
            (35, 59, 66), // solo: purple fog, purple beams, lasers on
            (28, 42, 67) // outro: blue fog, beams off, lasers off
        ];
        _project.Showlights.Add(new Showlight { Position = Raw(0), Note = looks[0].Fog });
        for (var i = 0; i < looks.Length; i++)
        {
            var at = At(SectionStarts[i]);
            if (i > 0) _project.Showlights.Add(new Showlight { Position = at, Note = looks[i].Fog });
            _project.Showlights.Add(new Showlight { Position = at, Note = looks[i].Beams });
            _project.Showlights.Add(new Showlight { Position = at, Note = looks[i].Lasers });
        }
    }

    /// <summary>Writes the notes of one arrangement into its level AND into the audio events.</summary>
    private sealed class Part(ReadmeDemoSong song, ChartLevel level, ReadmeDemoInstrument instrument, int[] open)
    {
        public void Note(double beat, double length, int str, int fret, Action<GuitarNote>? configure = null,
            (double Beat, double Semis)[]? pitch = null, MuteType mute = MuteType.None)
        {
            var note = new GuitarNote
            {
                Position = At(beat), StringIndex = str, Fret = fret, Sustain = Len(length), Mute = mute
            };
            configure?.Invoke(note);
            level.Notes.Add(note);
            song._audio.Add(new ReadmeDemoNote(instrument, song.MsOf(beat), song.MsOfLength(length), open[str] + fret,
                mute == MuteType.Palm ? 0.6 : 0.9, pitch?.Select(p => (song.MsOfLength(p.Beat), p.Semis)).ToArray()));
        }

        /// <summary>A pitched slide: the sliding note linked to a short one that lands where it ends.</summary>
        public void Slide(double beat, double length, int str, int from, int to)
        {
            var glide = new (double, double)[] { (0, 0), (length * 0.4, 0), (length, to - from) };
            Note(beat, length, str, from, n =>
            {
                n.SlideTo = to;
                n.LinkNext = true;
            }, glide);
            Note(beat + length, 0.5, str, to);
        }

        public void Chord(ChordTemplate template, double beat, double length, PickDirectionType strum, MuteType mute)
        {
            var chord = new Chord { Position = At(beat), TemplateId = template.Id, Strum = strum };
            foreach (var (str, fret) in template.Frets.OrderBy(f => f.Key))
            {
                chord.ChordNotes.Add(new ChordNote
                {
                    StringIndex = str, Fret = fret, Mute = mute, Sustain = Len(length), ExplicitSustain = true
                });
                song._audio.Add(new ReadmeDemoNote(instrument, song.MsOf(beat) + (str * 8), song.MsOfLength(length),
                    open[str] + fret, mute == MuteType.Palm ? 0.55 : 0.85));
            }

            level.Chords.Add(chord);
            level.HandShapes.Add(new HandShape
            {
                Position = At(beat), EndPosition = At(beat + length), TemplateId = template.Id, Name = template.Name
            });
        }

        public void Anchor(double beat, int fret) => level.Anchors.Add(new FretAnchor(At(beat), Math.Max(1, fret), 4));
    }

    /// <summary>Bend points, as (beats into the note, step); a step of 1 is a half step, 2 a whole step.</summary>
    private static void AddBend(GuitarNote note, double start, params (double Beat, double Step)[] points)
    {
        foreach (var (beat, step) in points) note.BendValues.Add(new BendValue(At(start + beat), step));
    }

    /// <summary>The fret the hand should sit on for a chord: where the index finger plays.</summary>
    private static int AnchorFret(ChordTemplate template)
    {
        var fingered = template.Fingers.Where(f => f.Value > 0).ToList();
        if (fingered.Count == 0) return Math.Max(1, template.GetLowestNotOpenFret());
        var index = fingered.FirstOrDefault(f => f.Value == 1);
        if (index.Value == 1) return template.Frets[index.Key];
        var lowest = fingered.OrderBy(f => f.Value).First();
        return Math.Max(1, template.Frets[lowest.Key] - (lowest.Value - 1));
    }

    private Arrangement BuildGuitar()
    {
        var chords = _variant.Chords;
        var arrangement = NewArrangement("Lead", ArrangementType.Lead, 6, ["E", "A", "D", "G", "B", "e"]);
        var level = arrangement.Levels[0];
        var templates = chords.Select((name, id) => Template(name, id)).ToList();
        foreach (var template in templates) arrangement.ChordTemplates.Add(template);
        var part = new Part(this, level, ReadmeDemoInstrument.Guitar, GuitarOpen);

        // Intro, bars 1-4: arpeggios, and a hammer-on on the second bar.
        for (var bar = 0; bar < 4; bar++)
        {
            var template = templates[Math.Min(bar < 2 ? 0 : bar - 1, chords.Length - 1)];
            var shape = template.Frets.OrderBy(f => f.Key).ToList();
            part.Anchor(bar * 4, AnchorFret(template));
            int[] order = [0, 1, 2, 3, 2, 1, 2, 3];
            for (var step = 0; step < 8; step++)
            {
                var (str, fret) = shape[Math.Min(order[step], shape.Count - 1)];
                var beat = (bar * 4) + (step * 0.5);
                if (bar == 1 && step == 6)
                    part.Note(beat, 0.5, shape[1].Key, shape[1].Value);
                else if (bar == 1 && step == 7)
                    part.Note(beat, 0.5, shape[1].Key, shape[1].Value + 2, n => n.Hopo = HopoType.HammerOn);
                else
                    part.Note(beat, 0.5, str, fret);
            }
        }

        // Verse, bars 5-8: palm-muted chord stabs.
        for (var bar = 4; bar < 8; bar++)
        {
            var template = templates[bar - 4];
            part.Anchor(bar * 4, AnchorFret(template));
            part.Chord(template, bar * 4, 1.5, PickDirectionType.Down, MuteType.Palm);
            part.Chord(template, (bar * 4) + 1.5, 0.5, PickDirectionType.Up, MuteType.Palm);
            part.Chord(template, (bar * 4) + 2, 1.0, PickDirectionType.Down, MuteType.Palm);
            part.Chord(template, (bar * 4) + 3, 1.0, PickDirectionType.Down, MuteType.None);
        }

        // Chorus, bars 9-12: open chords that ring, and a slide fill on the last beat.
        for (var bar = 8; bar < 12; bar++)
        {
            var template = templates[bar - 8];
            part.Anchor(bar * 4, AnchorFret(template));
            part.Chord(template, bar * 4, 3, PickDirectionType.Down, MuteType.None);
            part.Anchor((bar * 4) + 3, 2);
            part.Slide((bar * 4) + 3, 0.5, 5, 2, 4);
        }

        // Solo, bars 13-16: pentatonic with a bend, pull-offs, hammer-ons, slides and vibrato.
        var s = _variant.SoloShift;
        part.Anchor(48, 12 + s);
        part.Note(48, 2, 5, 12 + s, n => AddBend(n, 48, (0.5, 1.0), (1.0, 2.0), (1.5, 2.0)), [(0, 0), (0.5, 1), (1, 2), (1.5, 2), (2, 2)]);
        part.Note(50, 1, 5, 15 + s);
        part.Note(51, 1, 5, 12 + s, n => n.Hopo = HopoType.PullOff);
        part.Note(52, 0.5, 4, 15 + s);
        part.Note(52.5, 0.5, 4, 12 + s, n => n.Hopo = HopoType.PullOff);
        part.Note(53, 0.5, 3, 12 + s);
        part.Note(53.5, 0.5, 3, 14 + s, n => n.Hopo = HopoType.HammerOn);
        part.Slide(54, 1.5, 4, 12 + s, 15 + s);
        part.Note(56, 2, 5, 15 + s, n =>
        {
            n.Vibrato = true;
            n.VibratoStrength = 80;
        }, [(0, 0), (2, 0)]);
        part.Note(58, 1, 5, 12 + s, n => AddBend(n, 58, (0.5, 1.0)), [(0, 0), (0.5, 1), (1, 1)]);
        part.Note(59, 1, 4, 15 + s);
        part.Note(60, 0.5, 4, 12 + s);
        part.Note(60.5, 0.5, 4, 14 + s, n => n.Hopo = HopoType.HammerOn);
        part.Slide(61, 2, 3, 12 + s, 14 + s);

        // Outro, bars 17-20: the progression once more, the last chord rings out.
        for (var bar = 16; bar < 20; bar++)
        {
            var template = templates[bar - 16];
            part.Anchor(bar * 4, AnchorFret(template));
            part.Chord(template, bar * 4, bar == 19 ? 4 : 3.5, PickDirectionType.Down, MuteType.None);
        }

        Finish(arrangement, level);
        arrangement.ToneBase = "Clean";
        arrangement.ToneA = "Crunch";
        arrangement.ToneB = "Lead";
        arrangement.StartingTone = "Clean";
        arrangement.ToneNames.Add("Clean");
        arrangement.ToneNames.Add("Crunch");
        arrangement.ToneNames.Add("Lead");
        arrangement.ToneChanges.Add(new ToneChange { Position = At(15.25), ToneName = "Crunch" });
        arrangement.ToneChanges.Add(new ToneChange { Position = At(31.25), ToneName = "Clean" });
        arrangement.ToneChanges.Add(new ToneChange { Position = At(47.25), ToneName = "Lead" });
        arrangement.ToneChanges.Add(new ToneChange { Position = At(63.25), ToneName = "Clean" });
        return arrangement;
    }

    /// <summary>The 7-string part: palm-muted chugs on the low B, then power chords and a low melody.</summary>
    private Arrangement BuildSevenString()
    {
        var arrangement = NewArrangement("Seven", ArrangementType.Rhythm, 7, ["B", "E", "A", "D", "G", "B", "e"]);
        var level = arrangement.Levels[0];
        (string Name, int Root)[] power = [("B5", 0), ("G5", 8), ("D5", 3), ("A5", 10)];
        var templates = power.Select((p, id) => new ChordTemplate
        {
            Id = id, Name = p.Name,
            Frets = new Dictionary<int, int> { [0] = p.Root, [1] = p.Root + 2, [2] = p.Root + 2 },
            Fingers = p.Root == 0
                ? new Dictionary<int, int> { [1] = 1, [2] = 2 }
                : new Dictionary<int, int> { [0] = 1, [1] = 3, [2] = 4 }
        }).ToList();
        foreach (var template in templates) arrangement.ChordTemplates.Add(template);
        var part = new Part(this, level, ReadmeDemoInstrument.Seven, SevenOpen);

        // Intro and verse (bars 1-8): the low-B chug riff.
        for (var bar = 0; bar < 8; bar++)
        {
            var root = new[] { 0, 0, 3, 5 }[bar % 4];
            part.Anchor(bar * 4, root == 0 ? 1 : root);
            for (var step = 0; step < 8; step++)
            {
                var accent = step is 3 or 6;
                part.Note((bar * 4) + (step * 0.5), 0.5, 0, accent ? root + 3 : root, null, null,
                    accent ? MuteType.None : MuteType.Palm);
            }
        }

        // Chorus (bars 9-12): power chords, a ringing low note on the last beat.
        for (var bar = 8; bar < 12; bar++)
        {
            var template = templates[bar - 8];
            part.Anchor(bar * 4, AnchorFret(template));
            part.Chord(template, bar * 4, 2, PickDirectionType.Down, MuteType.None);
            part.Chord(template, (bar * 4) + 2, 1, PickDirectionType.Down, MuteType.Palm);
            part.Note((bar * 4) + 3, 1, 0, template.Frets[0]);
        }

        // Solo (bars 13-16): a melody on the low strings with a slide, a bend and a hammer-on.
        part.Anchor(48, 5);
        part.Slide(48, 1.5, 1, 5, 8);
        part.Note(50, 2, 1, 7, n => AddBend(n, 50, (0.5, 1.0), (1.0, 2.0), (1.5, 2.0)), [(0, 0), (0.5, 1), (1, 2), (2, 2)]);
        part.Anchor(52, 8);
        part.Note(52, 1, 0, 8);
        part.Note(53, 1, 0, 10, n => n.Hopo = HopoType.HammerOn);
        part.Anchor(54, 3);
        part.Slide(54, 1.5, 0, 6, 3);
        part.Anchor(56, 1);
        for (var i = 0; i < 8; i++)
            part.Note(56 + (i * 0.5), 0.5, 0, i % 2 == 0 ? 0 : 3, null, null, i % 2 == 0 ? MuteType.Palm : MuteType.None);
        part.Anchor(60, 2);
        for (var i = 0; i < 8; i++)
            part.Note(60 + (i * 0.5), 0.5, 0, i % 4 == 3 ? 5 : 0, null, null, i % 4 == 3 ? MuteType.None : MuteType.Palm);

        // Outro (bars 17-20).
        for (var bar = 16; bar < 20; bar++)
        {
            var template = templates[bar - 16];
            part.Anchor(bar * 4, AnchorFret(template));
            part.Chord(template, bar * 4, bar == 19 ? 4 : 3.5, PickDirectionType.Down, MuteType.None);
        }

        Finish(arrangement, level);
        arrangement.ToneBase = "Crunch";
        arrangement.StartingTone = "Crunch";
        arrangement.ToneNames.Add("Crunch");
        return arrangement;
    }

    private Arrangement NewArrangement(string name, ArrangementType type, int strings, string[] tuning)
    {
        var arrangement = new Arrangement
        {
            Name = name, ArrangementType = type, StringCount = strings, Tuning = tuning,
            TuningType = "E_STANDARD", ArtistName = _variant.Artist, AlbumName = _variant.Album,
            AlbumYear = _variant.Year
        };
        for (var i = 0; i < strings; i++) arrangement.TuningPitches.Add(new TuningOffset(0));
        arrangement.Levels.Add(new ChartLevel { DifficultyIndex = 0 });
        for (var i = 0; i < SectionNames.Length; i++)
        {
            arrangement.EventPoints.Add(new EventPoint
            {
                Position = At(SectionStarts[i]), Section = SectionNames[i], Phrase = SectionNames[i],
                Status = i < 3 ? SectionStatus.Done : SectionStatus.Review
            });
        }

        arrangement.Phrases.Add("COUNT");
        arrangement.PhraseIterations.Add(new PhraseIteration(Raw(0), 0));
        foreach (var section in SectionNames) arrangement.Phrases.Add(section);
        arrangement.Phrases.Add("END");
        arrangement.PhraseInfos["COUNT"] = new PhraseInfo(0, false);
        foreach (var section in SectionNames) arrangement.PhraseInfos[section] = new PhraseInfo(0, section == "solo");
        arrangement.PhraseInfos["END"] = new PhraseInfo(0, false);
        for (var i = 0; i < SectionNames.Length; i++)
            arrangement.PhraseIterations.Add(new PhraseIteration(At(SectionStarts[i]), i + 1));
        arrangement.PhraseIterations.Add(new PhraseIteration(At(Bars * 4), SectionNames.Length + 1));
        return arrangement;
    }

    private void Finish(Arrangement arrangement, ChartLevel level)
    {
        Sort(level.Notes, _project.Beats);
        Sort(level.Chords, _project.Beats);
        Sort(level.HandShapes, _project.Beats);
        Sort(level.Anchors, _project.Beats);
        arrangement.Properties = ArrangementPropertiesDeriver.Derive(arrangement);
    }

    private static void Sort<T>(System.Collections.ObjectModel.ObservableCollection<T> items, IList<BeatMarker> beats)
        where T : ITimedElement
    {
        var sorted = items.OrderBy(i => i.GetTimeMs(beats)).ToList();
        items.Clear();
        foreach (var item in sorted) items.Add(item);
    }

    private static ChordTemplate Template(string name, int id)
    {
        var (frets, fingers, _) = Shapes[name];
        var template = new ChordTemplate { Id = id, Name = name };
        for (var str = 0; str < frets.Length; str++)
        {
            if (frets[str] >= 0) template.Frets[str] = frets[str];
        }

        foreach (var (str, finger) in fingers) template.Fingers[str] = finger;
        return template;
    }

    private VocalTrack BuildVocals()
    {
        var track = new VocalTrack { Name = "Vocals" };
        string[][] lines =
        [
            ["Every", "dot", "becomes", "a", "line"],
            ["Every", "line", "becomes", "a", "song"],
            ["Count", "the", "bars", "and", "mind", "the", "time"],
            ["We", "will", "play", "it", "all", "night", "long"],
            ["Slide", "into", "the", "open", "air"],
            ["Hammer", "on", "and", "let", "it", "ring"],
            ["Every", "bend", "a", "little", "brighter"],
            ["Every", "chord", "a", "little", "thing"],
            ["Light", "it", "up,", "the", "highway", "glows"],
            ["Strings", "are", "bending,", "here", "it", "goes"],
            ["Dots", "and", "lines", "and", "bars", "and", "beats"],
            ["Play", "it", "loud", "and", "play", "it", "sweet"]
        ];
        double[] starts = [0, 4, 8, 12, 16, 20, 24, 28, 32, 36, 40, 44];
        for (var i = 0; i < lines.Length; i++)
        {
            var words = lines[i];
            var beat = starts[i] + 0.5;
            var step = 3.0 / words.Length;
            var phrase = new LyricPhrase { Position = At(starts[i]), EndPosition = At(starts[i] + 4) };
            for (var w = 0; w < words.Length; w++)
            {
                var last = w == words.Length - 1;
                phrase.Lyrics.Add(new LyricEvent
                {
                    Text = words[w], Position = At(beat + (w * step)),
                    EndPosition = At(beat + (w * step) + (step * 0.85)), IsPhraseEnd = last
                });
            }

            track.Phrases.Add(phrase);
        }

        return track;
    }

    private void AddDrumsAndBass()
    {
        var chords = _variant.Chords;
        for (var bar = 0; bar < Bars; bar++)
        {
            var index = bar < 4 ? Math.Min(bar < 2 ? 0 : bar - 1, 3) : bar < 8 ? bar - 4 : bar < 12 ? bar - 8 : bar < 16 ? bar % 4 : bar - 16;
            var root = Shapes[chords[Math.Min(index, 3)]].BassMidi;
            for (var half = 0; half < 2; half++)
            {
                _audio.Add(new ReadmeDemoNote(ReadmeDemoInstrument.Bass, MsOf((bar * 4) + (half * 2)),
                    MsOfLength(half == 0 ? 1.5 : 1.0), root, 0.9));
            }

            for (var beat = 0; beat < 4; beat++)
            {
                if (bar < 4 && beat % 2 == 1) continue;
                var ms = MsOf((bar * 4) + beat);
                _audio.Add(new ReadmeDemoNote(beat % 2 == 0 ? ReadmeDemoInstrument.Kick : ReadmeDemoInstrument.Snare,
                    ms, 200, 0, 0.9));
            }

            for (var eighth = 0; eighth < 8; eighth++)
            {
                _audio.Add(new ReadmeDemoNote(ReadmeDemoInstrument.Hat, MsOf((bar * 4) + (eighth * 0.5)), 60, 0,
                    eighth % 2 == 0 ? 0.55 : 0.35));
            }
        }
    }

    private double MsOf(double beat) => (beat + Pad) * 60000.0 / _variant.Bpm;

    private double MsOfLength(double beats) => beats * 60000.0 / _variant.Bpm;
}
