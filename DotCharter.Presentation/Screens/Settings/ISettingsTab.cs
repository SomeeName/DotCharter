using System.ComponentModel;

namespace DotCharter.Presentation.Screens.Settings;

/// <summary>One settings tab as the shell sees it: rail key, screen contract (null while the old page draws it),
/// the four footer verbs and the searchable rows. Registered by <see cref="SettingsModule.RegisterTab{TContract,TViewModel}" />.</summary>
public interface ISettingsTab : INotifyPropertyChanged
{
    string Key { get; }

    Type? Screen { get; }

    void Load();

    void Save();

    void Revert();

    void Reset();

    IReadOnlyList<SettingsSearchGroup> SearchGroups { get; }

    void ApplySearch(string query);
}

/// <summary>What the footer's unsaved count sums over: the settings that differ from the last baseline, as <c>tab.Setting</c> keys.</summary>
public interface ISettingsChangeSource : INotifyPropertyChanged
{
    IReadOnlyList<string> UnsavedSettings { get; }

    void Rebaseline();
}
