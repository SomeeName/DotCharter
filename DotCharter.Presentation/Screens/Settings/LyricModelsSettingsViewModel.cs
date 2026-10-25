using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Settings;
using DotCharter.Services.Audio.Lyrics;
using DotCharter.Tone.Diagnostics;

namespace DotCharter.Presentation.Screens.Settings;

/// <summary>
///     Settings ▸ AI &amp; models ▸ Lyric auto-sync (<see cref="SettingsRail.Hosted" />).
///     A tab without settings: Load and Revert re-read the models' state, and
///     nothing is ever unsaved.
/// </summary>
public sealed class LyricModelsSettingsViewModel : ObservableObject,
    ILyricModelsSettingsScreen, ISettingsTab, ISettingsChangeSource
{
    private static readonly SettingsTabRows Rows = new("lyricModels.",
    [
        new("Settings_LyricModels_Title", "Settings_LyricModels_Subtitle",
            new(LyricModelCatalog.MultilingualId, "Settings_LyricModels_Multilingual", "Settings_LyricModels_Multilingual_Desc"),
            new(LyricModelCatalog.EnglishId, "Settings_LyricModels_English", "Settings_LyricModels_English_Desc"))
    ]);

    private readonly ILyricModelDownloadService _models;
    private readonly ILocalizationService _localization;
    private readonly List<LyricModelRowViewModel> _rows;

    public LyricModelsSettingsViewModel(ILyricModelDownloadService models, ILocalizationService localization, IUiDispatcher ui)
    {
        _models = models;
        _localization = localization;
        _rows =
        [
            new(Rows.RowKey(LyricModelCatalog.MultilingualId), LyricModelCatalog.MultilingualId,
                "Settings_LyricModels_Multilingual", models, localization, ui),
            new(Rows.RowKey(LyricModelCatalog.EnglishId), LyricModelCatalog.EnglishId,
                "Settings_LyricModels_English", models, localization, ui)
        ];
        Refresh();
    }

    public IReadOnlyList<ILyricModelRow> Models => _rows;

    public IReadOnlySet<string> HiddenKeys { get; private set; } = new HashSet<string>();

    public string Key => SettingsTabKeys.LyricModels;

    public Type? Screen => typeof(ILyricModelsSettingsScreen);

    public IReadOnlyList<string> UnsavedSettings => [];

    public IReadOnlyList<SettingsSearchGroup> SearchGroups => Rows.Localize(_localization);

    /// <summary>The key a view binds a whole group's visibility to.</summary>
    public static string GroupKey(int index) => Rows.GroupKey(index);

    public void Load() => Refresh();

    /// <summary>Nothing to write: a download or a GPU retry takes effect when it is pressed.</summary>
    public void Save()
    {
    }

    public void Revert() => Refresh();

    public void Reset() => Refresh();

    public void Rebaseline()
    {
    }

    public void ApplySearch(string query)
    {
        HiddenKeys = Rows.Hidden(_localization, query, out _);
        foreach (var row in _rows) row.IsShown = !HiddenKeys.Contains(row.Key);
        OnPropertyChanged(nameof(HiddenKeys));
    }

    private void Refresh()
    {
        var states = _models.Models;
        foreach (var row in _rows) row.Apply(states.FirstOrDefault(s => s.Id == row.Id));
    }
}
