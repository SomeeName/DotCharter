using System.Xml.Linq;
using DotCharter.Services.Editing.Validation.Rules;
using DotCharter.Services.Parsing.GuitarPro;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

public sealed class GpDeadNoteSlideEdgeTests
{
    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    public void GraceParsingDoesNotCreateDeadSlideMarkers(int flags)
    {
        var xml = XDocument.Parse(PickScrapeScenes.Gpif(new PickScrapeScenes.ScrapeBeat[]
        {
            new("Quarter", [new(0, 0, flags)])
        }));
        var note = new GpNoteParser().ParseNote(xml.Descendants("Note").Single(), FractionalPosition.Zero,
            FractionalPosition.Zero, 6, false, isGrace: true);
        Assert.Equal(0, note.Fret);
        Assert.Null(note.SlideTo);
        Assert.False(note.UnpitchedSlide);
        Assert.False(note.LinkNext);
    }

    [Theory]
    [InlineData("BeforeBeat")]
    [InlineData("OnBeat")]
    public void DeadOpenGraceStaysANotationHint(string placement)
    {
        var xml = XDocument.Parse(PickScrapeScenes.Gpif(new PickScrapeScenes.ScrapeBeat[]
        {
            new("Quarter", [new(0, 0, 4)]), new("Quarter", [new(0, 5)])
        }));
        xml.Descendants("Beat").First().Add(new XElement("GraceNotes", placement));
        var arrangement = PickScrapeScenes.Import(xml.ToString());
        AssertGraceHint(arrangement);
    }

    [Theory]
    [InlineData("4.06")]
    [InlineData("5.10")]
    public void LegacyOpenSlideGraceStaysANotationHint(string version)
    {
        var path = LegacyGpFixtureBuilder.WriteFile(version,
            [new FxMeasure().AddVoice(FxBeat.Chord(0, new FxNote(0, 5, GraceFret: 0, GraceSlide: true)))]);
        AssertGraceHint(ImportLegacy(path));
    }

    [Theory]
    [InlineData("gpif", 120)]
    [InlineData("gpif", 240)]
    [InlineData("gp5", 120)]
    public void DeadSlideLeavesOneThirtySecondBeatBeforeTheNextSound(string format, int bpm)
    {
        var arrangement = GpDeadNoteSlideImportTests.Import(format, 4, 0, 5);
        var level = arrangement.Levels[0];
        var slide = level.Notes[0];
        Assert.Equal(new FractionalPosition(0, new Fraction(31, 32)), slide.Sustain);
        Assert.Equal(Fraction.One, slide.NotationDuration);
        var issues = new List<ArrangementIssue>();
        new AnchorRule().Validate(ArrangementValidationContext.Build(arrangement, Beats(bpm), null), issues);
        Assert.DoesNotContain(issues, i => i.Type == ArrangementIssueType.AnchorCloseToUnpitchedSlide);
    }

    [Theory]
    [InlineData(991, 1, 1000)]
    [InlineData(991, 4, 1000)]
    [InlineData(0, 1, 1)]
    public void SubGridDeadSlideKeepsItsAuthoredSustainWhenTheBeatGapCannotFit(int onsetTicks, int sustainTicks, int nextTicks)
    {
        var result = new GpImportResult();
        var arrangement = new Arrangement();
        var level = new ChartLevel();
        var slide = new GuitarNote
        {
            Position = new FractionalPosition(0, new Fraction(onsetTicks, 1000)),
            Sustain = new FractionalPosition(0, new Fraction(sustainTicks, 1000)),
            NotationDuration = new Fraction(sustainTicks, 1000),
            Fret = 12, SlideTo = -4, UnpitchedSlide = true, Mute = MuteType.Full
        };
        level.Notes.Add(slide);
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(0, new Fraction(nextTicks, 1000)), Fret = 5 });
        arrangement.Levels.Add(level);
        result.Arrangements.Add(arrangement);
        GpDeadNoteSlide.Resolve(level, 0, 24, result.DeadSlideSounds);

        GpDeadNoteSlide.TrimSustains(result);

        Assert.True(slide.Sustain > FractionalPosition.Zero);
        Assert.Equal(new FractionalPosition(0, new Fraction(sustainTicks, 1000)), slide.Sustain);
        Assert.Equal(new Fraction(sustainTicks, 1000), slide.NotationDuration);
    }

    [Theory]
    [InlineData("gpif", false)]
    [InlineData("gpif", true)]
    [InlineData("gp5", false)]
    [InlineData("gp5", true)]
    public void TiedDeadOpenNotesKeepOneAttackAndTheWholeSustain(string format, bool slideOnFirst)
    {
        Arrangement arrangement;
        if (format == "gpif")
        {
            var xml = XDocument.Parse(PickScrapeScenes.Gpif(new PickScrapeScenes.ScrapeBeat[]
            {
                new("Quarter", [new(0, 0, slideOnFirst ? 4 : 0)]),
                new("Quarter", [new(0, 0, slideOnFirst ? 0 : 4)])
            }));
            var notes = xml.Descendants("Note").ToArray();
            notes[0].Add(new XElement("Tie", new XAttribute("origin", "true")));
            notes[1].Add(new XElement("Tie", new XAttribute("destination", "true")));
            foreach (var part in notes)
            {
                if (!part.Descendants("Property").Any(p => p.Attribute("name")?.Value == "Muted"))
                    part.Element("Properties")!.Add(new XElement("Property", new XAttribute("name", "Muted")));
            }
            arrangement = PickScrapeScenes.Import(xml.ToString());
        }
        else
        {
            var path = LegacyGpFixtureBuilder.WriteFile("5.10", [new FxMeasure().AddVoice(
                FxBeat.Chord(0, new FxNote(0, 0, Dead: true, Slide: slideOnFirst ? 4 : null)),
                FxBeat.Chord(0, new FxNote(0, 0, Tied: true, Slide: slideOnFirst ? null : 4)))]);
            arrangement = ImportLegacy(path);
        }
        var note = Assert.Single(arrangement.Levels[0].Notes);
        Assert.Equal((12, 5, MuteType.Full, true, false),
            (note.Fret, note.SlideTo, note.Mute, note.UnpitchedSlide, note.LinkNext));
        Assert.Equal(new FractionalPosition(2), note.Sustain);
        Assert.Equal(new Fraction(2, 1), note.NotationDuration);
    }

    [Theory]
    [InlineData("4.06", 3, 1, 0)]
    [InlineData("4.06", 4, 24, 0)]
    [InlineData("4.06", 3, 0, 24)]
    [InlineData("5.10", 4, 1, 0)]
    [InlineData("5.10", 8, 24, 0)]
    [InlineData("5.10", 4, 0, 24)]
    public void LegacyDeadNotesKeepFullMuteWhenThePalmBitIsSetWithoutSlideRoom(string version, int flags, int fret, int capo)
    {
        var path = LegacyGpFixtureBuilder.WriteFile(version,
            [new FxMeasure().AddVoice(FxBeat.Chord(0, new FxNote(0, fret, Dead: true, Slide: flags, PalmMute: true)))], capo: capo);
        Assert.Equal(MuteType.Full, Assert.Single(ImportLegacy(path).Levels[0].Notes).Mute);
    }

    /// <summary>Checks the final fret clamp; unresolved direction markers are cleared separately.</summary>
    [Theory]
    [InlineData("gpif")]
    [InlineData("gp5")]
    [InlineData("gp4")]
    public void ImportedSlideTargetsStayWithinThePlayableFretRange(string format)
    {
        foreach (var flags in new[] { 4, 8 })
        {
            var arrangement = GpDeadNoteSlideImportTests.Import(format, flags, 0, flags == 4 ? 5 : 12, capo: 3, frets: 22, chord: true);
            var level = arrangement.Levels[0];
            var sounds = level.Notes.Cast<IGuitarSound>().Concat(level.Chords.SelectMany(c => c.ChordNotes));
            Assert.Contains(sounds, n => n.SlideTo.HasValue);
            Assert.All(sounds, n => Assert.True(n.SlideTo == null || n.SlideTo >= arrangement.Capo + 1 && n.SlideTo <= 22));
        }
    }

    private static void AssertGraceHint(Arrangement arrangement)
    {
        var note = Assert.Single(arrangement.Levels[0].Notes);
        Assert.Equal(5, note.Fret);
        Assert.Equal(0, note.GraceFret);
        Assert.Null(note.SlideTo);
        Assert.False(note.LinkNext);
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

    private static List<BeatMarker> Beats(int bpm) => Enumerable.Range(0, 8)
        .Select(i => new BeatMarker { Time = i * 60000.0 / bpm, Bpm = bpm, BeatsInMeasure = 4, NoteDenominator = 4 }).ToList();
}
