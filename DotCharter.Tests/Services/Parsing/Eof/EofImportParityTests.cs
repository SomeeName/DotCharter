using DotCharter.Services.Parsing.Eof;

namespace DotCharter.Tests.Services.Parsing.Eof;

public sealed class EofImportParityTests
{
    [Fact]
    public void ApplyTextEventsToArrangement_PromotesRsEventsAndSoloFlags()
    {
        var arrangement = new Arrangement();
        var metadata = new EofImportMetadata();
        metadata.TextEvents.Add(new EofTextEventInfo
        {
            Text = "solo_phrase",
            BeatPos = 1,
            Flags = 1 | 8,
            Track = 0
        });
        metadata.TextEvents.Add(new EofTextEventInfo
        {
            Text = "verse",
            BeatPos = 1,
            Flags = 2,
            Track = 0
        });
        metadata.TextEvents.Add(new EofTextEventInfo
        {
            Text = "TS:4/4",
            BeatPos = 2,
            Flags = 4,
            Track = 0
        });
        metadata.TextEvents.Add(new EofTextEventInfo
        {
            Text = "bridge",
            BeatPos = 0,
            Flags = 0,
            Track = 0
        });

        var beats = new List<BeatMarker>
        {
            new() { Time = 0, Bpm = 120, IsMeasure = true },
            new() { Time = 500, Bpm = 120 },
            new() { Time = 1000, Bpm = 120 }
        };

        EofImporter.ApplyTextEventsToArrangement(arrangement, 1, metadata, beats);

        Assert.Contains(arrangement.EventPoints, ep => ep.Phrase == "solo_phrase");
        Assert.Contains(arrangement.EventPoints, ep => ep.Section == "verse");
        Assert.Contains(arrangement.EventPoints, ep => ep.Events == "TS:4/4");
        Assert.Contains(arrangement.EventPoints, ep => ep.Phrase == "bridge");
        Assert.True(arrangement.PhraseInfos["solo_phrase"].Solo);
    }

    [Theory]
    [InlineData("Rhythm", ArrangementType.Rhythm)]
    [InlineData("REAL_BASS", ArrangementType.Bass)]
    [InlineData("Combo Path", ArrangementType.Combo)]
    [InlineData("Lead Guitar", ArrangementType.Lead)]
    [InlineData("Unknown Track", ArrangementType.Lead)]
    public void InferArrangementType_UsesTrackNameHints(string trackName, ArrangementType expected) => Assert.Equal(expected, EofProGuitarTrackParser.InferArrangementType(trackName));

    [Fact]
    public void ResolveChordSlideTarget_UsesSlideDistanceFromLowestChordFret()
    {
        const uint flags = EofConstants.PgFlagRsNotation | EofConstants.PgFlagSlideUp;

        Assert.Equal(3, EofProGuitarTrackParser.ResolveChordSlideTarget(flags, slideEndFret: 3,
            unpitchedSlideEndFret: 0, currentFret: 1, lowestFrettedNote: 1, capo: 0));
        Assert.Equal(5, EofProGuitarTrackParser.ResolveChordSlideTarget(flags, slideEndFret: 3,
            unpitchedSlideEndFret: 0, currentFret: 3, lowestFrettedNote: 1, capo: 0));
        Assert.Equal(5, EofProGuitarTrackParser.ResolveChordSlideTarget(flags, slideEndFret: 3,
            unpitchedSlideEndFret: 0, currentFret: 3, lowestFrettedNote: 1, capo: 0));
    }

    [Fact]
    public void ResolveChordSlideTarget_DoesNotAssignSlideToOpenStrings()
    {
        const uint flags = EofConstants.PgFlagRsNotation | EofConstants.PgFlagSlideUp;

        Assert.Null(EofProGuitarTrackParser.ResolveChordSlideTarget(flags, slideEndFret: 3,
            unpitchedSlideEndFret: 0, currentFret: 0, lowestFrettedNote: 1, capo: 0));
    }

    [Fact]
    public void FindApplicableTechNote_ReturnsLastMatchingStringOverrideWithinNoteRange()
    {
        var raw = new EofRawProGuitarNote
        {
            Difficulty = 0,
            PositionMs = 100,
            LengthMs = 80,
            NoteBitmask = 0b0000_0111
        };

        var techNotes = new List<EofRawProGuitarNote>
        {
            new() { Difficulty = 0, PositionMs = 90, NoteBitmask = 0b0000_0010 },
            new() { Difficulty = 0, PositionMs = 120, NoteBitmask = 0b0000_0001, SlideEndFret = 3 },
            new() { Difficulty = 0, PositionMs = 140, NoteBitmask = 0b0000_0010, SlideEndFret = 5 },
            new() { Difficulty = 0, PositionMs = 160, NoteBitmask = 0b0000_0010, SlideEndFret = 7 },
            new() { Difficulty = 1, PositionMs = 150, NoteBitmask = 0b0000_0010, SlideEndFret = 9 }
        };

        var match = EofProGuitarTrackParser.FindApplicableTechNote(techNotes, raw, stringIndex: 1);

        Assert.NotNull(match);
        Assert.Equal(7, match.SlideEndFret);
    }

    [Fact]
    public void ResolveEffectiveSlide_UsesPerStringTechNoteOverrideForChordSlides()
    {
        var raw = new EofRawProGuitarNote
        {
            Flags = EofConstants.PgFlagRsNotation | EofConstants.PgFlagSlideUp,
            SlideEndFret = 3
        };
        var tech = new EofRawProGuitarNote
        {
            Flags = EofConstants.PgFlagRsNotation | EofConstants.PgFlagSlideUp,
            SlideEndFret = 5
        };

        var (slideTo, unpitchedSlide) = EofProGuitarTrackParser.ResolveEffectiveSlide(raw, tech,
            currentFret: 3, lowestFrettedNote: 1, capo: 0);

        Assert.False(unpitchedSlide);
        Assert.Equal(5, slideTo);
    }

    [Fact]
    public void ResolveEffectiveSlide_FallsBackToRawChordSlideWhenNoTechNoteApplies()
    {
        var raw = new EofRawProGuitarNote
        {
            Flags = EofConstants.PgFlagRsNotation | EofConstants.PgFlagSlideUp,
            SlideEndFret = 3
        };

        var (slideTo, unpitchedSlide) = EofProGuitarTrackParser.ResolveEffectiveSlide(raw, null,
            currentFret: 3, lowestFrettedNote: 1, capo: 0);

        Assert.False(unpitchedSlide);
        Assert.Equal(5, slideTo);
    }
}
