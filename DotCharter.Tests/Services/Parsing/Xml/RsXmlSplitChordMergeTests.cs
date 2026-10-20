using System.Xml.Linq;
using DotCharter.Services.Editing.Collisions;
using DotCharter.Services.Parsing.Xml;
using DotCharter.Services.Parsing.Xml.Song;

namespace DotCharter.Tests.Services.Parsing.Xml;

/// <summary>
///     The RS-XML import gives back a chord marked <see cref="Chord.SplitIntoNotes" />
///     from the single notes EOF (and DotCharter) write for one (#609, #621): two or
///     more notes at one time, on different strings, with no chord there, inside no
///     hand shape and with no link landing on them (<c>RsXmlSplitChordMerge</c>). A
///     split chord counts as its notes in every export decision, so the merged
///     arrangement still writes back as exactly the document the loose notes write.
///     Every scene is a file built in code and read twice: once by the import (merge
///     on) and once as a DotCharter v2 project's own arrangement is read (merge off).
/// </summary>
public sealed class RsXmlSplitChordMergeTests
{
    // ── what merges ─────────────────────────────────────────────────────────

    /// <summary>
    ///     An A5 strum, then the same two frets as two loose notes, each with a
    ///     technique of its own: they come back as ONE split chord playing the
    ///     file's A5, every technique on its own string, and the file writes back
    ///     unchanged — before AND after the save-time pass.
    /// </summary>
    [Fact]
    public void SimultaneousNotes_OnADeclaredShape_BecomeOneSplitChord_AndWriteBackIdentically()
    {
        var file = Scene(level =>
        {
            level.Chords.Add(Strum(At(1), (0, 5), (1, 7)));
            level.Notes.Add(new GuitarNote { Position = At(2), StringIndex = 0, Fret = 5, Sustain = Beats(1), Vibrato = true, VibratoStrength = 80 });
            level.Notes.Add(new GuitarNote { Position = At(2), StringIndex = 1, Fret = 7, Sustain = Beats(1), SlideTo = 9 });
        });

        var merged = Import(file);
        var loose = ReadWithoutMerge(file);

        var level = Assert.Single(Assert.Single(merged.Arrangements).Levels);
        Assert.Empty(level.Notes);
        var split = Assert.Single(level.Chords, c => c.SplitIntoNotes);
        Assert.Equal(0, split.TemplateId);
        Assert.Equal([0, 1], split.ChordNotes.Select(n => n.StringIndex));
        Assert.True(split.ChordNotes[0].Vibrato);
        Assert.Equal(9, split.ChordNotes[1].SlideTo);
        Assert.Equal(2, merged.Arrangements[0].ChordTemplates.Count); // nothing minted

        Assert.Equal(2, Assert.Single(loose.Arrangements[0].Levels).Notes.Count);
        Assert.Equal(Written(loose), Written(merged));
        Assert.Equal(NoteLines(file), NoteLines(Write(merged)));

        // A save runs ChartSpacingEnforcer over the chart: the chord and the loose
        // notes must still come out the same.
        ChartSpacingEnforcer.EnforceProject(merged, new AppConfig());
        ChartSpacingEnforcer.EnforceProject(loose, new AppConfig());
        Assert.Equal(Written(loose), Written(merged));
    }

    [Fact]
    public void BothImportPaths_Merge()
    {
        var file = Scene(level =>
        {
            level.Chords.Add(Strum(At(1), (0, 5), (1, 7)));
            level.Notes.Add(new GuitarNote { Position = At(2), StringIndex = 0, Fret = 5 });
            level.Notes.Add(new GuitarNote { Position = At(2), StringIndex = 1, Fret = 7 });
        });
        var path = Path.Combine(Path.GetTempPath(), $"dotcharter-split-merge-{Guid.NewGuid():N}.xml");
        file.Save(path);
        try
        {
            var opened = new XmlChartSerializer().Load(path);
            Assert.Single(opened.Arrangements[0].Levels[0].Chords, c => c.SplitIntoNotes);

            var host = new ChartProject();
            new RsXmlImporter().ImportInto(host, path);
            Assert.Single(host.Arrangements[0].Levels[0].Chords, c => c.SplitIntoNotes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>A DotCharter v2 project's own arrangement files (<c>RscpMigrations</c>)
    /// are read with the merge off: those loose notes were written as notes.</summary>
    [Fact]
    public void TheReaderAlone_KeepsLooseNotes()
    {
        var file = Scene(level =>
        {
            level.Chords.Add(Strum(At(1), (0, 5), (1, 7)));
            level.Notes.Add(new GuitarNote { Position = At(2), StringIndex = 0, Fret = 5 });
            level.Notes.Add(new GuitarNote { Position = At(2), StringIndex = 1, Fret = 7 });
        });

        var level = ReadWithoutMerge(file).Arrangements[0].Levels[0];

        Assert.Equal(2, level.Notes.Count);
        Assert.DoesNotContain(level.Chords, c => c.SplitIntoNotes);
    }

    // ── what does not ───────────────────────────────────────────────────────

    public static TheoryData<string, Action<ChartLevel>> NotMerged => new()
    {
        {
            // EOF keeps the hand shape over every chord it breaks into notes for
            // another reason (a link landing, an arpeggio, a chordify); a split
            // chord gets none.
            "inside a hand shape", level =>
            {
                level.HandShapes.Add(new HandShape { Position = At(1), EndPosition = At(3), TemplateId = 0 });
                level.Notes.Add(new GuitarNote { Position = At(2), StringIndex = 0, Fret = 5 });
                level.Notes.Add(new GuitarNote { Position = At(2), StringIndex = 1, Fret = 7 });
            }
        },
        {
            // EOF breaks a chord a linkNext lands on into notes (rs.c:2010) — an
            // ordinary chord, not a split one.
            "a link lands on it", level =>
            {
                level.Chords.Add(Strum(At(4), (0, 5), (1, 7)));
                level.Notes.Add(new GuitarNote { Position = At(1), StringIndex = 0, Fret = 3, Sustain = Beats(1), LinkNext = true });
                level.Notes.Add(new GuitarNote { Position = At(2), StringIndex = 0, Fret = 5 });
                level.Notes.Add(new GuitarNote { Position = At(2), StringIndex = 1, Fret = 7 });
            }
        },
        {
            // The save would strip the muted string out of a partly muted chord.
            "partly fret-hand muted", level =>
            {
                level.Chords.Add(Strum(At(1), (0, 5), (1, 7)));
                level.Notes.Add(new GuitarNote { Position = At(2), StringIndex = 0, Fret = 5, Mute = MuteType.Full });
                level.Notes.Add(new GuitarNote { Position = At(2), StringIndex = 1, Fret = 7 });
            }
        },
        {
            // (the E5 dyad on strings 1-2 is declared; the strum is on 3-4)
            "a chord at the same time", level =>
            {
                level.Chords.Add(Strum(At(1), (0, 5), (1, 7)));
                level.Chords.Add(Strum(At(2), (3, 7), (4, 9), templateId: -1));
                level.Notes.Add(new GuitarNote { Position = At(2), StringIndex = 1, Fret = 7 });
                level.Notes.Add(new GuitarNote { Position = At(2), StringIndex = 2, Fret = 7 });
            }
        },
        {
            // (no template names a string twice)
            "two notes on one string", level =>
            {
                level.Chords.Add(Strum(At(1), (0, 5), (1, 7)));
                level.Notes.Add(new GuitarNote { Position = At(2), StringIndex = 0, Fret = 5 });
                level.Notes.Add(new GuitarNote { Position = At(2), StringIndex = 0, Fret = 5 });
                level.Notes.Add(new GuitarNote { Position = At(2), StringIndex = 1, Fret = 7 });
            }
        },
        {
            // Fret 24 makes the save ignore the note; as a chord the ONE derived
            // ignore would reach the other string too.
            // (a strum with no template declares 24/7: the writer synthesizes one)
            "a derived ignore the other string does not have", level =>
            {
                level.Chords.Add(Strum(At(1), (0, 5), (1, 7)));
                level.Chords.Add(Strum(At(1), (2, 24), (3, 7), templateId: -1));
                level.Notes.Add(new GuitarNote { Position = At(2), StringIndex = 2, Fret = 24 });
                level.Notes.Add(new GuitarNote { Position = At(2), StringIndex = 3, Fret = 7 });
            }
        }
    };

    [Theory]
    [MemberData(nameof(NotMerged))]
    public void SimultaneousNotes_ThatAreNoSplitChord_StayLoose(string why, Action<ChartLevel> build)
    {
        var file = Scene(build);

        var imported = Import(file);

        Assert.True(imported.Arrangements[0].Levels[0].Chords.All(c => !c.SplitIntoNotes), why);
        Assert.Equal(Written(ReadWithoutMerge(file)), Written(imported));
    }

    /// <summary>
    ///     No template of these frets in the file: the chord plays a nameless,
    ///     fingerless mint, which is NOT written (only a split chord plays it), and
    ///     its double stop sets no <c>doubleStops</c> — a split chord counts as its
    ///     notes. The file writes back unchanged.
    /// </summary>
    [Fact]
    public void ASplitChordOfAnUndeclaredShape_PlaysAMint_ThatIsNotWritten()
    {
        // No strum in the level, so nothing else sets doubleStops.
        var file = Scene(level =>
        {
            level.Notes.Add(new GuitarNote { Position = At(2), StringIndex = 3, Fret = 9, Tremolo = true });
            level.Notes.Add(new GuitarNote { Position = At(2), StringIndex = 4, Fret = 10, Tremolo = true });
        });

        var merged = Import(file);

        var arrangement = merged.Arrangements[0];
        var split = Assert.Single(arrangement.Levels[0].Chords, c => c.SplitIntoNotes);
        var mint = Assert.Single(arrangement.ChordTemplates, t => t.Id == split.TemplateId);
        Assert.Equal(2, mint.Id);
        Assert.Equal("", mint.Name);
        Assert.Empty(mint.Fingers);
        Assert.True(mint.DescribesChordNotes(split.ChordNotes));
        Assert.Equal(2, Write(merged).Descendants("chordTemplate").Count());
        Assert.Equal("0", Write(merged).Descendants("arrangementProperties").Single().Attribute("doubleStops")?.Value);
        Assert.Equal(Written(ReadWithoutMerge(file)), Written(merged));
    }

    /// <summary>
    ///     The write-back is still the safety net. A declared template nothing else
    ///     plays ships today; a split chord on it would make it split-only, so it
    ///     would drop out of <c>&lt;chordTemplates&gt;</c>. The notes stay loose.
    /// </summary>
    [Fact]
    public void AGroupThatWouldDropADeclaredTemplate_StaysLoose()
    {
        var file = Scene(level =>
        {
            // Template 1 (E5, strings 1-2 at 7/7) is declared and played by nothing.
            level.Chords.Add(Strum(At(1), (0, 5), (1, 7)));
            level.Notes.Add(new GuitarNote { Position = At(2), StringIndex = 1, Fret = 7 });
            level.Notes.Add(new GuitarNote { Position = At(2), StringIndex = 2, Fret = 7 });
        });
        Assert.Equal(2, file.Descendants("chordTemplate").Count());

        var imported = Import(file);

        Assert.DoesNotContain(imported.Arrangements[0].Levels[0].Chords, c => c.SplitIntoNotes);
        Assert.Equal(2, imported.Arrangements[0].ChordTemplates.Count);
        Assert.Equal(Written(ReadWithoutMerge(file)), Written(imported));
    }

    /// <summary>
    ///     A DotCharter split chord -> RS XML -> import is the same split chord: the
    ///     same strings, frets and per-string techniques, the chord's own ignore, a
    ///     template of its frets — and the second export is the first, byte for byte.
    /// </summary>
    [Fact]
    public void ASplitChord_RoundTripsThroughRsXml()
    {
        var file = Scene(level =>
        {
            var split = Strum(At(2), (2, 9), (3, 9), templateId: -1);
            split.SplitIntoNotes = true;
            split.Ignore = true;
            split.ChordNotes[0].Vibrato = true;
            split.ChordNotes[0].VibratoStrength = 80;
            split.ChordNotes[0].Sustain = Beats(1);
            split.ChordNotes[1].SlideTo = 11;
            split.ChordNotes[1].Sustain = Beats(1);
            level.Chords.Add(split);
        });
        // The split chord ships as two notes and no template of its own.
        Assert.Equal(2, file.Descendants("note").Count());
        Assert.Empty(file.Descendants("chord"));
        Assert.Equal(2, file.Descendants("chordTemplate").Count());

        var back = Import(file);

        var chord = Assert.Single(back.Arrangements[0].Levels[0].Chords, c => c.SplitIntoNotes);
        Assert.Empty(back.Arrangements[0].Levels[0].Notes);
        Assert.Equal(At(2), chord.Position);
        Assert.True(chord.Ignore);
        Assert.Equal([(2, 9), (3, 9)], chord.ChordNotes.Select(n => (n.StringIndex, n.Fret)));
        Assert.True(chord.ChordNotes[0].Vibrato);
        Assert.Equal(80, chord.ChordNotes[0].VibratoStrength);
        Assert.Equal(11, chord.ChordNotes[1].SlideTo);
        Assert.All(chord.ChordNotes, n => Assert.True(n.Ignore));
        Assert.True(back.Arrangements[0].ChordTemplates.Single(t => t.Id == chord.TemplateId)
            .DescribesChordNotes(chord.ChordNotes));
        Assert.Equal(Strip(file), Written(back));
    }

    /// <summary>
    ///     Every EOF-written RS XML in the local corpus (<c>TestData/EOF/**/*_RS2.xml</c>,
    ///     git-ignored): with the merge on, the import writes back as exactly what the
    ///     loose notes write — straight away AND after the save-time pass. Skips
    ///     without the corpus; logs how many chords merged.
    /// </summary>
    [Fact]
    public void TheCorpus_WritesBackTheSame_WithTheMerge_AndAfterASave()
    {
        var files = CorpusFiles().ToList();
        Assert.SkipWhen(files.Count == 0, "No RS-XML corpus: TestData/EOF/**/*_RS2.xml is absent (TestData/ is git-ignored).");

        var merged = 0;
        var mergedFiles = 0;
        var failures = new List<string>();
        foreach (var file in files)
        {
            var doc = XDocument.Load(file);
            if (doc.Root?.Name.LocalName != "song") continue;

            var withMerge = Import(doc);
            var loose = ReadWithoutMerge(doc);
            var splits = withMerge.Arrangements[0].Levels.Sum(l => l.Chords.Count(c => c.SplitIntoNotes))
                         - loose.Arrangements[0].Levels.Sum(l => l.Chords.Count(c => c.SplitIntoNotes));
            merged += splits;
            if (splits > 0) mergedFiles++;

            if (Written(withMerge) != Written(loose))
            {
                failures.Add($"{Path.GetFileName(file)}: import");
                continue;
            }

            ChartSpacingEnforcer.EnforceProject(withMerge, new AppConfig());
            ChartSpacingEnforcer.EnforceProject(loose, new AppConfig());
            if (Written(withMerge) != Written(loose)) failures.Add($"{Path.GetFileName(file)}: after a save");
        }

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{files.Count} files, {merged} split chords merged in {mergedFiles} of them.");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static IEnumerable<string> CorpusFiles()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "TestData"))) dir = dir.Parent;
        var eof = dir == null ? null : Path.Combine(dir.FullName, "TestData", "EOF");
        if (eof == null || !Directory.Exists(eof)) return [];

        return Directory.EnumerateFiles(eof, "*_RS2.xml", SearchOption.AllDirectories)
            .Where(f => !f.Contains("debug", StringComparison.OrdinalIgnoreCase)
                        && !f.Contains("vocal", StringComparison.OrdinalIgnoreCase));
    }

    // ── scene ───────────────────────────────────────────────────────────────

    private static readonly List<BeatMarker> Grid =
        [.. Enumerable.Range(0, 32).Select(i => new BeatMarker { Time = 1000 + i * 500.0, Bpm = 120, IsMeasure = i % 4 == 0 })];

    private static FractionalPosition At(int beat) => new(beat);

    private static FractionalPosition Beats(int count) => new(count);

    /// <summary>A Lead arrangement declaring A5 (strings 0–1, frets 5/7) as template 0
    /// and an E5 dyad (strings 1–2, 7/7) as template 1, written by our own writer:
    /// the "file" every test imports.</summary>
    private static XDocument Scene(Action<ChartLevel> build)
    {
        var project = new ChartProject();
        foreach (var beat in Grid) project.Beats.Add(beat.Clone());
        var arrangement = new Arrangement { Name = "Lead", ArrangementType = ArrangementType.Lead, StringCount = 6 };
        for (var i = 0; i < 6; i++) arrangement.TuningPitches.Add(new TuningOffset(0));

        var a5 = new ChordTemplate { Id = 0, Name = "A5" };
        a5.Frets[0] = 5;
        a5.Frets[1] = 7;
        a5.Fingers[0] = 1;
        a5.Fingers[1] = 3;
        arrangement.ChordTemplates.Add(a5);
        var dyad = new ChordTemplate { Id = 1, Name = "E5" };
        dyad.Frets[1] = 7;
        dyad.Frets[2] = 7;
        dyad.Fingers[1] = 3;
        dyad.Fingers[2] = 3;
        arrangement.ChordTemplates.Add(dyad);

        var level = new ChartLevel();
        build(level);
        arrangement.Levels.Add(level);
        project.Arrangements.Add(arrangement);
        return new RsXmlSongWriter().Write(project);
    }

    private static Chord Strum(FractionalPosition at, (int String, int Fret) a, (int String, int Fret) b, int templateId = 0)
    {
        var chord = new Chord { Position = at, TemplateId = templateId };
        chord.ChordNotes.Add(new ChordNote { StringIndex = a.String, Fret = a.Fret });
        chord.ChordNotes.Add(new ChordNote { StringIndex = b.String, Fret = b.Fret });
        return chord;
    }

    private static ChartProject Import(XDocument file) =>
        new RsXmlSongReader { MergeSimultaneousNotes = true }.ReadDocument(new XDocument(file), "scene.xml");

    private static ChartProject ReadWithoutMerge(XDocument file) =>
        new RsXmlSongReader().ReadDocument(new XDocument(file), "scene.xml");

    private static XDocument Write(ChartProject project) => new RsXmlSongWriter().Write(project);

    /// <summary>The written document minus the clock reading.</summary>
    private static string Written(ChartProject project) => Strip(Write(project));

    private static string Strip(XDocument written)
    {
        var doc = new XDocument(written);
        doc.Root!.Element("lastConversionDateTime")?.Remove();
        return doc.ToString();
    }

    private static string[] NoteLines(XDocument doc) =>
        [.. doc.Descendants("note").Select(n => n.ToString(SaveOptions.DisableFormatting))];
}
