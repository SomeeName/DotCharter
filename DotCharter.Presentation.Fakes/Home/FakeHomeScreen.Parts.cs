using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Home;

namespace DotCharter.Presentation.Fakes.Home;

/// <summary>The item rows, and the shorthand the factories build them with.</summary>
public sealed partial class FakeHomeScreen
{
    private static IReadOnlyList<IArrangementChip> Chips(params (string Label, bool IsBest)[] rows) =>
        [.. rows.Select(r => new Chip(r.Label, r.IsBest))];

    private static IReadOnlyList<IProgressRow> Rows(params (string Title, string Progress)[] rows) =>
        [.. rows.Select(r => new Progress(r.Title, r.Progress))];

    /// <summary>The four destinations, whose subtitles are the only thing that varies between states.</summary>
    private static IReadOnlyList<IDestinationCard> StandardDestinations(
        string firstSubtitle = "Pick a song and play it with the highway",
        string chartSubtitle = "4 charts in progress, 3 with export blockers",
        string rigSubtitle = "Crunch lead, edited, 8 units, scene A",
        string librarySubtitle = "312 songs, 12 with audio and no chart yet") =>
    [
        new Destination("Play", firstSubtitle, "IconGuitar", IsPrimary: true),
        new Destination("Chart", chartSubtitle, "IconChartLine", IsPrimary: false),
        new Destination("Rig", rigSubtitle, "IconAmp", IsPrimary: false),
        new Destination("Library", librarySubtitle, "IconLibraryMusic", IsPrimary: false)
    ];

    private sealed record Chip(string Label, bool IsBest) : IArrangementChip;

    private sealed record Progress(string Title, string ProgressLabel) : IProgressRow
    {
        public string ArtPath => "";

        public bool HasArt => ArtPath.Length > 0;

        public ICommand OpenCommand { get; } = new RelayCommand(() => { });
    }

    private sealed record Destination(string Title, string Subtitle, string IconKey, bool IsPrimary)
        : IDestinationCard
    {
        public ICommand OpenCommand { get; } = new RelayCommand(() => { });
    }
}
