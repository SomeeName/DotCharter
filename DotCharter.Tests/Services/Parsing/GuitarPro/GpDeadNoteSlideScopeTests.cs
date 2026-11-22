using System.Xml.Linq;
using DotCharter.Services.Parsing.GuitarPro;
using DotCharter.Services.Editing.Validation.Rules;
using DotCharter.Services.Export.GuitarPro;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

public sealed class GpDeadNoteSlideScopeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyTiesWithoutADeadSlideKeepMainMuteStates(bool dead)
    {
        var first = new FxNote(0, 5, Dead: dead, PalmMute: !dead);
        var arrangement = ImportLegacy([new FxMeasure().AddVoice(FxBeat.Chord(0, first),
            FxBeat.Chord(0, new FxNote(0, 5, Tied: true)))]);
        var notes = arrangement.Levels[0].Notes;
        Assert.Equal(2, notes.Count);
        Assert.Equal(dead ? MuteType.Full : MuteType.Palm, notes[0].Mute);
        Assert.Equal(MuteType.None, notes[1].Mute);
        Assert.All(notes, n => Assert.Null(n.SlideTo));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(5)]
    [InlineData(7)]
    public void AChordWithATiedStringWithoutADeadSlideKeepsEveryMainString(int originFret)
    {
        var first = originFret < 0 ? FxBeat.RestBeat(0) : FxBeat.Chord(0, new FxNote(0, originFret, Dead: true));
        var arrangement = ImportLegacy([new FxMeasure().AddVoice(first, FxBeat.Chord(0,
            new FxNote(0, 5, Tied: true), new FxNote(1, 7), new FxNote(2, 9)))]);
        var chord = Assert.Single(arrangement.Levels[0].Chords);
        Assert.Equal(new[] { (0, 5, MuteType.None), (1, 7, MuteType.None), (2, 9, MuteType.None) },
            chord.ChordNotes.OrderBy(n => n.StringIndex).Select(n => (n.StringIndex, n.Fret, n.Mute)));
    }

    [Theory]
    [InlineData("gpif", false)]
    [InlineData("gpif", true)]
    [InlineData("gp5", false)]
    [InlineData("gp5", true)]
    public void ThreeDeadTiedNotesAcrossABarKeepOneSlideAttack(string format, bool slideOnFirst)
    {
        Arrangement arrangement;
        if (format == "gpif")
        {
            var xml = XDocument.Parse(PickScrapeScenes.Gpif(
                [new("Half", []), new("Quarter", []), new("Quarter", [new(0, 0, slideOnFirst ? 4 : 0)])],
                [new("Quarter", [new(0, 0)]), new("Quarter", [new(0, 0, slideOnFirst ? 0 : 4)])]));
            var parts = xml.Descendants("Note").ToArray();
            for (var i = 0; i < parts.Length; i++)
            {
                parts[i].Add(new XElement("Tie", new XAttribute("origin", i < 2 ? "true" : "false"),
                    new XAttribute("destination", i > 0 ? "true" : "false")));
                if (!parts[i].Descendants("Property").Any(p => p.Attribute("name")?.Value == "Muted"))
                    parts[i].Element("Properties")!.Add(new XElement("Property", new XAttribute("name", "Muted")));
            }
            arrangement = PickScrapeScenes.Import(xml.ToString());
        }
        else
        {
            arrangement = ImportLegacy([
                new FxMeasure().AddVoice(FxBeat.RestBeat(-1), FxBeat.RestBeat(0),
                    FxBeat.Chord(0, new FxNote(0, 0, Dead: true, Slide: slideOnFirst ? 4 : null))),
                new FxMeasure().AddVoice(FxBeat.Chord(0, new FxNote(0, 0, Tied: true)),
                    FxBeat.Chord(0, new FxNote(0, 0, Tied: true, Slide: slideOnFirst ? null : 4)))]);
        }
        var note = Assert.Single(arrangement.Levels[0].Notes);
        Assert.Equal(new FractionalPosition(3), note.Position);
        Assert.Equal(new FractionalPosition(3), note.Sustain);
        Assert.Equal((12, 5, MuteType.Full, true), (note.Fret, note.SlideTo, note.Mute, note.UnpitchedSlide));
    }

    [Theory]
    [InlineData(true, 4, false)]
    [InlineData(true, 8, false)]
    [InlineData(true, 4, true)]
    [InlineData(true, 8, true)]
    [InlineData(false, 4, false)]
    [InlineData(false, 8, false)]
    [InlineData(false, 4, true)]
    [InlineData(false, 8, true)]
    public void GpifTieMemberWithoutMutedFollowsTheOriginsDeadState(bool dead, int flags, bool slideOnFirst)
    {
        var xml = XDocument.Parse(PickScrapeScenes.Gpif(new PickScrapeScenes.ScrapeBeat[]
        {
            new("Quarter", [new(0, 0, slideOnFirst ? flags : 0)]),
            new("Quarter", [new(0, 0, slideOnFirst ? 0 : flags)])
        }));
        var parts = xml.Descendants("Note").ToArray();
        foreach (var muted in xml.Descendants("Property").Where(p => p.Attribute("name")?.Value == "Muted").ToArray())
            muted.Remove();
        if (dead) parts[0].Element("Properties")!.Add(new XElement("Property", new XAttribute("name", "Muted")));
        parts[0].Add(new XElement("Tie", new XAttribute("origin", "true")));
        parts[1].Add(new XElement("Tie", new XAttribute("destination", "true")));

        var note = Assert.Single(PickScrapeScenes.Import(xml.ToString()).Levels[0].Notes);
        Assert.Equal(dead ? MuteType.Full : MuteType.None, note.Mute);
        Assert.Equal(new FractionalPosition(2), note.Sustain);
        Assert.Equal(dead ? flags == 4 ? 12 : 5 : 0, note.Fret);
        Assert.Equal(dead ? flags == 4 ? 5 : 12 : null, note.SlideTo);
        Assert.Equal(dead, note.UnpitchedSlide);
    }

    [Theory]
    [InlineData(2, 2, 2)]
    [InlineData(3, 3, 3)]
    [InlineData(3, 5, 1)]
    public void ShortChordSlideSkipsItsOwnOnsetToFindTheLaterSound(int sustainTicks, int nextTicks, int expectedTicks)
    {
        var result = new GpImportResult();
        var arrangement = new Arrangement();
        var level = new ChartLevel();
        var slide = new ChordNote
        {
            Fret = 12, SlideTo = -4, Mute = MuteType.Full,
            Sustain = new FractionalPosition(0, new Fraction(sustainTicks, 128))
        };
        var chord = new Chord();
        chord.ChordNotes.Add(slide);
        chord.ChordNotes.Add(new ChordNote { Fret = 7, StringIndex = 1, Mute = MuteType.Full });
        level.Chords.Add(chord);
        level.Notes.Add(new GuitarNote { Fret = 5, StringIndex = 5, Position = new FractionalPosition(0, new Fraction(nextTicks, 128)) });
        arrangement.Levels.Add(level);
        result.Arrangements.Add(arrangement);
        GpDeadNoteSlide.Resolve(level, 0, 24, result.DeadSlideSounds);

        GpDeadNoteSlide.TrimSustains(result);

        Assert.True(slide.Sustain > FractionalPosition.Zero);
        Assert.Equal(new FractionalPosition(0, new Fraction(expectedTicks, 128)), slide.Sustain);
    }

    [Fact]
    public void ShortDeadSlideSkipsEarlierOnsetsAndTrimsBeforeTheLaterSound()
    {
        var result = new GpImportResult();
        var arrangement = new Arrangement();
        var level = new ChartLevel();
        var slide = new GuitarNote
        {
            Fret = 12, SlideTo = -4, Mute = MuteType.Full,
            Position = new FractionalPosition(0, new Fraction(8, 128)),
            Sustain = new FractionalPosition(0, new Fraction(3, 128))
        };
        level.Notes.Add(new GuitarNote { Fret = 7, StringIndex = 1, Position = new FractionalPosition(0, new Fraction(7, 128)) });
        level.Notes.Add(slide);
        level.Notes.Add(new GuitarNote { Fret = 5, StringIndex = 5, Position = new FractionalPosition(0, new Fraction(13, 128)) });
        arrangement.Levels.Add(level);
        result.Arrangements.Add(arrangement);
        GpDeadNoteSlide.Resolve(level, 0, 24, result.DeadSlideSounds);

        GpDeadNoteSlide.TrimSustains(result);

        Assert.Equal(new FractionalPosition(0, new Fraction(1, 128)), slide.Sustain);
    }

    [Theory]
    [InlineData(false, MuteType.Palm)]
    [InlineData(true, MuteType.Full)]
    public void LegacyDeadPalmMuteOnlyChangesWhenThereIsASlideOut(bool slide, MuteType expected)
    {
        var arrangement = ImportLegacy([new FxMeasure().AddVoice(
            FxBeat.Chord(0, new FxNote(0, 12, Dead: true, PalmMute: true, Slide: slide ? 4 : null)))]);
        Assert.Equal(expected, Assert.Single(arrangement.Levels[0].Notes).Mute);
    }

    [Theory]
    [InlineData(1, 0, false)]
    [InlineData(1, 63, true)]
    [InlineData(2, 0, true)]
    [InlineData(2, 1, true)]
    public void OnlySoundsNearTheSlideEndCutItsSustain(int nextBeat, int nextPart, bool cut)
    {
        var next = new FractionalPosition(nextBeat, new Fraction(nextPart, 64));
        var result = Scene(new FractionalPosition(2), next, true);
        GpDeadNoteSlide.TrimSustains(result);
        var expected = cut ? next - new FractionalPosition(0, new Fraction(1, 32)) : new FractionalPosition(2);
        Assert.Equal(expected, result.Arrangements[0].Levels[0].Notes[0].Sustain);
    }

    [Fact]
    public void AnUnplacedMutedUnpitchedSlideKeepsItsSustain()
    {
        var result = Scene(new FractionalPosition(2), new FractionalPosition(2), false);
        GpDeadNoteSlide.TrimSustains(result);
        Assert.Equal(new FractionalPosition(2), result.Arrangements[0].Levels[0].Notes[0].Sustain);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    public void LinkingClearsAnUnresolvedDeadSlideMarkerAndKeepsTheMutedNote(int flags)
    {
        var level = new ChartLevel();
        var xml = XDocument.Parse(PickScrapeScenes.Gpif(new PickScrapeScenes.ScrapeBeat[]
        {
            new("Quarter", [new(0, 0, flags)])
        }));
        level.Notes.Add(new GpNoteParser().ParseNote(xml.Descendants("Note").Single(), FractionalPosition.Zero,
            new FractionalPosition(1), 6, false));
        var note = Assert.Single(level.Notes);
        Assert.True(GpDeadNoteSlide.IsPending(note));
        GpPostProcessor.LinkSlides(level, 6);
        Assert.Null(note.SlideTo);
        Assert.Equal(MuteType.Full, note.Mute);
        Assert.Same(note, Assert.Single(level.Notes));
    }

    [Fact]
    public void MutedUnpitchedFingerChangesUseTheMainRule()
    {
        var arrangement = new Arrangement();
        var level = new ChartLevel();
        level.Notes.Add(new GuitarNote
        {
            Fret = 24, SlideTo = 5, Mute = MuteType.Full,
            UnpitchedSlide = true, Sustain = new FractionalPosition(1)
        });
        level.Anchors.Add(new FretAnchor(FractionalPosition.Zero, 21));
        level.Anchors.Add(new FretAnchor(new FractionalPosition(1), 5));
        arrangement.Levels.Add(level);
        var issues = new List<ArrangementIssue>();
        new NoteRule().Validate(ArrangementValidationContext.Build(arrangement, Beats(120), null), issues);
        Assert.Contains(issues, i => i.Type == ArrangementIssueType.FingerChangeDuringSlide);
    }

    [Theory]
    [InlineData("gpif", 120)]
    [InlineData("gpif", 240)]
    [InlineData("gp5", 120)]
    [InlineData("gp5", 240)]
    public void ImportedDeadSlidePassesTheFullInspectorAtBothTempos(string format, int bpm)
    {
        var arrangement = GpDeadNoteSlideImportTests.Import(format, 4, 0, 5);
        var beats = Beats(bpm);
        var issues = new ArrangementValidationService(new FakeAppLogger()).Validate(arrangement, beats);
        Assert.DoesNotContain(issues, i => i.Anchor != null || i.Note?.UnpitchedSlide == true
            || i.Type == ArrangementIssueType.ValidationRuleCrashed);
    }

    [Theory]
    [InlineData("gpif")]
    [InlineData("gp5")]
    public void ExportReimportKeepsTheBeatGapSustain(string format)
    {
        var arrangement = GpDeadNoteSlideImportTests.Import(format, 4, 0, 5);
        var sustain = arrangement.Levels[0].Notes[0].Sustain;
        Assert.Equal(new FractionalPosition(0, new Fraction(31, 32)), sustain);
        var path = Path.Combine(Path.GetTempPath(), $"dead-slide-gap-{Guid.NewGuid():N}.gp");
        try
        {
            GpExporter.Export(arrangement, arrangement.Levels[0], Beats(120), path);
            var importer = new GpImporter();
            var roundTrip = Assert.Single(importer.ImportTracks(path, [Assert.Single(importer.GetAvailableTracks(path)).Id]));
            Assert.Equal(sustain, roundTrip.Levels[0].Notes[0].Sustain);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static List<BeatMarker> Beats(int bpm) => Enumerable.Range(0, 8).Select(i => new BeatMarker
    {
        Time = 1500 + i * 60000.0 / bpm, Bpm = bpm, BeatsInMeasure = 4, NoteDenominator = 4
    }).ToList();

    [Theory]
    [InlineData("gpif")]
    [InlineData("gp5")]
    public void NotationVoicesUseTheSameBeatGap(string format)
    {
        var arrangement = GpDeadNoteSlideImportTests.Import(format, 4, 0, 5, sidecar: true);
        Assert.Equal(new FractionalPosition(0, new Fraction(31, 32)), Assert.Single(arrangement.NotationVoices).Notes[0].Sustain);
    }

    [Theory]
    [InlineData(120)]
    [InlineData(480)]
    public void TempoMetadataDoesNotChangeTheBeatGap(int bpm)
    {
        var xml = XDocument.Parse(PickScrapeScenes.Gpif(new PickScrapeScenes.ScrapeBeat[]
        {
            new("Quarter", [new(0, 0, 4)]), new("Quarter", [new(2, 5)])
        }));
        xml.Root!.Element("MasterTrack")!.Add(new XElement("Automations", new XElement("Automation",
            new XElement("Type", "Tempo"), new XElement("Bar", 0), new XElement("Position", 0), new XElement("Value", bpm))));
        Assert.Equal(new FractionalPosition(0, new Fraction(31, 32)), PickScrapeScenes.Import(xml.ToString()).Levels[0].Notes[0].Sustain);
    }

    private static GpImportResult Scene(FractionalPosition sustain, FractionalPosition next, bool placed)
    {
        var result = new GpImportResult();
        result.Metadata.Bars.Add(new GpBarInfo());
        var level = new ChartLevel();
        var note = new GuitarNote { Fret = 12, Sustain = sustain, Mute = MuteType.Full, UnpitchedSlide = true, SlideTo = placed ? -4 : 5 };
        level.Notes.Add(note);
        level.Notes.Add(new GuitarNote { Position = next, StringIndex = 5, Fret = 5 });
        var arrangement = new Arrangement();
        arrangement.Levels.Add(level);
        result.Arrangements.Add(arrangement);
        if (placed) GpDeadNoteSlide.Resolve(level, 0, 24, result.DeadSlideSounds);
        return result;
    }

    private static Arrangement ImportLegacy(IReadOnlyList<FxMeasure> measures)
    {
        var path = LegacyGpFixtureBuilder.WriteFile("5.10", measures);
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
}
