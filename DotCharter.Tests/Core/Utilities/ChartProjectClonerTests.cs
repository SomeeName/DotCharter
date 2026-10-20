using DotCharter.Core.Domain.Vocals;

namespace DotCharter.Tests.Core.Utilities;

/// <summary>
///     Regression guard for the 2026-07 audit finding: the cloner silently
///     dropped ~15 persisted fields (arrangement properties, tone slots,
///     phrase infos, fret-hand mutes, EventPoint.Events, lyric phrase-ends,
///     stem mute/solo, per-arrangement artist metadata). Because the save,
///     crash-recovery and Rocksmith-export pipelines all serialize the CLONE,
///     any field missed here is wiped from disk on the next save. When adding
///     a domain field, extend this test alongside the cloner.
/// </summary>
public sealed class ChartProjectClonerTests
{
    private static ChartProject BuildFullyPopulatedProject()
    {
        var arrangement = new Arrangement
        {
            Name = "Lead",
            ToneBase = "base-tone",
            ToneA = "tone-a",
            ToneB = "tone-b",
            ToneC = "tone-c",
            ToneD = "tone-d",
            ArtistName = "Artist",
            ArtistNameSort = "artist",
            AlbumName = "Album",
            AlbumYear = 1998,
            Properties = new ArrangementProperties { Bends = 1, Tapping = 1, PalmMutes = 1, Represent = 0 }
        };
        arrangement.Phrases.Add("verse");
        arrangement.PhraseInfos["verse"] = new PhraseInfo(7, solo: true);
        arrangement.FretHandMuteTemplates.Add(new ChordTemplate { Name = "fhm" });
        arrangement.EventPoints.Add(new EventPoint
        {
            Phrase = "verse",
            Section = "verse",
            Events = "e0,e1",
            Position = new FractionalPosition(4, new Fraction(1, 2)),
            Status = SectionStatus.Review // v0.29 section tracker
        });

        var level = new ChartLevel { DifficultyIndex = 0 };
        level.Notes.Add(new GuitarNote { Fret = 5, StringIndex = 2 });
        level.FretHandMutes.Add(new FretHandMute(new FractionalPosition(2, new Fraction(1, 4))));
        arrangement.Levels.Add(level);

        var vocalTrack = new VocalTrack { Name = "Vocals" };
        var phrase = new LyricPhrase();
        phrase.Lyrics.Add(new LyricEvent { Text = "hel-", IsWordPart = true, IsPhraseEnd = true, Note = 60 });
        vocalTrack.Phrases.Add(phrase);

        var project = new ChartProject();
        project.Chart.Genre = "Rock";
        project.Chart.ReleaseDate = "2007-08-15";
        project.Chart.CoverArtPath = "cover.png";
        project.Arrangements.Add(arrangement);
        project.VocalTracks.Add(vocalTrack);
        project.Stems.Add(new Stem { Name = "drums", Muted = true, Solo = true, Volume = 0.5 });
        project.ToneDefinitions.Add(new ToneDefinition
        {
            Key = "dc_clean",
            Name = "DC Clean",
            Json = """{"Key":"dc_clean","Volume":"-12.000"}"""
        });
        return project;
    }

    [Fact]
    public void Clone_PreservesEveryPersistedField()
    {
        var source = BuildFullyPopulatedProject();

        var clone = ChartProjectCloner.Clone(source);

        Assert.Equal("Rock", clone.Chart.Genre);
        Assert.Equal("2007-08-15", clone.Chart.ReleaseDate);
        Assert.Equal("cover.png", clone.Chart.CoverArtPath);

        var arr = clone.Arrangements[0];
        Assert.Equal("base-tone", arr.ToneBase);
        Assert.Equal("tone-a", arr.ToneA);
        Assert.Equal("tone-b", arr.ToneB);
        Assert.Equal("tone-c", arr.ToneC);
        Assert.Equal("tone-d", arr.ToneD);
        Assert.Equal("Artist", arr.ArtistName);
        Assert.Equal("artist", arr.ArtistNameSort);
        Assert.Equal("Album", arr.AlbumName);
        Assert.Equal(1998, arr.AlbumYear);
        Assert.Equal(1, arr.Properties.Bends);
        Assert.Equal(1, arr.Properties.Tapping);
        Assert.Equal(1, arr.Properties.PalmMutes);
        Assert.Equal(0, arr.Properties.Represent);
        Assert.True(arr.PhraseInfos.TryGetValue("verse", out var info));
        Assert.Equal(7, info!.MaxDifficulty);
        Assert.True(info.Solo);
        Assert.Single(arr.FretHandMuteTemplates);
        Assert.Equal("fhm", arr.FretHandMuteTemplates[0].Name);
        Assert.Equal("e0,e1", arr.EventPoints[0].Events);
        Assert.Equal(SectionStatus.Review, arr.EventPoints[0].Status);
        Assert.Single(arr.Levels[0].FretHandMutes);
        Assert.Equal(source.Arrangements[0].Levels[0].FretHandMutes[0].Position,
            arr.Levels[0].FretHandMutes[0].Position);

        var lyric = clone.VocalTracks[0].Phrases[0].Lyrics[0];
        Assert.True(lyric.IsPhraseEnd);
        Assert.True(lyric.IsWordPart);

        var stem = clone.Stems[0];
        Assert.True(stem.Muted);
        Assert.True(stem.Solo);
        Assert.Equal(0.5, stem.Volume);

        var toneDefinition = Assert.Single(clone.ToneDefinitions);
        Assert.Equal("dc_clean", toneDefinition.Key);
        Assert.Equal("DC Clean", toneDefinition.Name);
        Assert.Equal("""{"Key":"dc_clean","Volume":"-12.000"}""", toneDefinition.Json);
    }

    [Fact]
    public void Clone_IsDeep_ForTheAuditedFields()
    {
        var source = BuildFullyPopulatedProject();
        var clone = ChartProjectCloner.Clone(source);
        var srcArr = source.Arrangements[0];
        var cloneArr = clone.Arrangements[0];

        cloneArr.Properties.Bends = 0;
        cloneArr.PhraseInfos["verse"].MaxDifficulty = 99;
        cloneArr.FretHandMuteTemplates[0].Name = "mutated";
        cloneArr.Levels[0].FretHandMutes.Clear();
        clone.ToneDefinitions[0].Json = "mutated";

        Assert.Equal(1, srcArr.Properties.Bends);
        Assert.Equal(7, srcArr.PhraseInfos["verse"].MaxDifficulty);
        Assert.Equal("fhm", srcArr.FretHandMuteTemplates[0].Name);
        Assert.Single(srcArr.Levels[0].FretHandMutes);
        Assert.Equal("""{"Key":"dc_clean","Volume":"-12.000"}""", source.ToneDefinitions[0].Json);
    }
}
