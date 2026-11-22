using DotCharter.Core.Domain.Project;
using DotCharter.Core.Domain.Vocals;

namespace DotCharter.Core.Domain.Validation;

/// <summary>A syllable's place in a project: vocal track, phrase and syllable index.</summary>
public sealed record LyricPlace(int Track, int Phrase, int Syllable)
{
    /// <summary>The vocal track and syllable at this place in <paramref name="project" />, or null when
    /// the project has none there.</summary>
    public (VocalTrack Track, LyricEvent Lyric)? Find(ChartProject project)
    {
        if (Track < 0 || Track >= project.VocalTracks.Count) return null;
        var track = project.VocalTracks[Track];
        if (Phrase < 0 || Phrase >= track.Phrases.Count) return null;
        var lyrics = track.Phrases[Phrase].Lyrics;
        return Syllable < 0 || Syllable >= lyrics.Count ? null : (track, lyrics[Syllable]);
    }
}
