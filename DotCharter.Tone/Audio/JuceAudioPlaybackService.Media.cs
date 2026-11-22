// Partial: media loading — LoadAudio*, the deliberately unlocked SwapMasterSource open, waveform generation and FreeAudio.
using JuceSharp;
using DotCharter.Tone.Audio.Engine;
using Ownership = DotCharter.Tone.Audio.Engine.MediaSourceOwnership;

namespace DotCharter.Tone.Audio;

public sealed partial class JuceAudioPlaybackService
{
    /// <summary>The open master's display path, empty after native-source teardown.</summary>
    public string CurrentAudioPath { get { lock (_gate) return _loadedPath ?? ""; } }

    private readonly object _mediaOperationGate = new();
    private long _mediaLoadGeneration;
    // Explicit loads and frees, not takes: a rebuild's stems belong to the song under a take.
    private long _mediaLineage;
    private MediaLoadRequest _requestedMediaLoad = new(null, 0);
    private MediaLoadRequest _loadedMediaLoad = new(null, 0);
    private Ownership.State _mediaOwnership = new(new(null, 0), new(null, 0));

    private Ownership.Action DecideMedia(Ownership.Event @event, MediaLoadRequest request, MediaLoadRequest? previous = null)
    {
        var decision = Ownership.Decide(_mediaOwnership, @event, request, previous, _loadedPath);
        Volatile.Write(ref _requestedMediaLoad, decision.Next.Requested);
        _loadedMediaLoad = decision.Next.Loaded;
        _mediaOwnership = decision.Next;
        _mediaRebuildRecovery.Observe(decision.Next.Requested);
        return decision.Action;
    }

    private MediaLoadRequest RequestMediaLoad(string? identity) => RequestMediaLoad(identity, false, out _);

    internal MediaLoadRequest RequestMediaLoad(string? identity, bool temporary, out MediaLoadRequest previous)
    {
        if (identity is not null && (Path.IsPathFullyQualified(identity) || File.Exists(identity))) identity = Path.GetFullPath(identity);
        lock (_mediaOperationGate)
            lock (_gate)
            {
                previous = _requestedMediaLoad;
                var request = new MediaLoadRequest(identity, ++_mediaLoadGeneration);
                if (!temporary) _mediaLineage++;
                DecideMedia(temporary ? Ownership.Event.TakeQueued : Ownership.Event.ExplicitLoadQueued, request);
                return request;
            }
    }

    public long CurrentMediaGeneration { get { lock (_gate) return _loadedMediaLoad.Generation; } }

    public bool OwnsMedia(string path, long generation)
    {
        lock (_gate) return OwnsMediaLocked(path, generation);
    }

    private bool OwnsMediaLocked(string path, long generation) => !IsDisposing && Ownership.Owns(_mediaOwnership, path, generation, _loadedPath);

    // Publication and teardown take this outer lock too; native waits never retain _gate.
    public bool RunIfOwned(string path, long generation, Action act)
    {
        lock (_mediaOperationGate)
        {
            lock (_gate)
            {
                if (!OwnsMediaLocked(path, generation)) return false;
            }
            act();
            return true;
        }
    }

    public Task LoadTemporaryAudioAsync(string filePath, CancellationToken cancellationToken, out long generation)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var request = RequestMediaLoad(filePath, true, out var previous);
        generation = request.Generation;
        return QueueAudioLoad(filePath, request, cancellationToken, previous);
    }

    public Task<bool> TryRestoreAudioAsync(string? path, string ownedPath, long generation, double position)
    {
        var restore = path is not null && File.Exists(path) ? path : null;
        Task pending;
        MediaLoadRequest request;
        lock (_mediaOperationGate)
        {
            Ownership.Action action;
            lock (_gate)
            {
                request = new(restore, ++_mediaLoadGeneration);
                action = IsDisposing ? Ownership.Action.None : DecideMedia(Ownership.Event.StopHandBack, request, new(ownedPath, generation));
                if (action == Ownership.Action.None) return Task.FromResult(false);
            }
            Stop();
            if (action == Ownership.Action.Release)
            {
                JuceAudioSource? previous;
                lock (_gate) previous = DetachMasterLocked();
                try { previous?.Dispose(); } catch { /* ignored */ }
                return Task.FromResult(true);
            }
            pending = QueueAudioLoad(restore!, request, CancellationToken.None, null);
        }
        return FinishRestoreAsync(pending, request, position);
    }

    private async Task<bool> FinishRestoreAsync(Task pending, MediaLoadRequest request, double position)
    {
        await pending.ConfigureAwait(false);
        return RunIfOwned(request.Identity!, request.Generation, () => SeekTo(position));
    }

    public Task LoadAudioAsync(string filePath) => LoadAudioCoreAsync(filePath, CancellationToken.None);

    Task IPlaybackTransport.LoadAudioAsync(string filePath, CancellationToken cancellationToken)
        => LoadAudioCoreAsync(filePath, cancellationToken);

    private Task LoadAudioCoreAsync(string filePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return Task.CompletedTask;

        var temporary = cancellationToken.CanBeCanceled;
        var request = RequestMediaLoad(filePath, temporary, out var previousRequest);
        return QueueAudioLoad(filePath, request, cancellationToken, temporary ? previousRequest : null);
    }

    private Task QueueAudioLoad(string filePath, MediaLoadRequest request, CancellationToken cancellationToken, MediaLoadRequest? previousRequest)
        => MediaSourceSwap.Queue(() =>
        {
            EnsureEngine(new JuceEngineConfig());
            return SwapMasterSource(filePath, request, engine => engine.OpenFile(filePath), cancellationToken, previousRequest);
        }, cancellationToken, _gate, DecideMedia, request, previousRequest, _mediaOperationGate);

    public Task LoadAudioFromMemoryAsync(ReadOnlyMemory<byte> bytes, string formatHint, string? displayPath = null)
    {
        if (bytes.IsEmpty) return Task.CompletedTask;
        var label = displayPath ?? $"<memory:{formatHint}:{bytes.Length} bytes>";
        var request = RequestMediaLoad(displayPath ?? $"{label}:{Guid.NewGuid():N}");
        // OpenMemory copies synchronously; callers retain bytes until completion (SongMemoryLoadFootprintTests).
        return Task.Run(() =>
        {
            EnsureEngine(new JuceEngineConfig());
            SwapMasterSource(label, request, engine => engine.OpenMemory(bytes.Span, formatHint));
        });
    }

    internal bool SwapMasterSource(string label, MediaLoadRequest request, Func<JuceAudioEngine, JuceAudioSource> opener,
        CancellationToken cancellationToken = default, MediaLoadRequest? previousRequest = null)
        => MediaSourceSwap.Run(new MediaSourceSwap.Context
        {
            MediaGate = _mediaLoadGate, OperationGate = _mediaOperationGate, Gate = _gate, Engine = () => _engine,
            Decide = DecideMedia, Source = () => _source,
            MayLoad = () => !IsDisposing, Open = opener,
            Detach = () =>
            {
                _source = null;
                _loadedPath = null;
                _lastKnownLengthMs = 0;
                _playhead.Reset();
                _lastKnownPosition.Reset(0, _playhead.Epoch);
            },
            Failed = ex =>
            {
                _logger?.Error(AppLogCategory.Audio, $"JUCE failed to open '{label}'.", ex);
                DecideMedia(Ownership.Event.DecodeFail, request, previousRequest);
            },
            Publish = (engine, opened, preservePrevious) =>
            {
                if (preservePrevious)
                {
                    _lastKnownLengthMs = 0;
                    _playhead.Reset();
                    _lastKnownPosition.Reset(0, _playhead.Epoch);
                }
                _source = opened;
                _loadedPath = label;
                BindSongClock(engine, opened);
                ApplyGain();
                try { opened.Pan = (float)Math.Clamp(_pan, -1.0, 1.0); } catch { /* ignored */ }
                try { opened.PitchSemitones = (float)Math.Clamp(_pitchShift, -24.0, 24.0); } catch { /* ignored */ }
                try { opened.TempoRatio = (float)(_playbackSpeed <= 0 ? 1.0 : _playbackSpeed); } catch { /* ignored */ }
            },
            Activate = (opened, preserve) =>
            {
                if (MediaSourceSwap.Resume(opened, IsPlaying, preserve, SnapshotStems().Select(s => s.Source))) _playhead.Reset();
            }
        }, request, previousRequest, cancellationToken);

    public double GetTotalAudioLengthMs() => MediaSourceSwap.Length(_source, ref _lastKnownLengthMs);

    public Task<WaveformSamples> GenerateWaveformAsync(string filePath) => AudioWaveformGeneration.GenerateAsync(filePath, _logger);

    public void FreeMasterAudio() => FreeMasterAudioFor(RequestMediaLoad(null));

    /// <summary>Releases only while <paramref name="request" /> is still the newest.</summary>
    internal void FreeMasterAudioFor(MediaLoadRequest request)
    {
        JuceAudioSource? previous;
        lock (_mediaOperationGate)
            lock (_gate)
            {
                if (!ReferenceEquals(_requestedMediaLoad, request)) return;
                previous = DetachMasterLocked();
                DecideMedia(Ownership.Event.EngineTeardown, _requestedMediaLoad);
                if (_stems.Count == 0) IsPlaying = false;
            }
        try { previous?.Dispose(); } catch { /* Teardown must not prevent the next master load. */ }
    }

    private JuceAudioSource? DetachMasterLocked()
    {
        var previous = _source;
        _source = null;
        _loadedPath = null;
        _lastKnownLengthMs = 0.0;
        _playhead.Reset();
        _lastKnownPosition.Reset(0.0, _playhead.Epoch);
        return previous;
    }

    public void FreeAudio() => FreeAudioFor(RequestMediaLoad(null));

    /// <summary>Releases only while <paramref name="request" /> is still the newest.</summary>
    internal void FreeAudioFor(MediaLoadRequest request)
    {
        lock (_mediaOperationGate)
            lock (_gate)
            {
                if (!ReferenceEquals(_requestedMediaLoad, request)) return;
                FreeAudioLocked();
            }
    }

    private void FreeAudioLocked()
    {
        try { DetachMasterLocked()?.Dispose(); } catch { /* ignored */ }
        DecideMedia(Ownership.Event.EngineTeardown, _requestedMediaLoad);
        IsPlaying = false;
        ClearStemsLocked();
    }
}
