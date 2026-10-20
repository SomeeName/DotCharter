namespace DotCharter.Services.Audio.LivePlay.Takes;

/// <summary>Outcome of one finished take recording.</summary>
/// <param name="FilePath">Absolute path of the finalized WAV file.</param>
/// <param name="SamplesWritten">Mono samples actually persisted.</param>
/// <param name="DroppedSamples">Samples the bounded ring had to drop because
/// the background writer fell behind (0 in healthy runs).</param>
/// <param name="WasCapped">True when the duration cap cut the recording.</param>
/// <param name="WriteFailure">The exception that stopped the background
/// writer, when one did. Everything after it was dropped, so the take is
/// TRUNCATED at <paramref name="SamplesWritten"/> — a caller that does not
/// surface this ships a short take that looks healthy.</param>
public sealed record LiveTakeResult(string FilePath, long SamplesWritten, long DroppedSamples, bool WasCapped,
    Exception? WriteFailure = null)
{
    public double DurationSeconds(int sampleRate) => sampleRate <= 0 ? 0 : SamplesWritten / (double)sampleRate;
}

/// <summary>
///     Persists the dry (pre-VST) input capture of one Live Play / gameplay
///     run to a 16-bit mono WAV file — the "take". Designed as a TEE off the
///     existing 30 Hz capture pump, which is allocation-free:
///     <list type="bullet">
///     <item><see cref="Write"/> copies into a fixed single-producer /
///     single-consumer ring and NEVER blocks, locks or allocates. When the
///     ring is full the samples are dropped and counted
///     (<see cref="DroppedSamples"/>) — recording quality degrades, the pump
///     never stalls.</item>
///     <item>A background task drains the ring into the file via reusable
///     buffers, so file I/O latency can never reach the pump tick.</item>
///     <item>A duration cap stops acceptance (not the pump) once reached.</item>
///     </list>
///     Not an audio-thread component: the producer is the UI-thread capture
///     pump, which already tolerates ~33 ms of jitter.
/// </summary>
public sealed class LiveTakeRecorder : IDisposable
{
    private const int WavHeaderBytes = 44;
    private const int WriterIdleDelayMs = 40;
    private const int WriterChunkSamples = 16384;

    private readonly float[] _ring;
    private readonly int _ringCapacity;
    private readonly long _maxSamples;
    private readonly byte[] _pcmChunk = new byte[WriterChunkSamples * 2];
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _writerTask;
    private readonly Stream _stream;

    // Producer-owned write cursor / consumer-owned read cursor (monotonic,
    // wrapped modulo capacity on access). Volatile pairs make the SPSC ring
    // safe without locks.
    private long _writePos;
    private long _readPos;

    private long _acceptedSamples;
    private long _droppedSamples;
    private long _samplesWritten;
    private volatile bool _capped;
    private volatile bool _stopAccepting;
    private volatile Exception? _writeFailure;
    private int _disposed;

    /// <param name="filePath">Destination WAV path (created/truncated).</param>
    /// <param name="sampleRate">Capture sample rate (Hz).</param>
    /// <param name="maxDuration">Duration cap; acceptance stops when reached.</param>
    /// <param name="ringCapacitySamples">Ring size; defaults to ~4 s of audio.</param>
    /// <param name="autoStartWriter">Test seam: false leaves the ring undrained
    /// so producer-side drop accounting can be asserted deterministically.</param>
    public LiveTakeRecorder(string filePath, int sampleRate, TimeSpan maxDuration,
        int ringCapacitySamples = 0, bool autoStartWriter = true)
        : this(OpenTakeFile(filePath), filePath, sampleRate, maxDuration, ringCapacitySamples, autoStartWriter)
    {
    }

    /// <summary>Test seam: records into an arbitrary stream so a failing
    /// disk can be simulated. Production goes through the path overload.</summary>
    internal LiveTakeRecorder(Stream stream, string filePath, int sampleRate, TimeSpan maxDuration,
        int ringCapacitySamples = 0, bool autoStartWriter = true)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        FilePath = filePath;
        SampleRate = sampleRate;
        _ringCapacity = ringCapacitySamples > 0 ? ringCapacitySamples : sampleRate * 4;
        _ring = new float[_ringCapacity];
        _maxSamples = maxDuration > TimeSpan.Zero
            ? (long)(maxDuration.TotalSeconds * sampleRate)
            : long.MaxValue;

        _stream = stream;
        _stream.Write(stackalloc byte[WavHeaderBytes]); // placeholder, patched on stop

        _writerTask = autoStartWriter ? Task.Run(WriterLoopAsync) : Task.CompletedTask;
    }

    private static FileStream OpenTakeFile(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        return new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read,
            bufferSize: 1 << 16, useAsync: false);
    }

    public string FilePath { get; }
    public int SampleRate { get; }

    /// <summary>The exception that stopped the writer, if any (see
    /// <see cref="LiveTakeResult.WriteFailure"/>).</summary>
    public Exception? WriteFailure => _writeFailure;

    /// <summary>Samples dropped because the ring was full (starved writer).</summary>
    public long DroppedSamples => Interlocked.Read(ref _droppedSamples);

    /// <summary>True once the duration cap stopped acceptance.</summary>
    public bool WasCapped => _capped;

    /// <summary>
    ///     Tee entry point, called from the capture pump with each drained
    ///     block. Single producer only. Never blocks, never allocates; excess
    ///     samples beyond the ring's free space or the duration cap are
    ///     dropped (counted) rather than stalling the caller.
    /// </summary>
    public void Write(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty || _stopAccepting) return;

        // Duration cap: accept only what is left under the cap.
        var remainingUnderCap = _maxSamples - _acceptedSamples;
        if (remainingUnderCap <= 0)
        {
            _capped = true;
            _stopAccepting = true;
            return;
        }

        if (samples.Length > remainingUnderCap)
        {
            samples = samples[..(int)remainingUnderCap];
            _capped = true;
        }

        var writePos = _writePos; // producer-owned
        var readPos = Volatile.Read(ref _readPos);
        var free = _ringCapacity - (int)(writePos - readPos);
        var toCopy = Math.Min(free, samples.Length);
        if (toCopy < samples.Length)
            Interlocked.Add(ref _droppedSamples, samples.Length - toCopy);

        if (toCopy > 0)
        {
            var offset = (int)(writePos % _ringCapacity);
            var first = Math.Min(toCopy, _ringCapacity - offset);
            samples[..first].CopyTo(_ring.AsSpan(offset));
            if (toCopy > first)
                samples.Slice(first, toCopy - first).CopyTo(_ring);
            Volatile.Write(ref _writePos, writePos + toCopy);
        }

        _acceptedSamples += samples.Length; // dropped samples still count against the cap
        if (_capped) _stopAccepting = true;
    }

    /// <summary>
    ///     Stops acceptance, drains everything left in the ring to disk,
    ///     patches the WAV header and closes the file. Safe to call once;
    ///     returns the final take facts.
    /// </summary>
    public async Task<LiveTakeResult> StopAsync()
    {
        _stopAccepting = true;
        // Dispose already cancelled and disposed the token source, and calling
        // Cancel on a disposed CTS throws — so a Dispose-then-Stop ordering used
        // to surface an ObjectDisposedException out of StopAsync. Latent only
        // because LiveTakeStore.FinalizeTakeAsync happens to order them the
        // other way and wraps everything in a catch.
        if (Volatile.Read(ref _disposed) == 0)
        {
            try { _stop.Cancel(); }
            catch (ObjectDisposedException) { /* raced Dispose */ }
        }
        try
        {
            await _writerTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Writer observed the cancel between drains — expected.
        }

        while (DrainOnce() > 0) { } // final drain of whatever the loop left behind
        FinalizeWav();
        return new LiveTakeResult(FilePath, _samplesWritten, DroppedSamples, _capped, _writeFailure);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stopAccepting = true;
        _stop.Cancel();
        try
        {
            _stream.Dispose();
        }
        catch
        {
            // Best effort: Dispose without StopAsync means the take is abandoned.
        }

        _stop.Dispose();
    }

    private async Task WriterLoopAsync()
    {
        // Captured ONCE: Dispose() disposes the source, and a disposed
        // CancellationTokenSource throws from `.Token` (not from
        // IsCancellationRequested). Dispose-then-StopAsync would otherwise surface that
        // ObjectDisposedException out of StopAsync's await of this task — seen
        // in the full gate under load, never in isolation.
        CancellationToken token;
        try { token = _stop.Token; }
        catch (ObjectDisposedException) { return; }

        while (!token.IsCancellationRequested)
        {
            if (DrainOnce() == 0)
            {
                try
                {
                    await Task.Delay(WriterIdleDelayMs, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>Consumer side: converts one chunk of ring content to 16-bit
    /// PCM into the reusable byte buffer and writes it. Returns samples written.</summary>
    private int DrainOnce()
    {
        if (_disposed != 0) return 0;
        var readPos = _readPos; // consumer-owned
        var writePos = Volatile.Read(ref _writePos);
        var available = (int)(writePos - readPos);
        if (available <= 0) return 0;

        var count = Math.Min(available, WriterChunkSamples);
        for (var i = 0; i < count; i++)
        {
            var sample = _ring[(int)((readPos + i) % _ringCapacity)];
            var clamped = Math.Clamp(sample, -1f, 1f);
            var value = (short)Math.Round(clamped * short.MaxValue);
            _pcmChunk[i * 2] = (byte)value;
            _pcmChunk[i * 2 + 1] = (byte)((ushort)value >> 8);
        }

        try
        {
            _stream.Write(_pcmChunk, 0, count * 2);
        }
        catch (Exception ex)
        {
            // Disk gone / file locked: stop consuming, the take is TRUNCATED
            // here but the pump (producer) keeps running unharmed. This class
            // has no logger — the failure rides out on the result so the
            // store can say so; before it did, the take just came up short.
            _writeFailure ??= ex;
            _stopAccepting = true;
            return 0;
        }

        _samplesWritten += count;
        Volatile.Write(ref _readPos, readPos + count);
        return count;
    }

    private void FinalizeWav()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            var dataBytes = _samplesWritten * 2;
            Span<byte> header = stackalloc byte[WavHeaderBytes];
            WriteWavHeader(header, SampleRate, dataBytes);
            _stream.Position = 0;
            _stream.Write(header);
            _stream.Flush();
        }
        catch (Exception ex)
        {
            _writeFailure ??= ex;
        }
        finally
        {
            _stream.Dispose();
            _stop.Dispose();
        }
    }

    /// <summary>Canonical 44-byte PCM WAV header (16-bit mono).</summary>
    internal static void WriteWavHeader(Span<byte> header, int sampleRate, long dataBytes)
    {
        const short channels = 1;
        const short bitsPerSample = 16;
        var byteRate = sampleRate * channels * (bitsPerSample / 8);
        var blockAlign = (short)(channels * (bitsPerSample / 8));

        "RIFF"u8.CopyTo(header);
        WriteInt32(header[4..], (int)Math.Min(int.MaxValue, 36 + dataBytes));
        "WAVE"u8.CopyTo(header[8..]);
        "fmt "u8.CopyTo(header[12..]);
        WriteInt32(header[16..], 16);            // fmt chunk size
        WriteInt16(header[20..], 1);             // PCM
        WriteInt16(header[22..], channels);
        WriteInt32(header[24..], sampleRate);
        WriteInt32(header[28..], byteRate);
        WriteInt16(header[32..], blockAlign);
        WriteInt16(header[34..], bitsPerSample);
        "data"u8.CopyTo(header[36..]);
        WriteInt32(header[40..], (int)Math.Min(int.MaxValue, dataBytes));
    }

    private static void WriteInt32(Span<byte> target, int value)
    {
        target[0] = (byte)value;
        target[1] = (byte)(value >> 8);
        target[2] = (byte)(value >> 16);
        target[3] = (byte)(value >> 24);
    }

    private static void WriteInt16(Span<byte> target, short value)
    {
        target[0] = (byte)value;
        target[1] = (byte)((ushort)value >> 8);
    }
}
