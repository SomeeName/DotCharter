namespace DotCharter.Tone.Audio.Tonelab;

/// <summary>
///     Starts Rocksmith with tonelab injected (or injects into the running game). The public
///     implementation is <see cref="TonelabGameLauncher" />, registered only when the install
///     carries <c>tools/tonelab/</c> (docs/agents/services/tonelab-bridge.md § Lifecycle).
///     Without one, the designer asks the user to start the game with tonelab and press Reconnect.
/// </summary>
public interface IGameToneLauncher
{
    /// <summary>The running launch once tonelab is injected, or null when it failed (the reason is logged by the implementation).</summary>
    Task<IGameToneLaunch?> LaunchAsync(CancellationToken ct);

    /// <summary>Why the last <see cref="LaunchAsync" /> returned null; <see cref="GameToneLaunchFailure.None" /> after a launch that worked.</summary>
    GameToneLaunchFailure LastFailure => GameToneLaunchFailure.None;
}

/// <summary>Why a launch failed, for the reason the UI prints.</summary>
public enum GameToneLaunchFailure
{
    None,

    /// <summary>The direct start needs the Steam client running; it is not.</summary>
    SteamNotRunning,

    /// <summary>The tone-server folder could not be brought up to date (the log names the file).</summary>
    MirrorFailed,

    /// <summary>RS_ASIO.ini of the live folder could not be prepared (or the game started meanwhile).</summary>
    IniRefused,

    /// <summary>The game did not start, or no process showed up.</summary>
    StartFailed,

    /// <summary>The helper could not inject tonelab into the game.</summary>
    InjectFailed
}

/// <summary>One launch: the game's pid, who started it, and how it ends.</summary>
public interface IGameToneLaunch
{
    int ProcessId { get; }

    /// <summary>True when the game runs with the driverless RS_ASIO.ini (or without RS ASIO): it opens NO ASIO device, so DotCharter keeps its own.</summary>
    bool GameOpensNoAudioDevice { get; }

    /// <summary>True when DotCharter started the game, so DotCharter ends it; false for a game the user started, which is only ever detached.</summary>
    bool StartedByApp { get; }

    /// <summary>
    ///     Ends a game DotCharter started: <paramref name="sendQuit" /> (the pipe's <c>quit</c>), a few
    ///     seconds for the game to exit, only then the process is ended, and RS_ASIO.ini is put back.
    ///     For a game the user started this does nothing at all.
    /// </summary>
    Task EndAsync(Func<CancellationToken, Task<bool>> sendQuit, CancellationToken ct);
}
