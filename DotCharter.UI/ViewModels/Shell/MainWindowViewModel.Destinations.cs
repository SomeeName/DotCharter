// The shell's side of DotCharter.Presentation's navigation seam.

using DotCharter.Presentation.Contracts.Shell;

namespace DotCharter.UI.ViewModels.Shell;

/// <summary>
///     <see cref="IShellDestinations" /> on the window's view model.
///     <para>
///         A screen in <c>DotCharter.Presentation</c> cannot reach this type —
///         that project may not see Avalonia — so it declares the seam and the
///         shell implements it, the same shape as <c>IChartEditorSurface</c>
///         and <c>IMessagePrompt</c>. Every member writes state the activity
///         rail is already bound to, so a screen navigating and a user clicking
///         the rail are the same event.
///     </para>
/// </summary>
public partial class MainWindowViewModel : IShellDestinations
{
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
