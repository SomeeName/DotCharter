using DotCharter.Tone.Audio.Tonelab;

namespace DotCharter.Tests.Audio.Tonelab;

/// <summary>Files in memory, with a fault hook: the guard's tests never touch a game folder.</summary>
internal sealed class MemoryTonelabFileSystem : ITonelabFileSystem
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Throws <see cref="IOException" /> from a write to this path when set.</summary>
    public Func<string, byte[], bool>? FailWrite { get; set; }

    public bool Exists(string path)
    {
        lock (_files) return _files.ContainsKey(path);
    }

    public byte[] ReadAllBytes(string path)
    {
        lock (_files) return _files.TryGetValue(path, out var bytes) ? [.. bytes] : throw new FileNotFoundException(path);
    }

    public void WriteAllBytes(string path, byte[] bytes)
    {
        if (FailWrite?.Invoke(path, bytes) == true) throw new IOException("disk full (test)");
        lock (_files) _files[path] = [.. bytes];
    }

    public void Delete(string path)
    {
        lock (_files) _files.Remove(path);
    }
}

/// <summary>One fake Rocksmith process: started by the starter, ended by quit (when it honours it) or by Kill.</summary>
internal sealed class FakeRocksmith : IRocksmithProcesses, IRocksmithStarter
{
    private TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _running;

    public int Pid { get; init; } = 4242;

    /// <summary>The starter's answer; false = Steam refused.</summary>
    public bool StartWorks { get; init; } = true;

    /// <summary>Whether a started game ever shows a process.</summary>
    public bool ShowsProcess { get; init; } = true;

    public bool HonoursQuit { get; init; } = true;

    public int Starts { get; private set; }

    public int Kills { get; private set; }

    public bool Running => _running;

    /// <summary>A game the user started before DotCharter asked.</summary>
    public void RunByUser() => Run();

    public void Run()
    {
        _exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _running = true;
    }

    public void Exit()
    {
        _running = false;
        _exited.TrySetResult();
    }

    /// <summary>The pipe's <c>quit</c>, as the session would send it.</summary>
    public Task<bool> SendQuit(CancellationToken ct)
    {
        if (HonoursQuit) Exit();
        return Task.FromResult(true);
    }

    public int? FindGame() => _running ? Pid : null;

    public async Task<bool> WaitForExitAsync(int processId, TimeSpan timeout, CancellationToken ct)
    {
        if (!_running) return true;
        var exited = _exited.Task;
        if (timeout == Timeout.InfiniteTimeSpan) await exited.WaitAsync(ct);
        else await Task.WhenAny(exited, Task.Delay(timeout, ct));
        return !_running;
    }

    public bool Kill(int processId)
    {
        Kills++;
        Exit();
        return true;
    }

    /// <summary>Like Steam's start: the live folder, and the pid is found afterwards.</summary>
    public bool UsesLiveGameFolder => true;

    public Task<RocksmithStart> StartAsync(CancellationToken ct)
    {
        Starts++;
        if (StartWorks && ShowsProcess) Run();
        return Task.FromResult(StartWorks ? RocksmithStart.Requested : RocksmithStart.Refused(GameToneLaunchFailure.StartFailed, "Steam refused (test)"));
    }
}

/// <summary>The mirror's direct start, scripted: it answers <paramref name="answer" />, and a <c>Running</c> answer runs <paramref name="game" />.</summary>
internal sealed class FakeMirrorStarter(RocksmithStart answer, FakeRocksmith game) : IRocksmithStarter
{
    public int Starts { get; private set; }

    public bool UsesLiveGameFolder => false;

    public Task<RocksmithStart> StartAsync(CancellationToken ct)
    {
        Starts++;
        if (answer.ProcessId is not null) game.Run();
        return Task.FromResult(answer);
    }
}

/// <summary>Records each helper call and answers with a scripted run.</summary>
internal sealed class FakeHelperRunner(HelperProcessRun answer) : IHelperProcessRunner
{
    public List<(string Exe, IReadOnlyList<string> Arguments, TimeSpan Timeout)> Calls { get; } = [];

    public Exception? Throw { get; init; }

    public Task<HelperProcessRun> RunAsync(string exe, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        Calls.Add((exe, arguments, timeout));
        return Throw is { } ex ? Task.FromException<HelperProcessRun>(ex) : Task.FromResult(answer);
    }

    public static FakeHelperRunner Ok() => new(new HelperProcessRun(0, "ok\r\n", false));
}
