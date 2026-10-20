using System.Collections;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace DotCharter.Tone.Audio.Tonelab;

/// <summary>A process to create: the exe, its working folder, its whole environment, whether its window starts hidden, and an optional command-line tail.</summary>
public sealed record GameProcessRequest(string Exe, string WorkingDirectory, IReadOnlyDictionary<string, string> Environment, bool Hidden,
    string Arguments = "");

/// <summary>The created process's pid, or null and why.</summary>
public sealed record GameProcessCreated(int? ProcessId, string Error);

public interface IGameProcessCreator
{
    GameProcessCreated Create(GameProcessRequest request);
}

/// <summary>
///     The default start (tonelab's HANDOVER, sequence steps 1-2): the mirror's upkeep, then
///     <c>CreateProcessW</c> on the mirror's <c>Rocksmith2014.exe</c>, working folder = the mirror,
///     the window hidden from its first frame, and DotCharter's environment plus
///     <c>SteamAppId</c>/<c>SteamGameId</c> = 221680. Steam must be running; without it the start is
///     REFUSED with that reason (<see cref="GameToneLaunchFailure.SteamNotRunning" />). A game folder
///     that admits no mirror (another volume, not writable) makes this strategy unavailable, and the
///     launcher falls back to Steam on the live folder.
/// </summary>
public sealed class MirrorRocksmithStarter : IRocksmithStarter
{
    private readonly TonelabGameMirror _mirror;
    private readonly Func<string?> _gameDirectory;
    private readonly Func<bool> _steamRunning;
    private readonly IGameProcessCreator _creator;
    private readonly Func<IDictionary> _environment;

    public MirrorRocksmithStarter(TonelabGameMirror mirror, Func<string?> gameDirectory, Func<bool> steamRunning, IGameProcessCreator creator,
        Func<IDictionary> environment)
    {
        _mirror = mirror;
        _gameDirectory = gameDirectory;
        _steamRunning = steamRunning;
        _creator = creator;
        _environment = environment;
    }

    public bool UsesLiveGameFolder => false;

    /// <summary>The mirror the last start used, or null.</summary>
    public string? MirrorRoot { get; private set; }

    /// <summary><paramref name="current" /> plus <c>SteamAppId</c> and <c>SteamGameId</c> = 221680 (replacing any value already there).</summary>
    public static IReadOnlyDictionary<string, string> GameEnvironment(IDictionary current)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in current)
        {
            if (entry.Key is string key && entry.Value is string value) environment[key] = value;
        }

        environment["SteamAppId"] = RocksmithGame.SteamAppId;
        environment["SteamGameId"] = RocksmithGame.SteamAppId;
        return environment;
    }

    public Task<RocksmithStart> StartAsync(CancellationToken ct)
    {
        var mirror = _mirror.Prepare(_gameDirectory());
        switch (mirror.Outcome)
        {
            case GameMirrorOutcome.CrossVolume or GameMirrorOutcome.NoGame:
                return Task.FromResult(RocksmithStart.NotHere($"no mirror ({mirror.Outcome}: {mirror.Detail})"));
            case GameMirrorOutcome.Failed:
                return Task.FromResult(RocksmithStart.Refused(GameToneLaunchFailure.MirrorFailed, mirror.Detail));
        }

        MirrorRoot = mirror.Root;
        if (!_steamRunning())
        {
            return Task.FromResult(RocksmithStart.Refused(GameToneLaunchFailure.SteamNotRunning, "Steam is not running; start Steam first"));
        }

        var root = mirror.Root!;
        var created = _creator.Create(new GameProcessRequest(Path.Combine(root, TonelabGameMirror.ExeName), root, GameEnvironment(_environment()), true));
        return Task.FromResult(created.ProcessId is { } pid
            ? RocksmithStart.Running(pid)
            : RocksmithStart.Refused(GameToneLaunchFailure.StartFailed, created.Error));
    }
}

/// <summary>
///     <c>CreateProcessW</c> with <c>STARTF_USESHOWWINDOW</c> + <c>SW_HIDE</c> (a hidden request is
///     honoured from the first window; <see cref="System.Diagnostics.ProcessStartInfo.WindowStyle" />
///     is only read by the shell-execute path), a Unicode environment block, and
///     <c>CREATE_NO_WINDOW</c> (no console for a console child; a GUI exe ignores it). Both
///     returned handles are closed; the pid is the result.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class Win32GameProcessCreator : IGameProcessCreator
{
    internal const uint StartfUseShowWindow = 0x00000001;
    internal const ushort SwHide = 0;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNoWindow = 0x08000000;

    public static Win32GameProcessCreator Instance { get; } = new();

    public GameProcessCreated Create(GameProcessRequest request)
    {
        var startup = StartupFor(request.Hidden);
        var commandLine = ('"' + request.Exe + '"' + (request.Arguments.Length > 0 ? " " + request.Arguments : "") + '\0').ToCharArray();
        if (!CreateProcessW(request.Exe, commandLine, IntPtr.Zero, IntPtr.Zero, false, CreateUnicodeEnvironment | CreateNoWindow,
                EnvironmentBlock(request.Environment), request.WorkingDirectory, ref startup, out var info))
        {
            return new GameProcessCreated(null, new Win32Exception(Marshal.GetLastPInvokeError()).Message);
        }

        CloseHandle(info.Thread);
        CloseHandle(info.Process);
        return new GameProcessCreated(info.ProcessId, "");
    }

    /// <summary>The <c>STARTUPINFOW</c> of a start: hidden means <c>STARTF_USESHOWWINDOW</c> with <c>SW_HIDE</c>.</summary>
    internal static StartupInfo StartupFor(bool hidden) => new()
    {
        Size = (uint)Marshal.SizeOf<StartupInfo>(),
        Flags = hidden ? StartfUseShowWindow : 0,
        ShowWindow = SwHide
    };

    /// <summary>A Unicode environment block: <c>name=value\0</c> per variable, sorted by name (case-insensitive), ending in an extra <c>\0</c>.</summary>
    internal static char[] EnvironmentBlock(IReadOnlyDictionary<string, string> environment)
    {
        var block = new StringBuilder();
        foreach (var (key, value) in environment.OrderBy(e => e.Key.ToUpperInvariant(), StringComparer.Ordinal))
            block.Append(key).Append('=').Append(value).Append('\0');
        return block.Append('\0').ToString().ToCharArray();
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct StartupInfo
    {
        public uint Size;
        public IntPtr Reserved;
        public IntPtr Desktop;
        public IntPtr Title;
        public uint X;
        public uint Y;
        public uint XSize;
        public uint YSize;
        public uint XCountChars;
        public uint YCountChars;
        public uint FillAttribute;
        public uint Flags;
        public ushort ShowWindow;
        public ushort Reserved2Size;
        public IntPtr Reserved2;
        public IntPtr StdInput;
        public IntPtr StdOutput;
        public IntPtr StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateProcessW(string applicationName, [In, Out] char[] commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags, [In] char[] environment,
        string currentDirectory, ref StartupInfo startupInfo, out ProcessInformation processInformation);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);
}
