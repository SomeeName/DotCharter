using DotCharter.Core.Domain.Vocals;
using DotCharter.Services.Export.Sng;
using DotCharter.Services.Export.Sng.Model;
using DotCharter.Services.Parsing.Xml.Song;

namespace DotCharter.Tests.Services.Export;

public sealed partial class SngFromDomainMapperTests
{
    [Fact]
    public void Anchors_handshapes_phrases_sections_and_tones_survive()
    {
        var beats = CreateBeats();
        var original = CreateArrangement(beats);
        var result = SngFromDomainMapper.BuildInstrumental(original, beats, 32000);
        var sng = result.Sng;

        // Phrases + iterations.
        Assert.Equal(["COUNT", "riff", "END"], sng.Phrases.Select(p => p.Name));
        Assert.Equal(3, sng.PhraseIterations.Count);
        Assert.Equal(1, sng.Phrases[1].IterationCount);
        // Iteration end times chain to the next start / song length.
        Assert.Equal(sng.PhraseIterations[1].StartTime, sng.PhraseIterations[0].EndTime, 3);
        Assert.Equal(32f, sng.PhraseIterations[2].EndTime, 3);

        // Sections carry numbers per repeated name and pi links.
        Assert.Equal(3, sng.Sections.Count);
        Assert.Equal("riff", sng.Sections[0].Name);
        Assert.Equal(1, sng.Sections[0].Number);
        Assert.Equal(2, sng.Sections[1].Number);
        Assert.Equal("noguitar", sng.Sections[2].Name);

        // Anchors: end times chain; first/last note times computed.
        var level = sng.Levels[0];
        Assert.Equal(2, level.Anchors.Count);
        Assert.Equal(level.Anchors[1].StartTime, level.Anchors[0].EndTime, 3);
        Assert.Equal(2.0f, level.Anchors[0].FirstNoteTime, 2);

        // The pitched slide produced an anchor extension at its target fret.
        var extension = Assert.Single(level.AnchorExtensions);
        Assert.Equal(9, extension.Fret);
        Assert.Equal(5.5f, extension.BeatTime, 2);

        // Handshapes reference the chord template.
        var handShape = Assert.Single(level.HandShapes);
        Assert.Equal(0, handShape.ChordId);

        // Chord notes deduplicated across the two identical chords.
        Assert.Single(sng.ChordNotes);
        var chordSngNotes = level.Notes.Where(n => (n.Mask & RsNoteMask.Chord) != 0).ToList();
        Assert.Equal(2, chordSngNotes.Count);
        Assert.Equal(0, chordSngNotes[0].ChordNotesId);
        Assert.Equal(0, chordSngNotes[1].ChordNotesId);

        // Tones map to slots A/B.
        Assert.Equal(2, sng.Tones.Count);
        Assert.Equal(0, sng.Tones[0].ToneId);
        Assert.Equal(1, sng.Tones[1].ToneId);

        // Link-next chain: parent has Parent mask, child has Child mask + parent id.
        var parentIndex = level.Notes.FindIndex(n => (n.Mask & RsNoteMask.Parent) != 0 && n.StringIndex == 4);
        Assert.True(parentIndex >= 0);
        var child = level.Notes[parentIndex + 1];
        Assert.NotEqual(0u, child.Mask & 0x10000000); // Child bit
        Assert.Equal((short)parentIndex, child.ParentPrevNote);

        // Note counts: 8 notes + 2 chords on the single (hard) level.
        Assert.Equal(10, result.NoteCounts.Hard);
        Assert.Equal(1, result.NoteCounts.Ignored);
        Assert.Equal(10.0, sng.MetaData.MaxNotesAndChords);
        Assert.Equal(9.0, sng.MetaData.MaxNotesAndChordsReal);
        Assert.Equal(10000.0, sng.MetaData.PointsPerNote);

        // Metadata basics.
        Assert.Equal(-1, sng.MetaData.CapoFretId);
        Assert.Equal(0, sng.MetaData.MaxDifficulty);
        Assert.Equal(0.5f, sng.MetaData.FirstBeatLength, 3);
        Assert.Equal(2.0f, sng.MetaData.FirstNoteTime, 2);
        Assert.Equal(32f, sng.MetaData.SongLength, 3);

        // Beats: measure numbering and phrase iteration attribution.
        Assert.Equal(64, sng.Beats.Count);
        Assert.Equal(1u, sng.Beats[0].Mask & 0x1);
        Assert.Equal(0u, sng.Beats[1].Mask);
        Assert.Equal(0, sng.Beats[0].PhraseIteration);
        Assert.True(sng.Beats[^1].PhraseIteration == 2);
    }

    /// <summary>
    ///     A phrase whose LAST syllable is blank still has to end with <c>+</c>.
    ///     <para>
    ///         In Rocksmith the <c>+</c> IS the line break. A blank syllable is skipped,
    ///         and it used to be skipped with a <c>continue</c> INSIDE the loop while the
    ///         terminator test was still <c>i == lyrics.Count - 1</c> against the
    ///         unfiltered list — so a phrase ending in a blank syllable (and carrying no
    ///         earlier <c>IsPhraseEnd</c>) exported with no <c>+</c> anywhere and merged
    ///         into the next phrase in-game, with nothing wrong in the editor.
    ///     </para>
    /// </summary>
    [Fact]
    public void Vocals_phrase_ending_in_a_blank_syllable_still_terminates()
    {
        var beats = CreateBeats();
        var track = new VocalTrack { Name = "Vocals" };
        var phrase = new LyricPhrase { Position = Pos(beats, 4000) };
        phrase.Lyrics.Add(new LyricEvent
        {
            Position = Pos(beats, 4000), EndPosition = Pos(beats, 4400), Text = "hel",
            IsWordPart = true, Note = 60
        });
        phrase.Lyrics.Add(new LyricEvent
        {
            Position = Pos(beats, 4400), EndPosition = Pos(beats, 4600), Text = "lo", Note = 62
        });
        // The charter cleared this one's text. It is not exportable — but it must not
        // take the phrase's line break with it.
        phrase.Lyrics.Add(new LyricEvent
        {
            Position = Pos(beats, 4600), EndPosition = Pos(beats, 5000), Text = "  ", Note = 64
        });
        track.Phrases.Add(phrase);

        var sng = SngFromDomainMapper.BuildVocals(track, beats);

        Assert.Equal(2, sng.Vocals.Count);
        Assert.Equal("hel-", sng.Vocals[0].Lyric);
        Assert.Equal("lo+", sng.Vocals[1].Lyric);
    }

    [Fact]
    public void Vocals_track_builds_a_valid_vocals_sng()
    {
        var beats = CreateBeats();
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

        var sng = SngFromDomainMapper.BuildVocals(track, beats);

        Assert.Equal(2, sng.Vocals.Count);
        Assert.Equal("hel-", sng.Vocals[0].Lyric);
        Assert.Equal("lo+", sng.Vocals[1].Lyric);
        Assert.Equal(60, sng.Vocals[0].Note);
        Assert.Equal(4.0f, sng.Vocals[0].Time, 2);

        Assert.Equal(2, sng.SymbolsHeaders.Count);
        Assert.Single(sng.SymbolsTextures);
        Assert.Equal(@"assets\ui\lyrics\lyrics.dds", sng.SymbolsTextures[0].Font);
        Assert.True(sng.SymbolDefinitions.Count > 100, "default glyph set should be embedded");
    }

    /// <summary>
    ///     #885: what the round trips above check through the reader, pinned by bytes on the
    ///     writer side: the plain SNG body of the instrumental fixture and of the vocals track,
    ///     with the conversion stamp fixed. A failure means the mapper or the writer changed
    ///     what it emits: investigate, never re-pin.
    /// </summary>
    [Fact]
    public void The_fixture_sng_bodies_are_pinned()
    {
        var conversion = new DateTime(2026, 10, 14, 9, 30, 0, DateTimeKind.Unspecified);
        var beats = CreateBeats();
        var instrumental = SngFromDomainMapper.BuildInstrumental(CreateArrangement(beats), beats, 32000, conversion).Sng;
        var vocals = SngFromDomainMapper.BuildVocals(HelloVocals(CreateBeats(), out var vocalBeats), vocalBeats, conversion);

        SngWriterBytePinTests.AssertPinned(PinnedInstrumentalBody, RocksmithSngWriter.WriteUnpacked(instrumental), "instrumental body");
        SngWriterBytePinTests.AssertPinned(PinnedVocalsBody, RocksmithSngWriter.WriteUnpacked(vocals), "vocals body");
    }

    [Fact]
    public void Chart_without_phrases_gets_synthetic_structure()
    {
        var beats = CreateBeats();
        var arrangement = new Arrangement();
        for (var i = 0; i < 6; i++) arrangement.TuningPitches.Add(new TuningOffset(0));
        var level = new ChartLevel();
        level.Notes.Add(new GuitarNote { Position = Pos(beats, 1000), StringIndex = 0, Fret = 1 });
        level.Anchors.Add(new FretAnchor(Pos(beats, 1000), 1));
        arrangement.Levels.Add(level);

        var result = SngFromDomainMapper.BuildInstrumental(arrangement, beats, 32000);

        Assert.Equal(3, result.Sng.Phrases.Count);
        Assert.Equal(3, result.Sng.PhraseIterations.Count);
        // No authored sections: the one-riff stub, and no closing noguitar.
        Assert.Single(result.Sng.Sections);
        Assert.Single(result.Sng.Levels[0].Notes);
    }

    [Fact]
    public void Missing_events_are_synthesized_ts_marker_and_crowd_events()
    {
        var beats = CreateBeats();
        var arrangement = CreateArrangement(beats);

        var sng = SngFromDomainMapper.BuildInstrumental(arrangement, beats, 32000).Sng;

        // 120 BPM 4/4 (medium crowd), first note at 2000 ms, song length 32 s:
        // TS from the beat map; intro applause 600 ms after the first note for
        // 2.5 s; outro applause 9 s before audio end until audio end.
        Assert.Equal(
            [("TS:4/4", 0f), ("e1", 0f), ("E3", 2.6f), ("E13", 5.1f), ("D3", 23f), ("E13", 32f)],
            sng.Events.Select(e => (e.Name, e.Time)));
    }

    [Fact]
    public void Authored_events_suppress_crowd_synthesis_and_are_kept()
    {
        var beats = CreateBeats();
        var arrangement = CreateArrangement(beats);
        arrangement.EventPoints.Add(new EventPoint { Position = Pos(beats, 1000), Events = "e0,E3,D3" });

        var sng = SngFromDomainMapper.BuildInstrumental(arrangement, beats, 32000).Sng;

        // Only the TS event is synthesized; events sort by time then ordinal name.
        Assert.Equal(["TS:4/4", "D3", "E3", "e0"], sng.Events.Select(e => e.Name));
    }

    [Fact]
    public void Level_without_anchors_gets_a_synthesized_covering_anchor()
    {
        var beats = CreateBeats();
        var arrangement = CreateArrangement(beats);
        arrangement.Levels[0].Anchors.Clear();

        var sng = SngFromDomainMapper.BuildInstrumental(arrangement, beats, 32000).Sng;

        // An empty SNG anchor array crashes the game; one anchor at the lowest
        // fretted content (the chord's fret 2) covers the level.
        var anchor = Assert.Single(sng.Levels[0].Anchors);
        Assert.Equal(2, anchor.Fret);
        Assert.Equal(4, anchor.Width);
        Assert.Equal(2000.0, anchor.StartTime * 1000.0, Tolerance);
        Assert.All(sng.Levels[0].Notes, n => Assert.Equal((sbyte)2, n.AnchorFret));
    }

    [Fact]
    public void Chord_with_no_template_and_no_chord_notes_is_dropped()
    {
        var beats = CreateBeats();
        var arrangement = CreateArrangement(beats);
        arrangement.Levels[0].Chords.Add(new Chord { Position = Pos(beats, 12000), TemplateId = -1 });

        var sng = SngFromDomainMapper.BuildInstrumental(arrangement, beats, 32000).Sng;

        // The ghost chord is not written; every written chord resolves to a
        // real template (ChordId -1 is an out-of-bounds index in-game).
        var chords = sng.Levels[0].Notes.Where(n => n.StringIndex == -1).ToList();
        Assert.Equal(2, chords.Count);
        Assert.All(chords, c => Assert.InRange(c.ChordId, 0, sng.Chords.Count - 1));
    }

    [Fact]
    public void Synthesized_end_phrase_starts_after_the_last_sustain()
    {
        var beats = CreateBeats();
        var arrangement = new Arrangement
        {
            ArrangementType = ArrangementType.Lead,
            Name = "Lead",
            StringCount = 6
        };
        for (var i = 0; i < 6; i++) arrangement.TuningPitches.Add(new TuningOffset(0));

        var level = new ChartLevel { DifficultyIndex = 0 };
        level.Anchors.Add(new FretAnchor(Pos(beats, 1000), 1, 4));
        level.Notes.Add(new GuitarNote
        {
            Position = Pos(beats, 30000),
            StringIndex = 0,
            Fret = 3,
            Sustain = Pos(beats, 1800) // ends at 31800 ms, past the last beat (31500 ms)
        });
        arrangement.Levels.Add(level);

        // No phrases at all: the COUNT/p0/END stub is synthesized.
        var sng = SngFromDomainMapper.BuildInstrumental(arrangement, beats, 32000).Sng;

        var end = sng.PhraseIterations[^1];
        Assert.True(end.StartTime * 1000.0 > 31800.0 - Tolerance,
            $"END must start after the last sustain, started at {end.StartTime * 1000.0} ms");
        var note = Assert.Single(sng.Levels[0].Notes);
        Assert.Equal(1, note.PhraseIterationId);
    }

    [Fact]
    public void Two_level_phrases_pin_hero_levels_to_hard()
    {
        var beats = CreateBeats();
        var arrangement = CreateArrangement(beats);
        arrangement.PhraseInfos.Clear();

        var level1 = new ChartLevel { DifficultyIndex = 1 };
        level1.Anchors.Add(new FretAnchor(Pos(beats, 2000), 1, 4));
        level1.Notes.Add(new GuitarNote { Position = Pos(beats, 2000), StringIndex = 0, Fret = 3 });
        arrangement.Levels.Add(level1);

        var sng = SngFromDomainMapper.BuildInstrumental(arrangement, beats, 32000).Sng;

        // Two levels: intermediate easy/medium hero levels crash score attack.
        Assert.All(sng.PhraseIterations, pi => Assert.Equal([1, 1, 1], pi.Difficulty));
    }

    /// <summary>
    ///     Bar 1 is <c>Measure = 1</c>, and the <c>EvenMeasure</c> bit follows from
    ///     that. <c>currentMeasure</c> used to start at <c>-1</c> and increment on
    ///     the first beat, so bar 1 shipped as <c>0</c> — and because the mask is
    ///     <c>currentMeasure % 2 == 0</c>, the parity was inverted for the whole
    ///     song. <c>BuildBeats</c> had no coverage at all.
    /// </summary>
    [Fact]
    public void BuildBeats_NumbersMeasuresFromOne_AndSetsEvenMeasureOnTheEvenOnes()
    {
        var beats = CreateBeats(); // 4/4 at 120 BPM, a measure every 4 beats
        var arrangement = CreateArrangement(beats);

        var sng = SngFromDomainMapper.BuildInstrumental(arrangement, beats, 32000).Sng;

        Assert.Equal(beats.Count, sng.Beats.Count);
        Assert.Equal(1, sng.Beats[0].Measure);
        Assert.Equal(0, sng.Beats[0].Beat);
        Assert.Equal(0x1u, sng.Beats[0].Mask & 0x1u); // FirstBeatOfMeasure
        Assert.Equal(0u, sng.Beats[0].Mask & 0x2u); // bar 1 is ODD

        // Beats 1-3 belong to bar 1 and carry no mask at all.
        for (var i = 1; i < 4; i++)
        {
            Assert.Equal(1, sng.Beats[i].Measure);
            Assert.Equal((short)i, sng.Beats[i].Beat);
            Assert.Equal(0u, sng.Beats[i].Mask);
        }

        Assert.Equal(2, sng.Beats[4].Measure);
        Assert.Equal(0x3u, sng.Beats[4].Mask & 0x3u); // FirstBeatOfMeasure | EvenMeasure
        Assert.Equal(3, sng.Beats[8].Measure);
        Assert.Equal(0u, sng.Beats[8].Mask & 0x2u);
    }

    /// <summary>
    ///     The golden. The reference converter does not COUNT measures at all — it
    ///     copies the number the XML already carries
    ///     (<c>Rocksmith2014.Conversion/XmlToSng.fs</c>,
    ///     <c>if xmlBeat.Measure &gt;= 0 then currentMeasure &lt;- xmlBeat.Measure</c>)
    ///     — so the hand-corrected corpus IS the expected output. All 89
    ///     arrangements number their bars consecutively from <c>measure="1"</c>, and
    ///     DotCharter's own <c>RsXmlSongWriter.WriteEbeats</c> writes them the same
    ///     way. Off by one, the psarc's SNG disagreed with the XML bundle the same
    ///     save produced.
    /// </summary>
    [Fact]
    public void OnTheEofCorpus_EveryExportedMeasureNumberMatchesTheXmlsOwn()
    {
        var files = EofRsXmlCorpus();
        Assert.SkipWhen(files.Count == 0,
            "No RS-XML corpus: TestData/EOF/**/*_RS2.xml is absent (TestData/ is git-ignored).");

        var reader = new RsXmlSongReader();
        var mismatches = new List<string>();
        var compared = 0;

        foreach (var file in files)
        {
            var expected = ExpectedMeasureNumbers(file);
            if (expected.Count == 0) continue;

            ChartProject project;
            try
            {
                project = reader.Read(file);
            }
            catch (Exception)
            {
                continue;
            }

            var arrangement = project.Arrangements.FirstOrDefault();
            if (arrangement == null || project.Beats.Count != expected.Count) continue;

            var sng = SngFromDomainMapper.BuildInstrumental(
                arrangement, project.Beats, project.Beats[^1].Time).Sng;

            compared++;
            for (var i = 0; i < expected.Count; i++)
            {
                if (sng.Beats[i].Measure == expected[i]) continue;
                mismatches.Add(
                    $"{Path.GetFileName(file)} beat {i}: exported {sng.Beats[i].Measure}, XML says {expected[i]}");
                break;
            }
        }

        Assert.True(compared > 0, "the corpus was found but nothing could be compared");
        Assert.True(mismatches.Count == 0,
            $"{mismatches.Count} of {compared} arrangements ship a measure number the XML disagrees with: " +
            string.Join("; ", mismatches.Take(5)));
    }
}
