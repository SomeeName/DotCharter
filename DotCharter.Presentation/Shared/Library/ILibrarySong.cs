using DotCharter.Services.Gameplay.Scoring;
using DotCharter.Services.Library;

namespace DotCharter.Presentation.Shared.Library;

/// <summary>One song row as <see cref="LibraryQuery" /> reads it — the Library's rows and Song Select's
/// both implement it. Unknown values: blank text, 0 or less for numbers, null capo and origin.</summary>
public interface ILibrarySong
{
    string Artist { get; }
    string Title { get; }

    /// <summary>"artist title album charter", put through <see cref="LibraryQuery.Fold" /> once per row.</summary>
    string SearchHaystack { get; }

    string TuningSummary { get; }
    string TuningCellLabel { get; }
    IReadOnlyList<string> ArrangementBadges { get; }
    string PackName { get; }
    int Year { get; }
    double LengthSeconds { get; }
    double Difficulty { get; }
    int? Capo { get; }
    LibraryOrigin? Origin { get; }
    bool CanLoad { get; }
    bool IsLesson { get; }
}

/// <summary>A row that also carries the active profile's record (Song Select's). A row without
/// it is unplayed and has no genre.</summary>
public interface IPlayedLibrarySong : ILibrarySong
{
    bool HasBest { get; }

    /// <summary>Below zero when unplayed.</summary>
    long BestScore { get; }

    DateTime LastPlayedUtc { get; }
    LetterGrade Grade { get; }
    bool IsFullCombo { get; }
    string? Genre { get; }
}
