using JuceSharp.AudioIo;

namespace DotCharter.Tone.Audio.Reamp;

/// <summary>
///     Records one seat's DRY input — the capture ring the tuner reads, i.e.
///     the guitar before the rig (after the drop tune) — to a mono float WAV at
///     the engine rate.
///     <para>
///         It follows the stream with the CURSORLESS positional read
///         (<see cref="IReampEngine.ReadCaptureFrom" />), holding its own cursor,
///         so it never steals a sample from live note detection, which drains
///         the ring's one streaming cursor. Samples go straight to disk as they
///         arrive (a temp file beside the target, renamed on <see cref="Stop" />),
///         so a long take costs no memory. Pure over the seam: the tests drive it
///         with a synthetic stream.
///     </para>
///     <para>
///         <b>Gaps are written as silence, never skipped.</b> A poll that falls a
///         whole ring (~2.7 s) behind loses samples; they are replaced by zeros so
///         the take keeps its timing, and <see cref="GapFrames" /> counts them. A
///         stream that jumps BACKWARDS (an engine rebuild restarted it) is
///         re-anchored and counted in <see cref="Restarts" />.
///     </para>
///     <para>
///         Not thread-safe: one owner calls <see cref="Start" />,
///         <see cref="Poll" /> and <see cref="Stop" />.
///     </para>
/// </summary>
public sealed class DiTakeRecorder : IDisposable
{
    /// <summary>Longest take a recording keeps (then it stops growing).</summary>
    public const double MaxSeconds = 20 * 60;

    private const int ReadChunk = 8192;
    private readonly IReampEngine _engine;
    private readonly float[] _buf = new float[ReadChunk];
    private readonly float[] _zeros = new float[ReadChunk];
    private WavWriter? _writer;
    private string? _tempPath;
    private string? _targetPath;
    private ulong _cursor;
    private long _maxFrames;

    public DiTakeRecorder(IReampEngine engine, int player = 0)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        Player = player;
    }

    /// <summary>The seat being recorded.</summary>
    public int Player { get; }

    /// <summary>True between <see cref="Start" /> and <see cref="Stop" />/<see cref="Cancel" />.</summary>
    public bool IsRecording => _writer != null;

    /// <summary>The take's rate (the engine's rate when recording started).</summary>
    public int SampleRate { get; private set; }

    /// <summary>Frames written so far, gaps included.</summary>
    public long Frames { get; private set; }

    /// <summary>Frames of silence written in place of samples the ring had already overwritten.</summary>
    public long GapFrames { get; private set; }

    /// <summary>How many times the stream restarted under the recording (an engine rebuild).</summary>
    public int Restarts { get; private set; }

    /// <summary>Peak |sample| of the last <see cref="Poll" />, for a level meter.</summary>
    public float LastPeak { get; private set; }

    /// <summary>Seconds recorded so far.</summary>
    public double ElapsedSeconds => SampleRate > 0 ? (double)Frames / SampleRate : 0;

    /// <summary>
    ///     Starts recording to <paramref name="targetPath" /> from the NEXT
    ///     captured sample. False — and nothing created — when there is no engine
    ///     or no input stream to follow.
    /// </summary>
    public bool Start(string targetPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        if (IsRecording) throw new InvalidOperationException("Already recording.");
        var rate = _engine.ReampSampleRate;
        if (rate <= 0 || !_engine.TryGetCaptureTotal(Player, out var total)) return false;

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(targetPath))!);
        _targetPath = targetPath;
        _tempPath = targetPath + ".recording";
        _writer = new WavWriter(_tempPath, rate, 1);
        SampleRate = rate;
        _cursor = total;
        _maxFrames = (long)(MaxSeconds * rate);
        Frames = 0;
        GapFrames = 0;
        Restarts = 0;
        LastPeak = 0;
        return true;
    }

    /// <summary>Drains everything captured since the last poll into the take.
    /// Returns the frames appended. Poll well inside the ring (≤ 1 s).</summary>
    public int Poll()
    {
        if (_writer == null) return 0;
        var appended = 0;
        var peak = 0f;
        while (Frames < _maxFrames)
        {
            var n = _engine.ReadCaptureFrom(Player, _cursor, _buf, out var first);
            if (first < _cursor)
            {
                // The stream went BACKWARDS: a rebuilt engine started a new ring.
                // Follow it from where it is; the take just continues.
                Restarts++;
                _cursor = first;
                if (n <= 0) break;
            }
            else if (first > _cursor)
            {
                // Fell a whole ring behind: keep the timing, write the hole as silence.
                var gap = (long)(first - _cursor);
                GapFrames += gap;
                appended += WriteSilence(gap);
                _cursor = first;
            }
            if (n <= 0) break;
            var keep = (int)Math.Min(n, _maxFrames - Frames);
            var span = new ReadOnlySpan<float>(_buf, 0, keep);
            _writer.Write(span);
            foreach (var s in span) { var a = MathF.Abs(s); if (a > peak) peak = a; }
            Frames += keep;
            appended += keep;
            _cursor += (ulong)n;
            if (n < _buf.Length) break;
        }
        LastPeak = peak;
        return appended;
    }

    private int WriteSilence(long frames)
    {
        var written = 0;
        while (frames > 0 && Frames < _maxFrames)
        {
            var k = (int)Math.Min(Math.Min(frames, _zeros.Length), _maxFrames - Frames);
            _writer!.Write(new ReadOnlySpan<float>(_zeros, 0, k));
            Frames += k;
            frames -= k;
            written += k;
        }
        return written;
    }

    /// <summary>Drains the last samples, finalizes the WAV and moves it into
    /// place. Returns the file, or null when nothing was recorded (the empty
    /// file is deleted).</summary>
    public string? Stop()
    {
        if (_writer == null) return null;
        Poll();
        _writer.Dispose();
        _writer = null;
        var temp = _tempPath!;
        var target = _targetPath!;
        _tempPath = _targetPath = null;
        if (Frames == 0)
        {
            BestEffortDelete.File(temp);
            return null;
        }
        File.Move(temp, target, overwrite: false);
        return target;
    }

    /// <summary>Abandons the recording and deletes what was written.</summary>
    public void Cancel()
    {
        if (_writer == null) return;
        _writer.Dispose();
        _writer = null;
        BestEffortDelete.File(_tempPath);
        _tempPath = _targetPath = null;
    }

    public void Dispose() => Cancel();
}
