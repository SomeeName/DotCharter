using DotCharter.Services.Parsing.Rscp;

namespace DotCharter.Tests.Services.Parsing;

/// <summary>
/// The notation-voice sidecar (<c>dcNotationVoices</c> root extension node) and
/// the before-beat grace hint (<c>gf</c> attribute): backward-tolerant v3
/// additions. Voices 2–4 of a multi-voice score import live per ARRANGEMENT as
/// notation-only data — the playable chart stays single-voice — and must
/// survive save/load exactly; files without the node (every pre-voice project)
/// must load with empty sidecars, byte-identical write output included.
/// </summary>
public sealed class RscpNotationVoiceTests
{
    private static ChartProject NewProject()
    {
        var project = new ChartProject();
        for (var i = 0; i < 8; i++)
            project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120 });
        var arr = new Arrangement { Name = "Lead" };
        arr.Levels.Add(new ChartLevel());
        project.Arrangements.Add(arr);
        return project;
    }

    private static ChartProject RoundTrip(ChartProject project)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcharter-nvtest-{Guid.NewGuid():N}.rscp");
        try
        {
            var serializer = new RscpChartSerializer();
            serializer.Save(path, project);
            return serializer.Load(path);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void SidecarVoices_SurviveSaveLoad_WithHintsAndTechniques()
    {
        var project = NewProject();
        var arr = project.Arrangements[0];
        arr.Levels[0].Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(0), StringIndex = 4, Fret = 5,
            NotationDuration = new Fraction(1, 2)
        });

        var voice = new NotationVoice { VoiceIndex = 1 };
        voice.Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(0), StringIndex = 0, Fret = 3,
            Sustain = new FractionalPosition(2),
            NotationDuration = new Fraction(2, 1),
            Vibrato = true
        });
        var chord = new Chord
        {
            Position = new FractionalPosition(2),
            NotationDuration = new Fraction(1, 1)
        };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 3 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 5 });
        voice.Chords.Add(chord);
        arr.NotationVoices.Add(voice);

        var loaded = RoundTrip(project);
        var loadedArr = loaded.Arrangements[0];

        // The playable chart stays what it was.
        Assert.Equal(new Fraction(1, 2), Assert.Single(loadedArr.Levels[0].Notes).NotationDuration);

        var lv = Assert.Single(loadedArr.NotationVoices);
        Assert.Equal(1, lv.VoiceIndex);
        var ln = Assert.Single(lv.Notes);
        Assert.Equal(new FractionalPosition(0), ln.Position);
        Assert.Equal(0, ln.StringIndex);
        Assert.Equal(3, ln.Fret);
        Assert.Equal(new FractionalPosition(2), ln.Sustain);
        Assert.Equal(new Fraction(2, 1), ln.NotationDuration);
        Assert.True(ln.Vibrato);
        var lc = Assert.Single(lv.Chords);
        Assert.Equal(new FractionalPosition(2), lc.Position);
        Assert.Equal(new Fraction(1, 1), lc.NotationDuration);
        Assert.Equal(2, lc.ChordNotes.Count);
    }

    /// <summary>
    ///     The headline loss this file used to miss by counting: a sidecar chord
    ///     came back with EVERY fret 0.
    ///     <para>
    ///         An rscp <c>chordNote</c> carries no fret — a chord's frets live in
    ///         its <c>ChordTemplate</c>. <c>RscpTemplateManager.RegenerateTemplates</c>
    ///         walks <c>arr.Levels</c> only, so a notation voice's chords keep the
    ///         <c>-1</c> "untemplated" sentinel forever and had nowhere to put
    ///         their frets. A multi-voice Guitar Pro import therefore lost every
    ///         voice-2/3/4 chord shape on the FIRST save, and the sheet strip
    ///         redrew the sidecar as an open strum.
    ///     </para>
    ///     <para>
    ///         Asserts the FRETS, per string. The pre-existing round trip above
    ///         asserted <c>ChordNotes.Count</c>, which two chords with completely
    ///         different shapes both satisfy — the counting habit that shipped
    ///         fourteen rscp fields as silent data loss.
    ///     </para>
    /// </summary>
    [Fact]
    public void SidecarChordFrets_SurviveASave_PerString()
    {
        var project = NewProject();
        var arr = project.Arrangements[0];

        var voice = new NotationVoice { VoiceIndex = 2 };
        var chord = new Chord { Position = new FractionalPosition(1) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 12 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 14, Sustain = new FractionalPosition(2) });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 3, Fret = 0 }); // a real OPEN string
        voice.Chords.Add(chord);
        arr.NotationVoices.Add(voice);

        var loaded = RoundTrip(project);

        var lc = Assert.Single(Assert.Single(loaded.Arrangements[0].NotationVoices).Chords);
        var byString = lc.ChordNotes.ToDictionary(n => n.StringIndex, n => n.Fret);
        Assert.Equal(12, byString[0]);
        Assert.Equal(14, byString[1]);
        Assert.Equal(0, byString[3]);
        // The rest of the note survived with it, so this is not an accidental
        // pass off a chord that happens to hold three zeroes.
        Assert.Equal(new FractionalPosition(2),
            lc.ChordNotes.Single(n => n.StringIndex == 1).Sustain);
    }

    /// <summary>
    ///     The additive half of the rule: the fret attribute exists ONLY where
    ///     nothing else can carry the shape. Every level chord is templated by
    ///     the save-time regeneration, so a single-voice chart's bytes are
    ///     unchanged — and the sidecar's chord notes are the ones that grow it.
    /// </summary>
    [Fact]
    public void ALevelChordNeverWritesAFret_OnlyAnUntemplatedSidecarChordDoes()
    {
        var project = NewProject();
        var arr = project.Arrangements[0];
        var levelChord = new Chord { Position = new FractionalPosition(0), TemplateId = -1 };
        levelChord.ChordNotes.Add(new ChordNote { StringIndex = 2, Fret = 9 });
        levelChord.ChordNotes.Add(new ChordNote { StringIndex = 3, Fret = 9 });
        arr.Levels[0].Chords.Add(levelChord);

        var path = Path.Combine(Path.GetTempPath(), $"dotcharter-nvtest-{Guid.NewGuid():N}.rscp");
        try
        {
            new RscpChartSerializer().Save(path, project);
            Assert.DoesNotContain("fret=", File.ReadAllText(path));

            // …and the level chord still round-trips its shape, through the
            // template the save minted for it.
            var reloaded = new RscpChartSerializer().Load(path);
            var rc = Assert.Single(reloaded.Arrangements[0].Levels[0].Chords);
            Assert.Equal(new[] { 9, 9 }, rc.ChordNotes.OrderBy(n => n.StringIndex).Select(n => n.Fret));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }

        // The same chord under a notation voice has no template to hold the
        // frets, so there the attribute must appear.
        var voiced = NewProject();
        var v = new NotationVoice { VoiceIndex = 1 };
        v.Chords.Add(levelChord.Clone());
        voiced.Arrangements[0].NotationVoices.Add(v);

        var voicedPath = Path.Combine(Path.GetTempPath(), $"dotcharter-nvtest-{Guid.NewGuid():N}.rscp");
        try
        {
            new RscpChartSerializer().Save(voicedPath, voiced);
            Assert.Contains("fret=\"9\"", File.ReadAllText(voicedPath));
        }
        finally
        {
            if (File.Exists(voicedPath)) File.Delete(voicedPath);
        }
    }

    [Fact]
    public void MultipleVoices_KeepTheirIndices()
    {
        var project = NewProject();
        var arr = project.Arrangements[0];
        foreach (var vi in new[] { 1, 2, 3 })
        {
            var v = new NotationVoice { VoiceIndex = vi };
            v.Notes.Add(new GuitarNote { Position = new FractionalPosition(vi), StringIndex = 0, Fret = vi });
            arr.NotationVoices.Add(v);
        }

        var loaded = RoundTrip(project);

        var voices = loaded.Arrangements[0].NotationVoices.OrderBy(v => v.VoiceIndex).ToList();
        Assert.Equal(new[] { 1, 2, 3 }, voices.Select(v => v.VoiceIndex));
        Assert.Equal(new[] { 1, 2, 3 }, voices.Select(v => v.Notes.Single().Fret));
    }

    [Fact]
    public void GraceFret_SurvivesSaveLoad_AndAbsenceStaysNull()
    {
        var project = NewProject();
        var level = project.Arrangements[0].Levels[0];
        level.Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(0), StringIndex = 2, Fret = 7, GraceFret = 5
        });
        level.Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(1), StringIndex = 2, Fret = 8 // no grace
        });

        var loaded = RoundTrip(project);

        var notes = loaded.Arrangements[0].Levels[0].Notes.OrderBy(n => n.Position).ToList();
        Assert.Equal(5, notes[0].GraceFret);
        Assert.Null(notes[1].GraceFret);
    }

    [Fact]
    public void FilesWithoutTheNode_LoadWithEmptySidecars_AndWriteNoNode()
    {
        // The exact shape every pre-voice save has: no dcNotationVoices node.
        var project = NewProject();
        project.Arrangements[0].Levels[0].Notes.Add(
            new GuitarNote { Position = new FractionalPosition(0), StringIndex = 0, Fret = 3 });

        var path = Path.Combine(Path.GetTempPath(), $"dotcharter-nvtest-{Guid.NewGuid():N}.rscp");
        try
        {
            var serializer = new RscpChartSerializer();
            serializer.Save(path, project);
            var text = File.ReadAllText(path);
            // A chart without sidecar voices must not grow the node — old files
            // stay byte-identical with previous writer output.
            Assert.DoesNotContain("dcNotationVoices", text);
            Assert.DoesNotContain("gf=", text);

            var loaded = serializer.Load(path);
            Assert.Empty(loaded.Arrangements[0].NotationVoices);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void CloneAndCopyFrom_CarryVoicesAndGrace()
    {
        // ChartProjectCloner snapshots (undo/redo, saves) go through the clone
        // graph — a field missed there silently vanishes from history.
        var arr = new Arrangement();
        var voice = new NotationVoice { VoiceIndex = 2 };
        voice.Notes.Add(new GuitarNote { Fret = 3, GraceFret = 1, NotationDuration = new Fraction(1, 2) });
        var chord = new Chord { NotationDuration = new Fraction(1, 1) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 2 });
        voice.Chords.Add(chord);
        arr.NotationVoices.Add(voice);

        var cloned = ChartProjectCloner.CloneArrangement(arr);

        var cv = Assert.Single(cloned.NotationVoices);
        Assert.Equal(2, cv.VoiceIndex);
        var cn = Assert.Single(cv.Notes);
        Assert.Equal(3, cn.Fret);
        Assert.Equal(1, cn.GraceFret);
        Assert.Equal(new Fraction(1, 2), cn.NotationDuration);
        Assert.NotSame(voice.Notes[0], cn);
        Assert.Single(Assert.Single(cv.Chords).ChordNotes);

        // GuitarNote.Clone carries the grace hint on its own too.
        Assert.Equal(1, new GuitarNote { GraceFret = 1 }.Clone().GraceFret);
    }
}
