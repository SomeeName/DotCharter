using System.Collections.Concurrent;
using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using DotCharter.Tone.Audio.Tonelab;

namespace DotCharter.Tests.Audio.Tonelab;

/// <summary>
///     A loopback stand-in for the game side (tonelab.dll inside Rocksmith), TEST-ONLY: it
///     never ships. It serves <c>\\.\pipe\tonelab-&lt;pid&gt;</c> exactly as protocol draft v0
///     describes (one reply line per command line, a new connection accepted after each
///     one ends) and, when asked, creates the <c>Local\tonelab-&lt;pid&gt;</c> shared-memory
///     block and "renders" every input block as the input times <see cref="Gain" />, delayed
///     by <see cref="DelaySamples" />, on both output channels. The pid is a fake number, so
///     nothing here can collide with a real game.
/// </summary>
internal sealed class TonelabGameMock : IDisposable
{
    private static int _nextPid = 1_900_000_000 + Environment.ProcessId % 10_000 * 1000;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _serve;
    private NamedPipeServerStream? _current;
    private TonelabSharedMemoryMock? _audio;

    public TonelabGameMock(int? processId = null)
    {
        ProcessId = processId ?? Interlocked.Increment(ref _nextPid);
        PipeName = TonelabProtocol.PipeNameFor(ProcessId);
        // The first server exists before the constructor returns, so a client never races its creation.
        var first = NewServer();
        _current = first;
        _serve = Task.Run(() => ServeAsync(first));
    }

    public int ProcessId { get; }

    public string PipeName { get; }

    /// <summary>What <c>status</c> answers after <c>ok </c>.</summary>
    public string Status { get; set; } = "ready v1 48000 128 0";

    /// <summary>Gear keys <c>apply-file</c> refuses with <c>err unknown-gear</c>.</summary>
    public HashSet<string> UnknownGear { get; } = new(StringComparer.Ordinal);

    /// <summary>Overrides the reply to a command line; return null to never answer it (a timeout).</summary>
    public Func<string, string?>? Responder { get; set; }

    public ConcurrentQueue<string> Received { get; } = new();

    /// <summary>Each applied file's text, read when the command arrived (the host may delete it afterwards).</summary>
    public ConcurrentQueue<string> AppliedBodies { get; } = new();

    private int _connections;

    public int Connections => Volatile.Read(ref _connections);

    public float Gain { get; set; } = 0.5f;

    public int DelaySamples { get; set; } = 3;

    public TonelabSharedMemoryMock CreateAudio(int blockFrames = 128, int ringBlocks = 32) =>
        _audio = new TonelabSharedMemoryMock(TonelabProtocol.SharedMemoryNameFor(ProcessId), blockFrames, ringBlocks, Gain, DelaySamples);

    /// <summary>The game crashes: the pipe and the shared memory go away at once.</summary>
    public void Dispose()
    {
        _stop.Cancel();
        _current?.Dispose();
        _audio?.Dispose();
        try
        {
            _serve.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
        }
    }

    public string[] Commands() => [.. Received];

    private NamedPipeServerStream NewServer() =>
        new(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

    private async Task ServeAsync(NamedPipeServerStream? first)
    {
        while (!_stop.IsCancellationRequested)
        {
            var server = first ?? NewServer();
            first = null;
            _current = server;
            try
            {
                await server.WaitForConnectionAsync(_stop.Token);
                Interlocked.Increment(ref _connections);
                using var reader = new StreamReader(server, new UTF8Encoding(false), false, 1024, leaveOpen: true);
                while (await reader.ReadLineAsync(_stop.Token) is { } line)
                {
                    Received.Enqueue(line);
                    var reply = Responder is { } custom ? custom(line) : Answer(line);
                    if (reply is null) continue;
                    var bytes = Encoding.UTF8.GetBytes(reply + "\n");
                    await server.WriteAsync(bytes, _stop.Token);
                    await server.FlushAsync(_stop.Token);
                    if (line == "quit") break;
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
            {
            }
            finally
            {
                await server.DisposeAsync();
            }
        }
    }

    private string Answer(string line)
    {
        var words = line.Split(' ');
        switch (words)
        {
            case ["status"]:
                return "ok " + Status;
            case ["tone", "apply-file", ..]:
            {
                var path = line["tone apply-file ".Length..];
                if (!File.Exists(path)) return "err no-file " + path;
                var body = File.ReadAllText(path);
                AppliedBodies.Enqueue(body);
                using var doc = JsonDocument.Parse(body);
                foreach (var slot in doc.RootElement.GetProperty("GearList").EnumerateObject())
                {
                    var key = slot.Value.GetProperty("Key").GetString() ?? "";
                    if (UnknownGear.Contains(key)) return "err unknown-gear " + key;
                }

                return "ok applied " + doc.RootElement.GetProperty("Key").GetString();
            }
            case ["tone", "knob", _, _, _]:
                return "ok";
            case ["tone", "dump", ..]:
            {
                if (AppliedBodies.LastOrDefault() is not { } active) return "err no-active-tone";
                File.WriteAllText(line["tone dump ".Length..], active);
                using var dumped = JsonDocument.Parse(active);
                return $"ok dumped {dumped.RootElement.GetProperty("GearList").EnumerateObject().Count()} live gears";
            }
            case ["tone", "restore"]:
                return AppliedBodies.IsEmpty ? "err nothing-to-restore" : "ok restored";
            case ["perf", "hidden_fps" or "visible_fps", _]:
            case ["mix", "mute", "on" or "off"]:
            case ["window", "hide" or "show"]:
            case ["quit"]:
                return "ok";
            default:
                return "err unknown-command " + words[0];
        }
    }
}

/// <summary>
///     The protocol's shared-memory block (v1): a 64-byte header (magic, version, rate, block,
///     ring size, channel counts, the four block indices, then gameUnderruns, hostUnderruns,
///     overruns, outOverruns), then the mono input ring and the stereo output ring, each
///     64-byte aligned. A worker plays the game:
///     for every input block the host wrote, one output block.
/// </summary>
internal sealed class TonelabSharedMemoryMock : IDisposable
{
    public const int HeaderBytes = 64;
    public const int InWriteOffset = 32;
    public const int InReadOffset = 36;
    public const int OutWriteOffset = 40;
    public const int OutReadOffset = 44;
    public const int GameUnderrunsOffset = 48;
    public const int HostUnderrunsOffset = 52;
    public const int OverrunsOffset = 56;
    public const int OutOverrunsOffset = 60;

    private readonly MemoryMappedFile _file;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _render;
    private readonly float[] _delayLine;
    private int _delayPos;

    public TonelabSharedMemoryMock(string name, int blockFrames, int ringBlocks, float gain, int delaySamples)
    {
        BlockFrames = blockFrames;
        RingBlocks = ringBlocks;
        InOffset = Align(HeaderBytes);
        OutOffset = Align(InOffset + (ringBlocks * blockFrames * 4));
        TotalBytes = OutOffset + (ringBlocks * blockFrames * 2 * 4);
        _delayLine = new float[delaySamples + 1];
        _file = MemoryMappedFile.CreateNew(name, TotalBytes);
        View = _file.CreateViewAccessor();
        View.WriteArray(0, "TONELAB\0"u8.ToArray(), 0, 8);
        View.Write(8, (uint)TonelabProtocol.Version);
        View.Write(12, (uint)TonelabProtocol.SampleRate);
        View.Write(16, (uint)blockFrames);
        View.Write(20, (uint)ringBlocks);
        View.Write(24, 1u);
        View.Write(28, 2u);
        // Its own thread: a pool thread spinning here for the whole test starves the pool under parallel tests.
        _render = Task.Factory.StartNew(() => Render(gain), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    public int BlockFrames { get; }

    public int RingBlocks { get; }

    public int InOffset { get; }

    public int OutOffset { get; }

    public int TotalBytes { get; }

    public MemoryMappedViewAccessor View { get; }

    public static int Align(int offset) => (offset + 63) & ~63;

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _render.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
        }

        View.Dispose();
        _file.Dispose();
    }

    private void Render(float gain)
    {
        var input = new float[BlockFrames];
        var output = new float[BlockFrames * 2];
        while (!_stop.IsCancellationRequested)
        {
            var inWrite = View.ReadUInt32(InWriteOffset);
            var inRead = View.ReadUInt32(InReadOffset);
            var outWrite = View.ReadUInt32(OutWriteOffset);
            var outRead = View.ReadUInt32(OutReadOffset);
            if (inRead == inWrite || outWrite - outRead >= RingBlocks)
            {
                Thread.Sleep(1);
                continue;
            }

            View.ReadArray(InOffset + ((int)(inRead % RingBlocks) * BlockFrames * 4), input, 0, BlockFrames);
            for (var i = 0; i < BlockFrames; i++)
            {
                _delayLine[_delayPos] = input[i];
                _delayPos = (_delayPos + 1) % _delayLine.Length;
                var delayed = _delayLine[_delayPos] * gain;
                output[2 * i] = delayed;
                output[(2 * i) + 1] = delayed;
            }

            View.WriteArray(OutOffset + ((int)(outWrite % RingBlocks) * BlockFrames * 2 * 4), output, 0, output.Length);
            View.Write(InReadOffset, inRead + 1);
            View.Write(OutWriteOffset, outWrite + 1);
        }
    }
}
