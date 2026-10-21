using DotCharter.Core.Domain.Vocals;
using DotCharter.Services.Parsing.Eof;
using static DotCharter.Tests.Services.Parsing.Eof.EofExportTestCharts;

namespace DotCharter.Tests.Services.Parsing.Eof;

/// <summary>
///     #638: the EOF export names WHAT it approximated or dropped, not just how many. Every
///     <see cref="EofLoss" /> is reachable from a real chart and comes back with its place, and
///     <see cref="EofLossSummary" /> groups the notes by kind, counts each place once across the
///     difficulty ladder, puts the dropped kinds first and caps each list.
/// </summary>
public sealed class EofLossSummaryTests
{
    [Fact]
    public void AChartWithEveryLoss_ReportsEachKind_AndNamesItsPlace()
    {
        var report = EofExporter.Export(EofLossCharts.EveryLoss()).Report;

        var missing = Enum.GetValues<EofLoss>().Where(loss => report.Notes.All(n => n.Loss != loss)).ToList();
        Assert.True(missing.Count == 0, "never reported: " + string.Join(", ", missing));

        // A loss at one place in the song carries that place; the whole-arrangement and
        // whole-song ones say what they are about instead.
        var timed = new[]
        {
            EofLoss.MeasureStart, EofLoss.LyricPitch, EofLoss.PartialChordTremolo, EofLoss.AnchorOnCapo,
            EofLoss.AnchorWidth, EofLoss.HandShapeShape, EofLoss.HandShapeGhostGem, EofLoss.NoteStringOutOfRange, EofLoss.ChordStringOutOfRange,
            EofLoss.GraceNote, EofLoss.ChordGraceNote, EofLoss.ChordPartialIgnore, EofLoss.ChordMixedHopo,
            EofLoss.ChordMixedHarmonic, EofLoss.ChordMixedSlapPop, EofLoss.ChordMixedVibrato, EofLoss.ChordMixedLinkNext,
            EofLoss.ChordMixedSustain, EofLoss.ChordMixedPalmMute, EofLoss.ChordMixedSlide, EofLoss.ChordMixedBend,
            EofLoss.VibratoStrength, EofLoss.FretClamped, EofLoss.SlideTargetOutOfRange, EofLoss.SlideFromOpenString,
            EofLoss.SlideToSameFret, EofLoss.SlideOutOfRange, EofLoss.FlatBend, EofLoss.BendRounded
        };
        foreach (var loss in timed)
            Assert.All(report.Notes.Where(n => n.Loss == loss), n => Assert.NotNull(n.TimeMs));

        Assert.Contains(report.Notes, n => n.Loss == EofLoss.PartialChordTremolo && n.Subject == "Lead" && n.TimeMs == 6000);
        Assert.Contains(report.Notes, n => n.Loss == EofLoss.MeasureStart && n.Subject == EofLosses.BeatsSubject && n.TimeMs == 2500);
        Assert.Contains(report.Notes, n => n.Loss == EofLoss.CentOffset && n.Subject == "Lead" && n.Detail == "+12");
        Assert.Contains(report.Notes, n => n.Loss == EofLoss.UnusedTone && n.Detail == "unused_tone");
        Assert.Contains(report.Notes, n => n.Loss == EofLoss.UnplacedPhrase && n.Detail == "never_placed");
        Assert.Contains(report.Notes, n => n.Loss == EofLoss.StartingToneReplaced && n.Subject == "Rhythm" && n.Detail == "clean → dist");
        Assert.Contains(report.Notes, n => n.Loss == EofLoss.BendRounded && n.Detail == "1.3 → 1.5");
        Assert.Contains(report.Notes, n => n.Loss == EofLoss.LyricPitch && n.Subject == EofLosses.VocalsSubject && n.Detail.Contains("ooh"));
        Assert.Contains(report.Notes, n => n.Loss == EofLoss.NoFreeSlot && n.Subject == "Extra");
        Assert.Contains(report.Notes, n => n.Loss == EofLoss.BassInGuitarSlot && n.Subject == "Bass 3");
    }

    [Fact]
    public void TheSummary_HasOneGroupPerKind_DroppedFirst_ThenTheMostFrequent()
    {
        var summary = EofLossSummary.From(EofExporter.Export(EofLossCharts.EveryLoss()).Report);

        Assert.Equal(Enum.GetValues<EofLoss>().Length, summary.Groups.Count);
        Assert.Equal(summary.Groups.Count, summary.Groups.Select(g => g.Loss).Distinct().Count());
        var firstApproximated = summary.Groups.ToList().FindIndex(g => g.Kind == EofConversionKind.Approximated);
        Assert.True(firstApproximated > 0);
        Assert.All(summary.Groups.Take(firstApproximated), g => Assert.Equal(EofConversionKind.Dropped, g.Kind));
        Assert.All(summary.Groups.Skip(firstApproximated), g => Assert.Equal(EofConversionKind.Approximated, g.Kind));
        for (var i = 1; i < firstApproximated; i++) Assert.True(summary.Groups[i - 1].Count >= summary.Groups[i].Count);
        Assert.Equal(summary.Total, summary.Dropped + summary.Approximated);
        Assert.Contains("UnusedTone×1", summary.LogLine(), StringComparison.Ordinal);
    }

    /// <summary>The writer reports a note once per level that holds it; the list counts the place once.</summary>
    [Fact]
    public void TheSameNoteOnEveryLevel_IsOnePlace()
    {
        var project = EofLossCharts.Grid(8);
        var lead = new Arrangement { Name = "Lead" };
        for (var d = 0; d < 4; d++)
        {
            var level = new ChartLevel { DifficultyIndex = d };
            level.Notes.Add(new GuitarNote { Position = Pos(2), StringIndex = 1, Fret = 5, Vibrato = true, VibratoStrength = 120 });
            lead.Levels.Add(level);
        }

        project.Arrangements.Add(lead);
        var report = EofExporter.Export(project).Report;
        var summary = EofLossSummary.From(report);

        Assert.Equal(4, report.Notes.Count(n => n.Loss == EofLoss.VibratoStrength));
        var group = Assert.Single(summary.Groups);
        var place = Assert.Single(group.Places);
        Assert.Equal(new EofLossPlace("Lead", 1000, "120"), place);
    }

    [Fact]
    public void AGroupListsItsFirstPlaces_AndCountsTheRest()
    {
        var report = new EofConversionReport();
        for (var i = 7; i >= 0; i--)
            report.Add(EofConversionKind.Approximated, EofLoss.AnchorWidth, "Lead", "m", i * 1000.0, "3");
        report.Add(EofConversionKind.Dropped, EofLoss.UnusedTone, "Lead", "m", detail: "clean");

        var summary = EofLossSummary.From(report);
        var widths = summary.Groups[1];

        Assert.Equal(EofLoss.UnusedTone, summary.Groups[0].Loss);
        Assert.Equal(8, widths.Count);
        Assert.Equal(EofLossSummary.DefaultPlacesPerGroup, widths.Shown.Count);
        Assert.Equal(3, widths.Hidden);
        Assert.Equal([0.0, 1000, 2000, 3000, 4000], widths.Shown.Select(p => p.TimeMs!.Value));
        Assert.Equal(9, summary.Total);

        var all = EofLossSummary.From(report, placesPerGroup: 8).Groups[1];
        Assert.Equal(0, all.Hidden);
    }

    [Fact]
    public void ALosslessExport_SummarisesToNothing()
    {
        var summary = EofLossSummary.From(EofExporter.Export(BuildRepresentativeProject()).Report);

        Assert.True(summary.IsLossless);
        Assert.Equal(0, summary.Total);
        Assert.Empty(summary.LogLine());
    }
}

/// <summary>Charts for the #638 loss report.</summary>
internal static class EofLossCharts
{
    /// <summary>A 4/4 grid at 120 BPM: a beat every 500 ms, a bar every four.</summary>
    public static ChartProject Grid(int beats)
    {
        var project = new ChartProject();
        for (var i = 0; i < beats; i++)
            project.Beats.Add(new BeatMarker { Time = i * 500.0, Bpm = 120.0, IsMeasure = i % 4 == 0 });
        return project;
    }

    /// <summary>A chart that makes the EOF writer report every <see cref="EofLoss" /> at least once.</summary>
    public static ChartProject EveryLoss()
    {
        var project = Grid(64);
        project.Beats[5].IsMeasure = true; // a bar line EOF cannot derive from 4/4

        project.Arrangements.Add(new Arrangement { Name = "Voice", ArrangementType = ArrangementType.Vocal });
        for (var i = 1; i <= 3; i++)
            project.Arrangements.Add(new Arrangement { Name = $"Bass {i}", ArrangementType = ArrangementType.Bass, StringCount = 4 });
        project.Arrangements.Add(Lead());
        project.Arrangements.Add(Rhythm());
        project.Arrangements.Add(new Arrangement { Name = "Extra", ArrangementType = ArrangementType.Lead });

        project.VocalTracks.Add(Vocals("Lead vocal", 4, ("ooh", 200)));
        project.VocalTracks.Add(Vocals("Harmony", 8, ("aah", 64)));
        return project;
    }

    private static Arrangement Lead()
    {
        var lead = new Arrangement
        {
            Name = "Lead", ArrangementType = ArrangementType.Lead, StringCount = 6, Capo = 2, CentOffset = 12,
            StartingTone = "ghost"
        };
        for (var s = 0; s < 4; s++) lead.TuningPitches.Add(new TuningOffset(0));
        lead.ToneNames.Add("unused_tone");
        lead.Phrases.Add("never_placed");
        lead.PhraseInfos["capped"] = new PhraseInfo(5, false);
        lead.ChordTemplates.Add(new ChordTemplate { Id = 0, Name = "A", Frets = new Dictionary<int, int> { { 0, 5 }, { 1, 7 } } });

        var level = new ChartLevel();
        level.Notes.Add(new GuitarNote { Position = Pos(1), StringIndex = 8, Fret = 3 });
        level.Notes.Add(new GuitarNote { Position = Pos(2), StringIndex = 1, Fret = 5, GraceFret = 5 });
        level.Notes.Add(new GuitarNote { Position = Pos(3), StringIndex = 1, Fret = 5, Vibrato = true, VibratoStrength = 120 });
        level.Notes.Add(new GuitarNote { Position = Pos(4), StringIndex = 1, Fret = 1 });
        level.Notes.Add(new GuitarNote { Position = Pos(5), StringIndex = 1, Fret = 5, SlideTo = 200, UnpitchedSlide = true });
        level.Notes.Add(new GuitarNote { Position = Pos(6), StringIndex = 1, Fret = 0, SlideTo = 5 });
        level.Notes.Add(new GuitarNote { Position = Pos(7), StringIndex = 1, Fret = 5, SlideTo = 5 });
        level.Notes.Add(new GuitarNote { Position = Pos(8), StringIndex = 1, Fret = 5, SlideTo = 200 });
        level.Notes.Add(new GuitarNote
        {
            Position = Pos(9), StringIndex = 1, Fret = 5, BendValues = { new BendValue(Pos(9), 0) }
        });
        level.Notes.Add(new GuitarNote
        {
            Position = Pos(10), Sustain = Pos(1), StringIndex = 1, Fret = 5,
            BendValues = { new BendValue(Pos(10), 0), new BendValue(Pos(11), 1.3) }
        });

        level.Chords.Add(Chord(12, new ChordNote { StringIndex = 0, Fret = 5, Tremolo = true }, new ChordNote { StringIndex = 1, Fret = 7 }));
        level.Chords.Add(Chord(13, new ChordNote { StringIndex = 0, Fret = 5 }, new ChordNote { StringIndex = 8, Fret = 7 }));
        level.Chords.Add(Chord(14, new ChordNote { StringIndex = 0, Fret = 5, GraceFret = 3 },
            new ChordNote { StringIndex = 1, Fret = 7, GraceFret = 6 }));
        level.Chords.Add(Chord(15, new ChordNote { StringIndex = 0, Fret = 5, Ignore = true }, new ChordNote { StringIndex = 1, Fret = 7 }));
        level.Chords.Add(Chord(16,
            new ChordNote
            {
                StringIndex = 0, Fret = 5, Hopo = HopoType.HammerOn, Harmonic = HarmonicType.Normal,
                BassPicking = BassPickingType.Slap, Vibrato = true, VibratoStrength = 80, LinkNext = true,
                Sustain = Pos(1), SlideTo = 7, BendValues = { new BendValue(Pos(16), 1.0) }
            },
            new ChordNote { StringIndex = 1, Fret = 7, SlideTo = 10, UnpitchedSlide = true, Mute = MuteType.Palm }));

        level.Anchors.Add(new FretAnchor(Pos(1), 2));
        level.Anchors.Add(new FretAnchor(Pos(2), 7, 3));
        level.HandShapes.Add(new HandShape { Position = Pos(30), EndPosition = Pos(31), TemplateId = 0 });

        // A phrase on a note between two beats: written as moveR1 on beat 20.
        var betweenBeats = new FractionalPosition(20, new Fraction(1, 2));
        level.Notes.Add(new GuitarNote { Position = betweenBeats, StringIndex = 1, Fret = 5 });
        lead.EventPoints.Add(new EventPoint { Position = betweenBeats, Phrase = "bridge" });
        // A grip holding a ghost gem over no sound: nothing in EOF can carry it (#795).
        lead.ChordTemplates.Add(new ChordTemplate { Id = 1, Frets = { [2] = 5 }, GhostMask = ChordTemplate.GhostBit(2) });
        level.HandShapes.Add(new HandShape { Position = Pos(32), EndPosition = Pos(33), TemplateId = 1 });
        lead.Levels.Add(level);
        return lead;
    }

    private static Arrangement Rhythm()
    {
        var rhythm = new Arrangement { Name = "Rhythm", ArrangementType = ArrangementType.Rhythm, StartingTone = "clean" };
        rhythm.ToneChanges.Add(new ToneChange { Position = Pos(0), ToneName = "dist" });
        rhythm.ToneNames.Add("dist");
        rhythm.Levels.Add(new ChartLevel());
        return rhythm;
    }

    private static Chord Chord(int beat, params ChordNote[] notes)
    {
        var chord = new Chord { Position = Pos(beat), TemplateId = 0 };
        foreach (var note in notes) chord.ChordNotes.Add(note);
        return chord;
    }

    private static VocalTrack Vocals(string name, int beat, (string Text, int Note) lyric)
    {
        var track = new VocalTrack { Name = name };
        var phrase = new LyricPhrase { Position = Pos(beat), EndPosition = Pos(beat + 2) };
        phrase.Lyrics.Add(new LyricEvent
        {
            Position = Pos(beat), EndPosition = Pos(beat + 1), Text = lyric.Text, Note = (byte)lyric.Note, IsPhraseEnd = true
        });
        track.Phrases.Add(phrase);
        return track;
    }
}
