// One row of the File ▸ Open Recent submenu and the editor launch card's list.
using CommunityToolkit.Mvvm.Input;

namespace DotCharter.UI.ViewModels.Shell;

/// <summary>
///     One row of the File ▸ Open Recent submenu (and its command-palette
///     twins). Carries its OWN open command so the dynamic menu items need no
///     ancestor-DataContext lookup; opening goes through the SAME funnel as
///     File ▸ Open (<c>LoadFile</c> → the discard guard → the replace rules).
/// </summary>
public sealed class RecentProjectItem
{
    public RecentProjectItem(string path, string displayName, Func<string, Task> open)
    {
        Path = path;
        DisplayName = displayName;
        OpenCommand = new AsyncRelayCommand(() => open(path));
    }

    public string Path { get; }
    public string DisplayName { get; }
    public IAsyncRelayCommand OpenCommand { get; }
}
