namespace DotCharter.Presentation.Contracts.Shell;

/// <summary>The app's top-level places — one per row of the activity rail.</summary>
public enum ShellDestination
{
    Home,
    Library,
    Chart,
    Play,
    Settings
}

/// <summary>
///     "Take me somewhere else in the app."
///     <para>
///         The shell owns which top-level surface is showing, and the shell is
///         Avalonia. So a screen that wants to send the user elsewhere declares
///         the seam and <c>DotCharter.UI</c> implements it — the same shape as
///         <c>IMessagePrompt</c> and <c>IChartEditorSurface</c>. Without it, the
///         Home screen's destination cards would need the window's view model,
///         which this project may not reference.
///     </para>
///     <para>
///         Calls land on the UI thread and must never throw.
///         <see cref="OpenRig" /> opens an OVERLAY over whatever is
///         showing rather than a destination, which is why it is not one.
///     </para>
/// </summary>
public interface IShellDestinations
{
    void Go(ShellDestination destination);

    /// <summary>Loads the song identified by its statistics key and opens Get ready on its recorded path.</summary>
    Task PlaySongAsync(string songKey, string arrangementName);

    void OpenRig();

    /// <summary>"Go to anything" — the command palette.</summary>
    void OpenCommandPalette();
}
