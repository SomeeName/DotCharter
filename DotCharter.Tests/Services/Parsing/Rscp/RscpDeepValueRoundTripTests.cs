using DotCharter.Core.Domain.Vocals;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Parsing.Rscp;

/// <summary>
///     VALUE fidelity, not presence. The coarse "does every settable property
///     appear somewhere in the writer" check has always been clean, so the
///     failures that survive it are the ones where a field is WRITTEN but the
///     value that comes back is not the value that went in — a rounded double,
///     a lost null, a reordered collection, a dropped element of a nested
///     collection.
///     <para>The comparison walks the whole <see cref="ChartProject" /> graph by
///     reflection and reports every differing PATH at once, so one run names
///     every loss rather than the first.</para>
/// </summary>
public sealed class RscpDeepValueRoundTripTests : IDisposable
{
    /// <summary>
    ///     The two paths the `.rscp` deliberately does not carry. Everything
    ///     else in the graph is compared, and a new exclusion needs a reason of
    ///     this shape — "it did not round-trip" is not one.
    ///     <para>`project.Chart.Tuning` was a third entry until 0.44.2, excused as
    ///     "vestigial, and the package library is the only reader". Neither half
    ///     was true — six UI/render call sites read it for a lane count, and the
    ///     psarc reader's `Length == 0` guard could never fire against the class
    ///     default. The field is GONE now rather than excluded, so the reflection
    ///     walk covers ChartData completely.</para>
    ///     <para>Shared with the `.dcsong` round trip (`DcSongRoundTripTests`), whose
    ///     chart IS a `.rscp`: the same two paths, for the same two reasons.</para>
    /// </summary>
    internal static readonly HashSet<string> Ignored = new(StringComparer.Ordinal)
    {
        // Vestigial: nothing in the app reads ChartData.BeatsPerMinute, and the
        // project's tempo is the BEAT GRID (ChartProject.Bpm is re-derived from
        // the first two beat times on load).
        "project.Chart.BeatsPerMinute",

        // Display-only, derived from the shape's chord template on every read
        // (RscpTemplateManager says so at the one place it is set). The
        // TemplateId is what carries the identity, and that IS compared.
        "project.Arrangements[0].Levels[0].HandShapes[0].Name"
    };

    private readonly string _dir = Directory.CreateTempSubdirectory("dotcharter_rscp_deep_").FullName;

    public void Dispose() => BestEffortDelete.Directory(_dir);

    [Fact]
    public void ARichProject_RoundTripsEveryValue()
    {
        var original = RichProjectFactory.Build();
        var path = Path.Combine(_dir, "rich.rscp");
        new RscpChartSerializer().Save(path, original);
        var loaded = new RscpChartSerializer().Load(path);

        // The save canonicalizes chord templates (RscpTemplateManager), on a copy
        // since #304, so the expectation is the original regenerated the same way.
        // This test used to compare against an original the writer had already
        // regenerated in place.
        var expected = ChartProjectCloner.Clone(original);
        foreach (var arr in expected.Arrangements) new RscpTemplateManager().RegenerateTemplates(arr);

        var diffs = DeepCompare.Diff("project", expected, loaded, Ignored);
        Assert.True(diffs.Count == 0,
            "The .rscp round trip changed these values:\n  " + string.Join("\n  ", diffs));
    }
}

/// <summary>
///     A deliberately RICH project: every settable field carries a value that
///     differs from its default, every collection carries more than one entry,
///     and the numeric values are chosen so a rounding or truncating writer
///     cannot reproduce them by accident.
/// </summary>
internal static class RichProjectFactory
{
    public static ChartProject Build()
    {
        var project = new ChartProject
        {
            ProjectId = "0123456789abcdef0123456789abcdef",
            AudioFileName = "audio/song mix.ogg",
            Bpm = 137.5,
            TextContent = "line one\nline two",
            IsBeatMapLocked = true,
            GuitarRolesSplit = true,
            // NOT the default true: the fixture's whole job is to sit away from
            // every default, and this pair is the one that turned a saved "play the
            // original, not the stems" into a project that opened silent.
            OriginalMixMuted = false
        };

        project.Chart.AlbumName = "The Album";
        project.Chart.AlbumYear = "1997";
        project.Chart.ArrangementId = 3;
        project.Chart.Artist = "The Artist";
        project.Chart.ArtistSort = "Artist, The";
        project.Chart.BeatsPerMinute = 137.5;
        project.Chart.ChartFormatVersion = 3;
        // NOT "GUITAR" — that is the class default, and a fixture value equal to
        // the default makes the whole comparison vacuous for that field (a writer
        // that dropped it entirely would still compare equal). RscpFixtureCoverageTests
        // lints for exactly this.
        project.Chart.EditMode = "BASS";
        project.Chart.JapaneseTitle = "曲名";
        project.Chart.JapaneseArtist = "アーティスト";
        project.Chart.Level = 2;
        project.Chart.MainAudioOffsetAdded = 1234.5;
        project.Chart.BarsAdded = 2;
        project.Chart.SelectedStem = 1;
        project.Chart.SongName = "The Song";
        project.Chart.Charter = "SomeName";
        project.Chart.Genre = "Progressive Rock";
        project.Chart.ReleaseDate = "1997-04-11";
        project.Chart.Version = "2.5";
        project.Chart.CoverArtPath = "art/cover.png";
        project.Chart.Time = 4321.25;
        project.Chart.KeyAccidentals = -3;
        project.Chart.KeyIsMinor = true;

        // A beat's Bpm is DERIVED on load from the gap to the next beat folded
        // with its own NoteDenominator, so the grid has to be internally
        // consistent or the comparison is measuring the fixture. Even 436.36 ms
        // spacing is 137.5 BPM in 4/4 and half that once the denominator is 8.
        for (var i = 0; i < 32; i++)
        {
            project.Beats.Add(new BeatMarker
            {
                Time = i * 436.36363636363637,
                Bpm = i < 16 ? 137.5 : 68.75,
                IsMeasure = i % 4 == 0,
                IsAnchor = i % 8 == 0,
                BeatsInMeasure = i < 16 ? 4 : 7,
                NoteDenominator = i < 16 ? 4 : 8
            });
        }

        project.Stems.Add(new Stem
        {
            Local = false, Name = "drums", Offset = -12.5, Path = "stems/drums.ogg",
            Volume = 0.75, Muted = true, Solo = true
        });
        project.Stems.Add(new Stem { Name = "bass", Path = "stems/bass.ogg", Volume = 0.5 });

        project.ArrangementFiles.Add("DTRS XML/lead.xml");
        project.ArrangementFiles.Add("DTRS XML/rhythm.xml");

        // The forward-compatibility residue: top-level nodes a NEWER build wrote
        // that this one cannot read, which the writer has to put back verbatim.
        // Stated in the canonical form the capture normalises to — one line, XLinq
        // self-closing spelling — because the value that comes back is
        // XElement.ToString(DisableFormatting) and the comparison here is an
        // equality, not a semantic XML diff. Two entries so the ORDER is covered.
        project.UnknownRscpNodes.Add(
            "<futureFeatureBlock version=\"9\"><futureChild p=\"12/7\" v=\"1.5\" /></futureFeatureBlock>");
        project.UnknownRscpNodes.Add("<dcSomethingFromLater>opaque payload</dcSomethingFromLater>");

        // Showlight types are CANONICALISED through ShowlightCatalog at both
        // ends (a free-text "fog" comes back as FOG_GREEN), so the fixture
        // states the canonical spelling and the comparison then covers the
        // position/note/type triple rather than the normaliser.
        project.Showlights.Add(new Showlight
        { Position = new FractionalPosition(3, new Fraction(1, 3)), Note = 24, Type = "FOG_GREEN" });
        project.Showlights.Add(new Showlight
        { Position = new FractionalPosition(9), Note = 66, Type = "LASERS_ON" });

        project.Bookmarks.Add(new Bookmark { Index = 1, Time = 1500.5 });
        project.Bookmarks.Add(new Bookmark { Index = 2, Time = 9000.25 });

        project.Annotations.Add(new ChartAnnotation
        { TimeMs = 2500.5, Kind = ChartAnnotationKind.Reminder, Text = "fix the bend <here>", Resolved = true });
        project.Annotations.Add(new ChartAnnotation { TimeMs = 7000, Kind = ChartAnnotationKind.Mark, Text = "solo" });

        project.ToneDefinitions.Add(new ToneDefinition
        { Key = "dc_clean", Name = "Clean & Bright", Json = """{"Key":"dc_clean"}""" });
        project.ToneDefinitions.Add(new ToneDefinition
        { Key = "dc_lead", Name = "Lead", Json = """{"Key":"dc_lead"}""" });

        // #160: every rule off its default, and the two sets different from each
        // other, so a writer that swapped guitar and bass would not round-trip.
        project.SynthPlayStyle.Guitar.CopyFrom(new ChartSynthPlayRules
        {
            HandShapesRing = false, HoldSingleNoteGaps = false, SingleNoteGapDivisor = 8,
            HoldChordGaps = false, ChordGapDivisor = 16
        });
        project.SynthPlayStyle.Bass.CopyFrom(new ChartSynthPlayRules
        {
            HandShapesRing = false, HoldSingleNoteGaps = false, SingleNoteGapDivisor = 2,
            HoldChordGaps = false, ChordGapDivisor = 32
        });

        var vocals = new VocalTrack { Name = "Lead Vocals", ColorR = 12, ColorG = 34, ColorB = 56, ColorA = 200 };
        // A phrase's END is the end of its PHRASE_END lyric — the format has no
        // separate field for it — and a word-part hyphen lives in IsWordPart,
        // never in the text (the parser strips a trailing "-" and raises the
        // flag). Both are canonicalisations, so the fixture states the canonical
        // form and the round trip stays an equality.
        var phrase = new LyricPhrase
        {
            Position = new FractionalPosition(4),
            EndPosition = new FractionalPosition(5, new Fraction(3, 4))
        };
        phrase.Lyrics.Add(new LyricEvent
        {
            Position = new FractionalPosition(4),
            EndPosition = new FractionalPosition(4, new Fraction(1, 2)),
            Text = "hel",
            IsWordPart = true,
            Note = 62
        });
        phrase.Lyrics.Add(new LyricEvent
        {
            Position = new FractionalPosition(5),
            EndPosition = new FractionalPosition(5, new Fraction(3, 4)),
            Text = "lo",
            IsPhraseEnd = true,
            Note = 64
        });
        vocals.Phrases.Add(phrase);
        project.VocalTracks.Add(vocals);

        project.Arrangements.Add(BuildArrangement());
        return project;
    }

    private static Arrangement BuildArrangement()
    {
        var arr = new Arrangement
        {
            ArrangementType = ArrangementType.Rhythm,
            Capo = 2,
            CentOffset = -10.5,
            Name = "Rhythm Guitar",
            PickedBass = true,
            StartingTone = "dc_clean",
            StringCount = 6,
            Subtype = ArrangementSubtype.Bonus,
            // Letters are RE-DERIVED from the offsets on load (uppercase, one
            // spelling per pitch class), and a type NAME is only written when
            // the offsets still mean it — so the fixture states the resolved
            // pair. The tuning's own losses have their own suite
            // (RscpTuningRoundTripTests); this one is here so a REGRESSION in
            // the offsets shows up in the whole-project walk too.
            Tuning = ["D", "A", "D", "G", "B", "E"],
            TuningType = "CUSTOM",
            ScrollSpeed = 1.7,
            ToneBase = "dc_clean",
            ToneA = "dc_lead",
            ToneB = "dc_b",
            ToneC = "dc_c",
            ToneD = "dc_d",
            ArtistName = "The Artist",
            ArtistNameSort = "Artist, The",
            AlbumName = "The Album",
            AlbumYear = 1997
        };

        foreach (var step in new[] { -2, 0, 0, 0, 0, 0 }) arr.TuningPitches.Add(new TuningOffset(step));
        arr.ToneNames.Add("dc_clean");
        arr.ToneNames.Add("dc_lead");

        arr.ChordTemplates.Add(new ChordTemplate
        {
            Id = 0, Name = "D5", Arpeggio = true, ForceArpeggioInRs = true, NopInRs = true, FollowedFingering = true,
            Frets = new Dictionary<int, int> { [0] = 0, [1] = 2, [2] = 2 },
            Fingers = new Dictionary<int, int> { [1] = 1, [2] = 3 }
        });
        arr.ChordTemplates.Add(new ChordTemplate
        {
            // String 5 is held, never played by the chord below: a ghost gem, and so pinned (#785).
            Id = 1, Name = "Gadd9", GhostMask = 1 << 5, Pinned = true,
            Frets = new Dictionary<int, int> { [3] = 12, [4] = 14, [5] = 12 },
            Fingers = new Dictionary<int, int> { [3] = 1, [4] = 3, [5] = 1 }
        });
        arr.FretHandMuteTemplates.Add(new ChordTemplate
        // Fingerless (#609) on the one template that has no fingers anyway.
        { Id = 0, Name = "mute", Fingerless = true, Frets = new Dictionary<int, int> { [0] = 5, [1] = 5 } });

        arr.EventPoints.Add(new EventPoint
        {
            Position = new FractionalPosition(0),
            Phrase = "intro",
            Section = "Intro",
            Events = "B0,B1",
            Status = SectionStatus.Review
        });
        arr.EventPoints.Add(new EventPoint
        {
            Position = new FractionalPosition(16, new Fraction(1, 3)),
            Phrase = "verse",
            Section = "Verse",
            Status = SectionStatus.Done
        });

        arr.ToneChanges.Add(new ToneChange { Position = new FractionalPosition(8), ToneName = "dc_lead" });
        arr.ToneChanges.Add(new ToneChange { Position = new FractionalPosition(24), ToneName = "dc_clean" });

        arr.Phrases.Add("intro");
        arr.Phrases.Add("verse");
        arr.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(0), 0));
        arr.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(16, new Fraction(1, 3)), 1));
        arr.PhraseInfos["intro"] = new PhraseInfo(3, true);
        arr.PhraseInfos["verse"] = new PhraseInfo(7, false);

        arr.Properties.ManualOverride = true;
        arr.Properties.Represent = 1;
        arr.Properties.BonusArr = 1;
        arr.Properties.PathRhythm = 1;
        arr.Properties.Bends = 1;
        arr.Properties.Syncopation = 1;

        var level = new ChartLevel { DifficultyIndex = 0 };
        level.Notes.Add(RichNote(new FractionalPosition(1, new Fraction(1, 3))));
        level.Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(2),
            StringIndex = 5,
            Fret = 0,
            Sustain = new FractionalPosition(0, new Fraction(3, 8)),
            Mute = MuteType.Palm,
            Hopo = HopoType.HammerOn,
            Harmonic = HarmonicType.Pinch,
            BassPicking = BassPickingType.Slap,
            Accent = true,
            Ignore = true,
            IgnoreDerived = true,
            PassOtherNotes = true,
            LinkNext = true,
            Tremolo = true,
            UnpitchedSlide = true,
            SlideTo = 9,
            NotationDuration = new Fraction(3, 2),
            GraceFret = 4,
            LeftHandFinger = 0,
            PickDirection = PickDirectionType.Up
        });

        var chord = new Chord
        {
            Position = new FractionalPosition(3, new Fraction(1, 6)),
            TemplateId = 1,
            SplitIntoNotes = true,
            ForceNoNotes = true,
            Accent = true,
            Ignore = true,
            IgnoreDerived = true,
            PassOtherNotes = true,
            Strum = PickDirectionType.Down,
            NotationDuration = new Fraction(1, 3)
        };
        chord.ChordNotes.Add(new ChordNote
        {
            StringIndex = 3, Fret = 12, Sustain = new FractionalPosition(0, new Fraction(1, 6)),
            Accent = true, BassPicking = BassPickingType.Pop, GraceFret = 10,
            Harmonic = HarmonicType.Normal, Hopo = HopoType.PullOff, LinkNext = true,
            Mute = MuteType.Full, SlideTo = 14, Tremolo = true, UnpitchedSlide = true,
            Vibrato = true, VibratoStrength = 60, Ignore = true, ExplicitSustain = true
        });
        chord.ChordNotes[0].BendValues.Add(new BendValue(new FractionalPosition(3, new Fraction(1, 4)), 1.5));
        chord.ChordNotes.Add(new ChordNote { StringIndex = 4, Fret = 14, ExplicitSustain = true });
        level.Chords.Add(chord);

        level.Anchors.Add(new FretAnchor(new FractionalPosition(0), 1, 4));
        level.Anchors.Add(new FretAnchor(new FractionalPosition(12, new Fraction(1, 2)), 12, 6));
        level.HandShapes.Add(new HandShape
        {
            Position = new FractionalPosition(3, new Fraction(1, 6)),
            EndPosition = new FractionalPosition(4),
            TemplateId = 1,
            Name = "Gadd9"
        });
        level.FretHandMutes.Add(new FretHandMute(new FractionalPosition(6, new Fraction(1, 2))));
        arr.Levels.Add(level);

        // A SECOND difficulty level, so ChartLevel.DifficultyIndex carries a
        // non-default value somewhere in the graph. The index is re-derived from
        // the level's ordinal on read, so this is the only way the walk can see
        // the levels come back in the right order at all.
        var harder = new ChartLevel { DifficultyIndex = 1 };
        harder.Notes.Add(RichNote(new FractionalPosition(9, new Fraction(1, 2))));
        arr.Levels.Add(harder);

        var voice = new NotationVoice { VoiceIndex = 2 };
        voice.Notes.Add(RichNote(new FractionalPosition(5, new Fraction(2, 3))));
        var voiceChord = new Chord { Position = new FractionalPosition(6), TemplateId = 0 };
        voiceChord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 2, GraceFret = 1, ExplicitSustain = true });
        voice.Chords.Add(voiceChord);
        arr.NotationVoices.Add(voice);

        return arr;
    }

    private static GuitarNote RichNote(FractionalPosition position)
    {
        var note = new GuitarNote
        {
            Position = position,
            StringIndex = 2,
            Fret = 7,
            Sustain = new FractionalPosition(1, new Fraction(1, 6)),
            Vibrato = true,
            VibratoStrength = 80,
            NotationDuration = new Fraction(2, 3),
            GraceFret = 5
        };
        note.BendValues.Add(new BendValue(position, 0.0));
        note.BendValues.Add(new BendValue(position + new Fraction(1, 6), 1.5));
        note.BendValues.Add(new BendValue(position + new Fraction(1, 3), 2.5));
        return note;
    }
}
