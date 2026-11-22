// The shell's side of DotCharter.Presentation's navigation seam.

using DotCharter.Presentation.Contracts.Shell;

namespace DotCharter.UI.ViewModels.Shell;

/// <summary>Routes screen navigation through the shell state bound to the activity rail.</summary>
public partial class MainWindowViewModel : IShellDestinations
{
    private void OnProjectOpened()
    {
        PlayMode.EndForProjectOpen();
        ShowEditor();
    }

    private void ShowEditor()
    {
        IsGameplayPresentation = false;
        SelectedMainTabIndex = ChartEditorTabIndex;
    }

    public void Go(ShellDestination destination)
    {
        // Home's Play opens the song library: Start is behind Home, not ahead of it.
        if (destination == ShellDestination.Play && PlayMode.IsStartScreen)
            PlayMode.OpenSongSelectCommand.Execute(null);

        SelectedMainTabIndex = destination switch
        {
            ShellDestination.Home => HomeTabIndex,
            ShellDestination.Library => SongBrowserTabIndex,
            ShellDestination.Chart => ChartEditorTabIndex,
            ShellDestination.Play => PlayModeTabIndex,
            _ => SettingsTabIndex
        };
    }

    public void OpenRig() => ToneRig.IsPedalboardOpen = true;

    public async Task PlaySongAsync(string songKey, string arrangementName)
    {
        await PlayMode.OpenSongAsync(songKey, arrangementName);
        SelectedMainTabIndex = PlayModeTabIndex;
    }

    public void OpenCommandPalette() => CommandPalette.Open();
}
