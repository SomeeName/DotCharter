using System.Globalization;

namespace DotCharter.Tone.Audio.Tonelab;

/// <summary>What one tonelab command came to, as the UI can show it.</summary>
public enum TonelabOutcome
{
    /// <summary>The game answered <c>ok</c> with the reply the command expects.</summary>
    Ok,

    /// <summary>The game answered <c>err &lt;reason&gt;</c> with a reason other than the ones below.</summary>
    Rejected,

    /// <summary><c>err unknown-gear &lt;key&gt;</c>: the game has no gear by that key.</summary>
    UnknownGear,

    /// <summary>No reply line inside the command's timeout; the connection is dropped (a late reply would desync it).</summary>
    Timeout,

    /// <summary>No pipe to talk to: never connected, or the game went away mid-command.</summary>
    NotConnected,

    /// <summary>A reply that is neither <c>ok …</c> nor <c>err …</c>, or an <c>ok</c> of the wrong shape.</summary>
    Malformed,

    /// <summary>Refused before sending: the tone fails the gear catalogue (<see cref="TonelabResult.Detail" /> says why).</summary>
    Invalid,

    /// <summary><c>status</c> named a protocol version other than <see cref="TonelabProtocol.Version" />; nothing else is sent.</summary>
    UnsupportedVersion
}

/// <summary>
///     One command's typed result. <see cref="Detail" /> is the payload of an <c>ok</c>
///     (the tone key of <c>ok applied</c>) or the reason of a failure (the gear key of
///     <c>unknown-gear</c>, the rest of an <c>err</c> line); <see cref="Raw" /> is the
///     reply line verbatim, for the log.
/// </summary>
public sealed record TonelabResult(TonelabOutcome Outcome, string Detail = "", string Raw = "")
{
    public bool IsOk => Outcome == TonelabOutcome.Ok;

    /// <summary>The <c>err</c> reason token (<c>unknown-gear</c>, …), or empty.</summary>
    public string ErrorCode { get; init; } = "";

    public static TonelabResult NotConnected(string why = "") => new(TonelabOutcome.NotConnected, why);

    public static TonelabResult Timeout(string command) => new(TonelabOutcome.Timeout, command);
}

/// <summary>The game side's state as <c>status</c> reports it.</summary>
public enum TonelabGameState
{
    Unknown,

    /// <summary><c>ok starting</c>: injected, but the profile or the tone engine is not up yet.</summary>
    Starting,

    /// <summary><c>ok ready &lt;rate&gt; &lt;block&gt;</c>: tones and audio can flow.</summary>
    Ready
}

/// <summary>
///     A parsed <c>status</c> reply (protocol v1, tonelab PROTOCOL.md):
///     <c>ok ready v1 &lt;rate&gt; &lt;block&gt; &lt;offset&gt;</c>. <see cref="BlockOffset" /> is the measured
///     in/out block offset, -1 until the game has measured one.
/// </summary>
public sealed record TonelabStatus(TonelabResult Result, TonelabGameState State, int Version, int SampleRate, int BlockFrames, int BlockOffset)
{
    public static TonelabStatus Failed(TonelabResult result) => new(result, TonelabGameState.Unknown, 0, 0, 0, 0);
}

/// <summary>
///     The tonelab command-pipe wire format (protocol draft v0): pipe and shared-memory
///     names, one command per line, one reply line per command, <c>ok …</c> or
///     <c>err &lt;reason&gt; …</c>. Pure: strings in, typed results out.
/// </summary>
public static class TonelabProtocol
{
    public const string PipePrefix = "tonelab-";

    /// <summary>The protocol version this host speaks; <c>status</c> must name it.</summary>
    public const int Version = 1;

    /// <summary>The game render rate the shared memory carries (the game's internal rate).</summary>
    public const int SampleRate = 48000;

    public const string UnknownGearCode = "unknown-gear";

    /// <summary>The 8-byte magic the shared-memory header starts with (<c>"TONELAB\0"</c>), handed to the generic native insert.</summary>
    public const string HeaderMagic = "TONELAB";

    /// <summary>The pipe name for a game process, without the <c>\\.\pipe\</c> prefix.</summary>
    public static string PipeNameFor(int processId) => PipePrefix + processId.ToString(CultureInfo.InvariantCulture);

    /// <summary>The shared-memory block's name for a game process.</summary>
    public static string SharedMemoryNameFor(int processId) => @"Local\" + PipeNameFor(processId);

    /// <summary>The process id a pipe name belongs to, or null when the name is not a tonelab pipe.</summary>
    public static int? ProcessIdOf(string pipeName)
    {
        if (!pipeName.StartsWith(PipePrefix, StringComparison.Ordinal)) return null;
        var digits = pipeName.AsSpan(PipePrefix.Length);
        if (digits.IsEmpty) return null;
        foreach (var c in digits)
            if (c is < '0' or > '9') return null;
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) && pid > 0 ? pid : null;
    }

    public static string Status() => "status";

    /// <summary>The rest of the line is the path, spaces included; a line break in it cannot be sent.</summary>
    public static string ApplyFile(string path) => "tone apply-file " + Arg(path, nameof(path), allowSpaces: true);

    /// <summary><paramref name="gearSlot" /> is the GearList slot (<c>Amp</c>, <c>PrePedal2</c>, …), <paramref name="knobKey" /> the full RS knob key.</summary>
    public static string Knob(string gearSlot, string knobKey, float value) =>
        "tone knob " + Arg(gearSlot, nameof(gearSlot)) + " " + Arg(knobKey, nameof(knobKey)) + " " +
        value.ToString("0.######", CultureInfo.InvariantCulture);

    public static string Restore() => "tone restore";

    /// <summary>Asks the game to write its ACTIVE tone as JSON to <paramref name="path" /> (e.g. the song's own tone).</summary>
    public static string Dump(string path) => "tone dump " + Arg(path, nameof(path), allowSpaces: true);

    /// <summary><c>perf hidden_fps|visible_fps &lt;n&gt;</c>: the game's main-loop cap, 0 = none.</summary>
    public static string Perf(bool whileHidden, int framesPerSecond)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(framesPerSecond);
        return (whileHidden ? "perf hidden_fps " : "perf visible_fps ") + framesPerSecond.ToString(CultureInfo.InvariantCulture);
    }

    public static string MixMute(bool on) => on ? "mix mute on" : "mix mute off";

    public static string Window(bool show) => show ? "window show" : "window hide";

    public static string Quit() => "quit";

    /// <summary>
    ///     Reads a reply that must be <c>ok</c> followed by <paramref name="expectedOkWords" />
    ///     (e.g. <c>applied</c>); the tokens after them become <see cref="TonelabResult.Detail" />.
    /// </summary>
    public static TonelabResult Parse(string? line, params string[] expectedOkWords)
    {
        if (line is null) return TonelabResult.NotConnected("the pipe closed before a reply");
        var raw = line.TrimEnd('\r');
        var words = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return new TonelabResult(TonelabOutcome.Malformed, raw, raw);

        switch (words[0])
        {
            case "ok":
            {
                if (words.Length - 1 < expectedOkWords.Length) return new TonelabResult(TonelabOutcome.Malformed, raw, raw);
                for (var i = 0; i < expectedOkWords.Length; i++)
                {
                    if (!string.Equals(words[i + 1], expectedOkWords[i], StringComparison.Ordinal))
                        return new TonelabResult(TonelabOutcome.Malformed, raw, raw);
                }

                return new TonelabResult(TonelabOutcome.Ok, string.Join(' ', words.Skip(1 + expectedOkWords.Length)), raw);
            }
            case "err":
            {
                if (words.Length < 2) return new TonelabResult(TonelabOutcome.Malformed, raw, raw);
                var code = words[1];
                var detail = string.Join(' ', words.Skip(2));
                // err timeout: the game is not ticking (loading screen, debugger); the next edit simply resends.
                var outcome = code switch
                {
                    UnknownGearCode => TonelabOutcome.UnknownGear,
                    "timeout" => TonelabOutcome.Timeout,
                    _ => TonelabOutcome.Rejected
                };
                return new TonelabResult(outcome, detail, raw) { ErrorCode = code };
            }
            default:
                return new TonelabResult(TonelabOutcome.Malformed, raw, raw);
        }
    }

    /// <summary>
    ///     <c>ok starting</c>, or strictly <c>ok ready v1 &lt;rate&gt; &lt;block&gt; &lt;offset&gt;</c>. A ready reply
    ///     naming another version is <see cref="TonelabOutcome.UnsupportedVersion" /> (its
    ///     <see cref="TonelabResult.Detail" /> is the version token); any other shape is malformed.
    ///     <c>ok starting v1</c> and the bare <c>ok starting</c> of tonelab M2 both read as starting.
    /// </summary>
    public static TonelabStatus ParseStatus(string? line)
    {
        var result = Parse(line);
        if (!result.IsOk) return TonelabStatus.Failed(result);
        var words = result.Detail.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        switch (words)
        {
            case ["starting"]:
                return new TonelabStatus(result, TonelabGameState.Starting, 0, 0, 0, -1);
            case ["starting", var startingVersion]:
                return ParseVersion(startingVersion) switch
                {
                    null => Malformed(result),
                    Version => new TonelabStatus(result, TonelabGameState.Starting, Version, 0, 0, -1),
                    _ => Unsupported(result, startingVersion)
                };
            case ["ready", var version, var rateText, var blockText, var offsetText]:
            {
                var number = ParseVersion(version);
                if (number is null) return Malformed(result);
                if (number != Version) return Unsupported(result, version);
                if (!int.TryParse(rateText, NumberStyles.None, CultureInfo.InvariantCulture, out var rate) || rate <= 0 ||
                    !int.TryParse(blockText, NumberStyles.None, CultureInfo.InvariantCulture, out var block) || block <= 0 ||
                    !int.TryParse(offsetText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var offset) || offset < -1)
                {
                    return Malformed(result);
                }

                return new TonelabStatus(result, TonelabGameState.Ready, Version, rate, block, offset);
            }
            default:
                return Malformed(result);
        }
    }

    private static int? ParseVersion(string token) =>
        token.Length >= 2 && token[0] == 'v' && int.TryParse(token.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;

    private static TonelabStatus Malformed(TonelabResult result) => TonelabStatus.Failed(result with { Outcome = TonelabOutcome.Malformed });

    private static TonelabStatus Unsupported(TonelabResult result, string version) =>
        TonelabStatus.Failed(result with { Outcome = TonelabOutcome.UnsupportedVersion, Detail = version });

    private static string Arg(string value, string name, bool allowSpaces = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        if (value.AsSpan().IndexOfAny('\r', '\n') >= 0) throw new ArgumentException("A tonelab argument cannot hold a line break.", name);
        if (!allowSpaces && value.Contains(' ')) throw new ArgumentException("A tonelab argument cannot hold a space.", name);
        return value;
    }
}
