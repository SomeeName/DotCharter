using DotCharter.Services.Parsing.Eof;

namespace DotCharter.Tests.Services.Parsing.Eof;

public sealed class EofGraceTechniqueRoundTripTests
{
    private static readonly string[] Techniques =
    [
        "plain", "bend at onset", "bend later", "prebend", "slide", "unpitched slide", "vibrato",
        "tremolo", "harmonic", "palm mute", "hammer on", "pull off", "tap", "accent"
    ];

    [Fact]
    public void Every_unchanged_single_grace_technique_round_trips_at_two_tempos_and_sustains() =>
        VerifyTable(Import);

    internal static void VerifyTable(Func<ChartProject, Arrangement> import)
    {
        var rows = new List<Action>();
        foreach (var bpm in new[] { 100, 200 })
        {
            foreach (var sustain in new[] { 0, 1200 })
            {
                foreach (var technique in Techniques)
                    rows.Add(() => AssertRoundTrip(import, bpm, sustain, technique));
            }
        }
        Assert.Multiple(rows.ToArray());
    }

    [Fact]
    public void The_reported_onset_bend_keeps_every_point_on_the_grace_target()
    {
        var project = Project(100, 1200, "bend at onset");
        var source = project.Arrangements[0].Levels[0].Notes[0];
        var actual = Assert.Single(Import(project).Levels[0].Notes);
        Assert.Equal(5, actual.GraceFret);
        Assert.Equal(7, actual.Fret);
        AssertCurve(source.BendValues, actual.BendValues, project.Beats);
        Assert.Equal(600, actual.Position.GetPositionMs(project.Beats));
        Assert.Equal(1800, actual.EndPosition.GetPositionMs(project.Beats));
    }

    [Fact]
    public void Bend_kind_edits_follow_main_while_slide_overrides_still_veto()
    {
        var mutations = new Action<List<EofRawProGuitarNote>>[]
        {
            notes => notes[0].Frets[0] = 5,
            notes => notes[0].LengthMs = 2,
            notes => notes[0].Flags |= EofConstants.PgFlagSlideUp,
            notes => notes[0].PositionMs = 599,
            notes => notes[0].BendStrength = 1,
            notes => notes[0].ExtendedFlags = 1,
            notes => notes.Add(new EofRawProGuitarNote
            {
                PositionMs = 630, LengthMs = 1, NoteBitmask = 1,
                Flags = EofConstants.PgFlagRsNotation | EofConstants.PgFlagSlideUp, SlideEndFret = 9
            })
        };
        for (var i = 0; i < mutations.Length; i++)
        {
            var project = Project(100, 1200, "bend at onset");
            var built = EofNoteBuilder.Build(project.Arrangements[0], project.Beats);
            var tech = built.SelectMany(n => n.BendTechNotes).ToList();
            mutations[i](tech);
            var arrangement = ParseTrack(built.Select(n => n.Raw).ToList(), tech, project.Beats);
            var veto = i is 2 or 6; // Both rows add a slide override, not merely bend data.
            Assert.Equal(veto ? 2 : 1, arrangement.Levels[0].Notes.Count);
            Assert.Equal(veto ? 0 : 1, arrangement.Levels[0].Notes.Count(n => n.GraceFret != null));
        }
    }

    internal static ChartProject Project(int bpm, int sustain, string technique)
    {
        var project = new ChartProject();
        for (var i = 0; i < 16; i++) project.Beats.Add(new BeatMarker { Time = i * 60000.0 / bpm });
        FractionalPosition At(int ms) => TimelineMath.PositionFromImportedMs(ms, project.Beats);
        var note = new GuitarNote
        {
            StringIndex = 0, Fret = 7, GraceFret = 5, Position = At(600), Sustain = At(600 + sustain) - At(600)
        };
        switch (technique)
        {
            case "bend at onset":
                note.BendValues.Add(new BendValue(At(600), 1));
                note.BendValues.Add(new BendValue(At(900), 2));
                note.BendValues.Add(new BendValue(At(1200), 0));
                break;
            case "bend later":
                note.BendValues.Add(new BendValue(At(900), 1));
                note.BendValues.Add(new BendValue(At(1200), 2));
                note.BendValues.Add(new BendValue(At(1500), 0));
                break;
            case "prebend": note.BendValues.Add(new BendValue(At(600), 1)); break;
            case "slide": note.SlideTo = 9; break;
            case "unpitched slide": note.SlideTo = 9; note.UnpitchedSlide = true; break;
            case "vibrato": note.Vibrato = true; note.VibratoStrength = 80; break;
            case "tremolo": note.Tremolo = true; break;
            case "harmonic": note.Harmonic = HarmonicType.Normal; break;
            case "palm mute": note.Mute = MuteType.Palm; break;
            case "hammer on": note.Hopo = HopoType.HammerOn; break;
            case "pull off": note.Hopo = HopoType.PullOff; break;
            case "tap": note.Hopo = HopoType.Tap; break;
            case "accent": note.Accent = true; break;
        }
        project.Arrangements.Add(new Arrangement { Name = "Lead", Levels = { new ChartLevel { Notes = { note } } } });
        return project;
    }

    private static void AssertRoundTrip(Func<ChartProject, Arrangement> import, int bpm, int sustain, string technique)
    {
        var project = Project(bpm, sustain, technique);
        var original = project.Arrangements[0].Levels[0].Notes[0];
        var sounds = import(project).Levels.SelectMany(l => l.Notes).ToList();
        Assert.True(sounds.Count == 1, $"{bpm} BPM / sustain {sustain} / {technique}: {sounds.Count} sounds");
        var actual = sounds[0];
        Assert.Equal(original.GraceFret, actual.GraceFret);
        Assert.Equal(original.Fret, actual.Fret);
        Assert.Equal(original.StringIndex, actual.StringIndex);
        Assert.Equal((original.SlideTo, original.UnpitchedSlide, original.Vibrato, original.VibratoStrength,
                original.Tremolo, original.Harmonic, original.Mute, original.Hopo, original.Accent,
                original.LinkNext, original.BassPicking, original.Ignore, original.PassOtherNotes),
            (actual.SlideTo, actual.UnpitchedSlide, actual.Vibrato, actual.VibratoStrength,
                actual.Tremolo, actual.Harmonic, actual.Mute, actual.Hopo, actual.Accent,
                actual.LinkNext, actual.BassPicking, actual.Ignore, actual.PassOtherNotes));
        Assert.InRange(Math.Abs(actual.Position.GetPositionMs(project.Beats) - 600), 0, 1);
        // A zero-sustain target keeps sustain zero: the ornament is not folded onto it (#643).
        var end = 600 + sustain;
        Assert.InRange(Math.Abs(actual.EndPosition.GetPositionMs(project.Beats) - end), 0, 1);
        if (sustain == 0 && technique == "bend at onset")
        {
            AssertCurve([original.BendValues[0]], actual.BendValues, project.Beats);
        }
        else if (sustain == 0 && technique == "bend later")
        {
            AssertCurve([new BendValue(original.Position, 0),
                new BendValue(TimelineMath.PositionFromImportedMs(end, project.Beats), 2)], actual.BendValues, project.Beats);
        }
        else
        {
            AssertCurve(original.BendValues, actual.BendValues, project.Beats);
        }
    }

    private static void AssertCurve(IReadOnlyList<BendValue> expected, IReadOnlyList<BendValue> actual, IList<BeatMarker> beats)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].Step, actual[i].Step);
            Assert.InRange(Math.Abs(expected[i].Position.GetPositionMs(beats) - actual[i].Position.GetPositionMs(beats)), 0, 1);
        }
    }

    private static Arrangement Import(ChartProject project)
    {
        var bytes = EofExporter.Export(project).Bytes;
        var path = Path.Combine(Path.GetTempPath(), "dc-eof-grace-tech-" + Guid.NewGuid().ToString("N") + ".eof");
        File.WriteAllBytes(path, bytes);
        try
        {
            return Assert.Single(new EofImporter().ImportTracksWithMetadata(path, [9]).Arrangements);
        }
        finally
        {
            File.Delete(path);
        }
    }

    internal static Arrangement ParseTrack(List<EofRawProGuitarNote> notes, List<EofRawProGuitarNote> tech, IList<BeatMarker> beats)
    {
        var writer = TrackBytes(notes, tech);
        return EofProGuitarTrackParser.Parse(new EofPackReader(writer), new EofTrackInfo { Id = 9, Name = "Lead" }, beats);
    }

    internal static byte[] TrackBytes(List<EofRawProGuitarNote> notes, List<EofRawProGuitarNote> tech)
    {
        var writer = new EofPackWriter();
        writer.WriteU8(24);
        writer.WriteU8(6);
        for (var i = 0; i < 6; i++) writer.WriteU8(0);
        writer.WriteU32((uint)notes.Count);
        foreach (var note in notes) EofProGuitarNoteWriter.Write(writer, note);
        writer.WriteU16(0);
        writer.WriteU32(1);
        var payload = new EofPackWriter();
        payload.WriteU32((uint)tech.Count);
        foreach (var note in tech) EofProGuitarNoteWriter.Write(payload, note);
        writer.WriteU32((uint)(4 + payload.Length));
        writer.WriteU32(EofConstants.PgCustomTechNotes);
        writer.WriteBytes(payload.ToArray());
        return writer.ToArray();
    }
}
