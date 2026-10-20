using DotCharter.Presentation.Shared.Library;
using DotCharter.Services.Gameplay.Scoring;
using DotCharter.Services.Library;

namespace DotCharter.Tests.Presentation.Library;

/// <summary>A plain <see cref="IPlayedLibrarySong" /> for the engine's pure tests: every fact
/// settable, nothing derived but the folded haystack, so a test states exactly the row it means.</summary>
public sealed record LibraryTestSong : IPlayedLibrarySong
{
    public string Artist { get; init; } = "Artist";
    public string Title { get; init; } = "Title";
    public string Album { get; init; } = "";
    public string Charter { get; init; } = "";
    public string SearchHaystack => LibraryQuery.Fold($"{Artist} {Title} {Album} {Charter}");
    public string TuningSummary { get; init; } = "E A D G B E";
    public string TuningCellLabel { get; init; } = "E Standard";
    public IReadOnlyList<string> ArrangementBadges { get; init; } = ["Lead"];
    public string PackName { get; init; } = "pack.psarc";
    public int Year { get; init; } = 1990;
    public double LengthSeconds { get; init; } = 200;
    public double Difficulty { get; init; } = 5;
    public int? Capo { get; init; } = 0;
    public LibraryOrigin? Origin { get; init; } = LibraryOrigin.Stock;
    public bool CanLoad { get; init; } = true;
    public bool IsLesson { get; init; }
    public bool HasBest { get; init; }
    public long BestScore { get; init; } = -1;
    public DateTime LastPlayedUtc { get; init; }
    public LetterGrade Grade { get; init; }
    public bool IsFullCombo { get; init; }
    public string? Genre { get; init; }

    /// <summary>A played row: a best score, a grade and a last run.</summary>
    public static LibraryTestSong Played(string title, long score, LetterGrade grade = LetterGrade.A,
        int daysAgo = 1, bool fullCombo = false) => new()
        {
            Title = title,
            HasBest = true,
            BestScore = score,
            Grade = grade,
            IsFullCombo = fullCombo,
            LastPlayedUtc = new DateTime(2026, 1, 31, 0, 0, 0, DateTimeKind.Utc).AddDays(-daysAgo)
        };
}

/// <summary>A row of the OTHER kind: catalogue facts only, no record — the Library's shape.</summary>
public sealed record LibraryCatalogueSong(string Artist, string Title) : ILibrarySong
{
    public string SearchHaystack => LibraryQuery.Fold($"{Artist} {Title}");
    public string TuningSummary => "";
    public string TuningCellLabel => "";
    public IReadOnlyList<string> ArrangementBadges => [];
    public string PackName => "";
    public int Year => 0;
    public double LengthSeconds => 0;
    public double Difficulty => 0;
    public int? Capo => null;
    public LibraryOrigin? Origin => null;
    public bool CanLoad => true;
    public bool IsLesson => false;
}
