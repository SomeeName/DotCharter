using System.Xml.Linq;
using DotCharter.Services.Editing.Validation.Rules;
using DotCharter.Services.Export.GuitarPro;
using DotCharter.Services.Parsing.GuitarPro;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

public sealed class GpDeadNoteSlideImportTests
{
    public static IEnumerable<object[]> Slides()
    {
        var cases = new[]
        {
            (4, 0, 5, 0, 24, 12, 5), (8, 0, 12, 0, 24, 5, 12),
            (4, 0, -1, 0, 24, 12, 5), (8, 0, -1, 0, 24, 5, 12),
            (4, 0, 15, 0, 24, 12, 5), (8, 0, 2, 0, 24, 5, 12),
            (4, 0, 11, 0, 24, 12, 5), (8, 0, 6, 0, 24, 5, 12),
            (4, 15, 5, 0, 24, 15, 5), (8, 7, 12, 0, 24, 7, 12),
            (4, 15, -1, 0, 24, 15, 8), (8, 7, -1, 0, 24, 7, 14),
            (4, 0, 5, 3, 24, 15, 8), (8, 0, 12, 3, 24, 8, 15),
            (4, 3, -1, 3, 24, 6, 4), (8, 20, -1, 0, 22, 20, 22),
            (4, 0, -1, 18, 22, 22, 19), (8, 0, -1, 18, 22, 21, 22),
            (4, 0, 10, 0, 24, 12, 10), (8, 0, 7, 0, 24, 5, 7),
            (4, 24, 5, 0, 24, 24, 5), (4, 3, -1, 0, 24, 3, 1)
        };
        foreach (var format in new[] { "gpif", "gp5", "gp4" })
        {
            foreach (var (flags, fret, next, capo, frets, start, landing) in cases)
                yield return [format, flags, fret, next, capo, frets, start, landing];
        }
    }

    [Theory]
    [MemberData(nameof(Slides))]
    public void DeadSlideUsesPlayableStartAndDirectionalLanding(string format, int flags, int fret, int next,
        int capo, int frets, int start, int landing)
    {
        var arrangement = Import(format, flags, fret, next, capo, frets);
        var level = Assert.Single(arrangement.Levels);
        var note = level.Notes[0];
        Assert.Equal((start, landing, MuteType.Full, true),
            (note.Fret, note.SlideTo, note.Mute, note.UnpitchedSlide));
        Assert.Equal((HopoType.None, false, false, false), (note.Hopo, note.Tremolo, note.Ignore, note.LinkNext));
        var anchor = level.Anchors.Last(a => a.Position <= note.Position);
        Assert.InRange(start, anchor.Fret, anchor.Fret + anchor.Width - 1);
        AssertSlideValidation(arrangement);
    }

    [Theory]
    [InlineData("gpif", 4, 12, 5)]
    [InlineData("gpif", 8, 5, 12)]
    [InlineData("gp5", 4, 12, 5)]
    [InlineData("gp5", 8, 5, 12)]
    [InlineData("gp4", 4, 12, 5)]
    [InlineData("gp4", 8, 5, 12)]
    public void DeadSlideChordKeepsEveryMutedString(string format, int flags, int start, int landing)
    {
        var arrangement = Import(format, flags, 0, landing, chord: true);
        var chord = Assert.Single(Assert.Single(arrangement.Levels).Chords);
        Assert.Equal(2, chord.ChordNotes.Count);
        Assert.All(chord.ChordNotes, n => AssertSlide(n, start, landing));
        AssertSlideValidation(arrangement);
    }

    [Theory]
    [InlineData("gpif", 4, 15, 5, 0)]
    [InlineData("gpif", 8, 7, -1, 0)]
    [InlineData("gpif", 4, 0, 5, 0)]
    [InlineData("gpif", 8, 0, 12, 0)]
    [InlineData("gpif", 4, 0, -1, 0)]
    [InlineData("gpif", 8, 0, -1, 0)]
    [InlineData("gp5", 4, 0, 5, 3)]
    [InlineData("gp5", 8, 0, 12, 3)]
    public void ExportReimportKeepsDeadSlideNotes(string format, int flags, int fret, int next, int capo)
    {
        var arrangement = Import(format, flags, fret, next, capo);
        var start = fret == 0 ? capo + (flags == 4 ? 12 : 5) : fret + capo;
        var landing = next >= 0 ? next + capo : start + (flags == 4 ? -7 : 7);
        AssertSlide(arrangement.Levels[0].Notes[0], start, landing);
        Assert.Equal(Sounds(arrangement), Sounds(Reimport(arrangement)));
    }

    [Theory]
    [InlineData("gpif", 4, 12, 5)]
    [InlineData("gpif", 8, 5, 12)]
    [InlineData("gp5", 4, 12, 5)]
    [InlineData("gp5", 8, 5, 12)]
    public void ExportReimportKeepsDeadSlideChord(string format, int flags, int start, int landing)
    {
        var arrangement = Import(format, flags, 0, landing, chord: true);
        var chord = Assert.Single(arrangement.Levels[0].Chords);
        Assert.All(chord.ChordNotes, n => AssertSlide(n, start, landing));
        var roundTrip = Reimport(arrangement);
        Assert.Single(roundTrip.Levels[0].Chords);
        Assert.Equal(Sounds(arrangement), Sounds(roundTrip));
    }

    [Theory]
    [InlineData("gpif")]
    [InlineData("gp5")]
    [InlineData("gp4")]
    public void DeadSlideClearsTapTremoloAndPalmMute(string format)
    {
        var arrangement = Import(format, 4, 0, 5, decorated: true);
        AssertSlide(arrangement.Levels[0].Notes[0], 12, 5);
        AssertSlideValidation(arrangement);
    }

    [Theory]
    [InlineData("gpif", "Bass")]
    [InlineData("gpif", "Rhythm")]
    [InlineData("gp5", "Bass")]
    [InlineData("gp5", "Rhythm")]
    [InlineData("gp4", "Bass")]
    [InlineData("gp4", "Rhythm")]
    public void DeadSlideUsesTheSameRuleOnEveryTrackType(string format, string trackName)
    {
        var arrangement = Import(format, 4, 0, 5, trackName: trackName);
        Assert.Equal(trackName == "Bass" ? ArrangementType.Bass : ArrangementType.Rhythm, arrangement.ArrangementType);
        AssertSlide(arrangement.Levels[0].Notes[0], 12, 5);
    }

    [Theory]
    [InlineData("gpif")]
    [InlineData("gp5")]
    public void NotationVoiceResolvesDeadSlideSentinels(string format)
    {
        var arrangement = Import(format, 4, 0, 5, sidecar: true);
        var voice = Assert.Single(arrangement.NotationVoices);
        AssertSlide(voice.Notes[0], 12, 5);
    }

    [Theory]
    [InlineData("gpif", 4, 1, 0, 1, 1, true)]
    [InlineData("gpif", 8, 24, 0, 24, 24, true)]
    [InlineData("gpif", 4, 0, 24, 0, null, false)]
    [InlineData("gpif", 8, 0, 24, 0, null, false)]
    [InlineData("gp5", 4, 1, 0, 1, 1, true)]
    [InlineData("gp5", 8, 24, 0, 24, 24, true)]
    [InlineData("gp5", 4, 0, 24, 0, null, false)]
    [InlineData("gp5", 8, 0, 24, 0, null, false)]
    public void ADeadSlideWithoutRoomKeepsItsAuthoredFret(string format, int flags, int fret, int capo,
        int start, int? landing, bool unpitched)
    {
        var note = Assert.Single(Import(format, flags, fret, -1, capo).Levels[0].Notes);
        Assert.Equal((start, landing, MuteType.Full, unpitched), (note.Fret, note.SlideTo, note.Mute, note.UnpitchedSlide));
    }

    [Theory]
    [InlineData("gpif")]
    [InlineData("gp5")]
    public void NextFrettedSoundSkipsOpenAndDeadNotes(string format)
    {
        Arrangement arrangement;
        if (format == "gpif")
        {
            var beats = new PickScrapeScenes.ScrapeBeat[]
            {
                new("Quarter", [new(0, 0, 4)]), new("Quarter", [new(1, 0)]),
                new("Quarter", [new(1, 9)]), new("Quarter", [new(2, 5)])
            };
            var xml = XDocument.Parse(PickScrapeScenes.Gpif(beats));
            xml.Descendants("Note").ElementAt(2).Element("Properties")!.Add(
                new XElement("Property", new XAttribute("name", "Muted"), new XElement("Enable")));
            arrangement = PickScrapeScenes.Import(xml.ToString());
        }
        else
        {
            var measure = new FxMeasure().AddVoice(FxBeat.Chord(0, new FxNote(0, 0, Slide: 4, Dead: true)),
                FxBeat.Note(0, 1, 0), FxBeat.Chord(0, new FxNote(1, 9, Dead: true)), FxBeat.Note(0, 2, 5));
            arrangement = ImportLegacy(LegacyGpFixtureBuilder.WriteFile("5.10", [measure]));
        }
        AssertSlide(arrangement.Levels[0].Notes[0], 12, 5);
    }

    [Fact]
    public void AnotherTracksEarlierSoundCannotSupplyTheLanding()
    {
        var lead = new FxMeasure().AddVoice(FxBeat.Chord(0, new FxNote(0, 0, Slide: 4, Dead: true)),
            FxBeat.RestBeat(0), FxBeat.Note(0, 2, 5));
        var rhythm = new FxMeasure().AddVoice(FxBeat.RestBeat(0), FxBeat.Note(0, 0, 9));
        var path = LegacyGpFixtureBuilder.WriteMultiTrackFile("5.10", [("Lead", [lead]), ("Rhythm", [rhythm])]);
        try
        {
            var importer = new GpImporter();
            var tracks = importer.GetAvailableTracks(path);
            var arrangements = importer.ImportTracks(path, tracks.Select(t => t.Id).ToList());
            var importedLead = Assert.Single(arrangements, a => a.Name == "Lead");
            AssertSlide(importedLead.Levels[0].Notes[0], 12, 5);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static Arrangement Reimport(Arrangement arrangement)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dead-slide-export-{Guid.NewGuid():N}.gp");
        try
        {
            GpExporter.Export(arrangement, arrangement.Levels[0], Beats(), path);
            var importer = new GpImporter();
            return Assert.Single(importer.ImportTracks(path, [Assert.Single(importer.GetAvailableTracks(path)).Id]));
        }
        finally
        {
            File.Delete(path);
        }
    }

    internal static Arrangement Import(string format, int flags, int fret, int next, int capo = 0, int frets = 24,
        bool chord = false, bool decorated = false, string trackName = "Guitar", bool sidecar = false)
    {
        if (format == "gpif")
        {
            var first = new List<PickScrapeScenes.ScrapeNote> { new(0, fret, flags) };
            if (chord) first.Add(new(1, fret, flags));
            var beats = new List<PickScrapeScenes.ScrapeBeat> { new("Quarter", first.ToArray()) };
            if (next >= 0) beats.Add(new("Quarter", [new(2, next)]));
            var xml = XDocument.Parse(PickScrapeScenes.GpifWithFrets(frets, beats.ToArray()));
            xml.Descendants("Track").Single().Element("Name")!.Value = trackName;
            if (decorated)
            {
                xml.Descendants("Note").First().Element("Properties")!.Add(
                    new XElement("Property", new XAttribute("name", "Tapped"), new XElement("Enable")),
                    new XElement("Property", new XAttribute("name", "PalmMuted"), new XElement("Enable")));
                xml.Descendants("Beat").First().Add(new XElement("Tremolo"));
            }
            if (sidecar) xml.Descendants("Bar").Single().Element("Voices")!.Value = "-1 0 -1 -1";
            xml.Descendants("Staff").Single().Element("Properties")!.Add(
                new XElement("Property", new XAttribute("name", "CapoFret"), new XElement("Fret", capo)));
            return PickScrapeScenes.Import(xml.ToString());
        }

        var slide = format == "gp4" ? flags == 4 ? 3 : 4 : flags;
        var notes = new List<FxNote>
        {
            new(0, fret, Slide: slide, Dead: true, PalmMute: decorated, TremoloPicking: decorated)
        };
        if (chord) notes.Add(new(1, fret, Slide: slide, Dead: true));
        var firstBeat = FxBeat.Chord(0, notes.ToArray());
        if (decorated) firstBeat.BeatStringEffect = 1;
        var voice = new List<FxBeat> { firstBeat };
        if (next >= 0) voice.Add(FxBeat.Note(0, 2, next));
        var measure = new FxMeasure();
        if (sidecar) measure.AddVoice(FxBeat.RestBeat(-2));
        measure.AddVoice(voice.ToArray());
        return ImportLegacy(LegacyGpFixtureBuilder.WriteFile(format == "gp4" ? "4.06" : "5.10", [measure],
            trackName: trackName, capo: capo, frets: frets));
    }

    private static Arrangement ImportLegacy(string path)
    {
        try
        {
            var importer = new GpImporter();
            return Assert.Single(importer.ImportTracks(path, [Assert.Single(importer.GetAvailableTracks(path)).Id]));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static (FractionalPosition, int, int, int?, MuteType, bool, HopoType, bool, bool)[] Sounds(Arrangement arrangement) =>
        arrangement.Levels[0].Notes.Select(n => (n.Position, Sound: (IGuitarSound)n))
            .Concat(arrangement.Levels[0].Chords.SelectMany(c => c.ChordNotes.Select(n => (c.Position, Sound: (IGuitarSound)n))))
            .OrderBy(s => s.Position).ThenBy(s => s.Sound.StringIndex)
            .Select(s => (s.Position, s.Sound.StringIndex, s.Sound.Fret, s.Sound.SlideTo, s.Sound.Mute,
                s.Sound.UnpitchedSlide, s.Sound.Hopo, s.Sound.Tremolo, s.Sound.LinkNext)).ToArray();

    private static void AssertSlide(IGuitarSound sound, int start, int landing)
    {
        Assert.Equal((start, landing, MuteType.Full, true),
            (sound.Fret, sound.SlideTo, sound.Mute, sound.UnpitchedSlide));
        Assert.Equal((HopoType.None, false, false), (sound.Hopo, sound.Tremolo, sound.LinkNext));
        if (sound is GuitarNote note) Assert.False(note.Ignore);
        if (sound is ChordNote chordNote) Assert.False(chordNote.Ignore);
    }

    private static void AssertSlideValidation(Arrangement arrangement)
    {
        var issues = new List<ArrangementIssue>();
        var beats = Beats();
        foreach (var beat in beats) beat.Time += 1500;
        var context = ArrangementValidationContext.Build(arrangement, beats, null);
        new SlideTechniqueRule().Validate(context, issues);
        new CapoAnchorRangeRule().Validate(context, issues);
        Assert.Empty(issues);
        var findings = new ArrangementValidationService(new FakeAppLogger()).Validate(arrangement, beats);
        Assert.DoesNotContain(findings, i => i.Type == ArrangementIssueType.ValidationRuleCrashed || i.Anchor != null
            || i.Note?.UnpitchedSlide == true || i.Chord?.ChordNotes.Any(n => n.UnpitchedSlide) == true);
        var sounds = arrangement.Levels[0].Notes.Cast<IGuitarSound>()
            .Concat(arrangement.Levels[0].Chords.SelectMany(c => c.ChordNotes));
        Assert.All(sounds.Where(n => n.UnpitchedSlide), n =>
        {
            Assert.True(n.Fret > arrangement.Capo);
            Assert.True(n.SlideTo > arrangement.Capo);
        });
    }

    private static List<BeatMarker> Beats() => Enumerable.Range(0, 8)
        .Select(i => new BeatMarker { Time = i * 500, Bpm = 120, IsMeasure = i % 4 == 0, BeatsInMeasure = 4, NoteDenominator = 4 }).ToList();
}
