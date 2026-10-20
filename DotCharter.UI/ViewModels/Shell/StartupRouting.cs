namespace DotCharter.UI.ViewModels.Shell;

/// <summary>
///     The ONE place the shell asks where to boot. <see cref="MainWindowViewModel"/>'s
///     constructor routes on this; the onboarding wizard's role choice and
///     Settings ▸ General ▸ "Open at startup" both write the
///     <see cref="AppConfig.StartupScreen"/> it reads. A pure function so the
///     Charter → editor / Player → Play Mode mapping is testable without the 26
///     services the shell view model takes.
/// </summary>
/// <summary>The surface a boot lands on.</summary>
public enum StartupSurface
{
    Editor,
    Start,
    Home
}

public static class StartupRouting
{
    /// <summary>True when the shell should open on the Play Mode front door;
    /// false means the chart editor workspace (the shell's tab 0, which needs
    /// no action to land on).</summary>
    public static bool BootsIntoPlayMode(AppConfig config) =>
        config.StartupScreen == StartupScreen.PlayMode;

    /// <summary>
    ///     Where a boot lands: the editor for a Charter, and for a Player the Start
    ///     screen, every time — its Start leads on to Home. <paramref name="profileCount" /> does not
    ///     decide it; the Start screen's who-is-playing panel does its own showing.
    /// </summary>
    public static StartupSurface SurfaceFor(AppConfig config, int profileCount) =>
        BootsIntoPlayMode(config) ? StartupSurface.Start : StartupSurface.Editor;

    /// <summary>What the wizard's role choice means for startup: a Charter
    /// wants the editor, a Player wants the game.</summary>
    public static StartupScreen ScreenForRole(bool isCharter) =>
        isCharter ? StartupScreen.Editor : StartupScreen.PlayMode;
}
