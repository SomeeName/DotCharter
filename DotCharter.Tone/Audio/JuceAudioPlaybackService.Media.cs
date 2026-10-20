// Partial: media loading — LoadAudio*, the deliberately unlocked SwapMasterSource open, waveform generation and FreeAudio.
using JuceSharp;

namespace DotCharter.Tone.Audio;

public sealed partial class JuceAudioPlaybackService
{
    /// <summary>What the master transport currently has open. Set by both load
    /// paths (the memory path stores its caller-supplied display path, which is
    /// the real cached WAV), cleared by <see cref="FreeAudio" />.</summary>
    public string CurrentAudioPath
    {
        get { lock (_gate) { return _loadedPath ?? ""; } }
    }

    public Task LoadAudioAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return Task.CompletedTask;

        // Opening the file synchronously can spend 1�3 seconds inside JUCE's
        // format readers (especially on long FLAC/MP3 inputs), so push it
        // to the thread pool to keep the UI responsive.
        return Task.Run(() =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            EnsureEngine(new JuceEngineConfig());
            var engineMs = sw.ElapsedMilliseconds;
            SwapMasterSource(filePath, engine => engine.OpenFile(filePath));
            _logger?.Debug(AppLogCategory.Audio,
                $"LoadAudio phases: ensureEngine={engineMs} ms, swap+open={sw.ElapsedMilliseconds - engineMs} ms ('{Path.GetFileName(filePath)}').");
        });
    }

    public Task LoadAudioFromMemoryAsync(ReadOnlyMemory<byte> bytes, string formatHint, string? displayPath = null)
    {
        if (bytes.IsEmpty) return Task.CompletedTask;
        // NO managed copy: `bytes.ToArray()` "because
        // the caller's array may live on the GC heap" would be a second
        // whole-song array (~50 MB for a five-minute 16-bit WAV) on the large
        // object heap for every song opened, and buy nothing. The native
        // open copies the blob into its own memory before it returns (and frees
        // it once the song is decoded into RAM), OpenMemory pins the span only
        // for that call, and the contract below says the caller does not touch
        // the bytes until this task completes. Pinned by
        // Services/Audio/SongMemoryLoadFootprintTests.
        var label = displayPath ?? $"<memory:{formatHint}:{bytes.Length} bytes>";
        return Task.Run(() =>
        {
            EnsureEngine(new JuceEngineConfig());
            SwapMasterSource(label, engine => engine.OpenMemory(bytes.Span, formatHint));
        });
    }

    /// <summary>
    /// Shared body for LoadAudioAsync + LoadAudioFromMemoryAsync. Replaces the
    /// master source, preserves stems, and resumes playback on the new source
    /// when the user was already playing.
    /// <para>
    /// <b>The native OPEN happens OUTSIDE <c>_gate</c>.</b> A JUCE format reader
    /// spends 1–3 seconds on a long FLAC/MP3, and six UI-thread members lock the
    /// same gate (<c>CurrentAudioPath</c>, <c>HasStems</c>, <c>LoadedStemNames</c>,
    /// the volume setters, and <c>SnapshotStems()</c> — which every one of
    /// Play/Pause/Stop/SeekTo calls first). Holding it across the decode meant
    /// pressing Space during a song load froze the UI for the whole decode; the
    /// method's own "waited N ms for the playback gate" log was the symptom. This
    /// is the same phase split <c>ApplyGuitarVstChainCore</c> already uses:
    /// decide + detach under the lock, do the native work unlocked, publish under
    /// the lock.
    /// </para>
    /// <para>
    /// The unlocked window is serialized by <c>_mediaLoadGate</c> (so two loads
    /// cannot interleave their publishes) and the publish step re-checks that the
    /// engine we opened against is still the live one — a device change can run a
    /// whole ReplaceEngine while we are inside the reader, and a source belonging
    /// to a destroyed engine must be dropped, not published.
    /// </para>
    /// </summary>
    private void SwapMasterSource(string label, Func<JuceAudioEngine, JuceAudioSource> opener)
    {
        lock (_mediaLoadGate)
        {
            JuceAudioEngine? engine;
            JuceAudioSource? previous;
            lock (_gate)
            {
                engine = _engine;
                // IMPORTANT: replace ONLY the master source. Do NOT call
                // FreeAudioLocked() here — it also clears the stem dictionary,
                // which races against StemMixerViewModel.Rebuild() (which fires
                // on the project-path-changed property event and loads stems
                // BEFORE LoadAudioFile runs).
                previous = _source;
                _source = null;
                _loadedPath = null;
                _lastKnownLengthMs = 0.0;
                _playhead.Reset();
                _lastKnownPosition.Reset(0.0, _playhead.Epoch);
            }
            if (engine == null)
            {
                _logger?.Warning(AppLogCategory.Audio, $"No audio engine to open '{label}' with.");
                return;
            }

            // Unlocked: the old source is already out of the field, so nothing
            // can reach it, and its teardown can block on the transport.
            try { previous?.Dispose(); } catch { /* ignored */ }

            JuceAudioSource opened;
            try
            {
                opened = opener(engine);
            }
            catch (Exception ex)
            {
                _logger?.Error(AppLogCategory.Audio, $"JUCE failed to open '{label}'.", ex);
                return;
            }

            lock (_gate)
            {
                if (!ReferenceEquals(_engine, engine))
                {
                    // The engine was replaced while we were decoding; this source
                    // belongs to a dead engine. The replace path re-opens the song
                    // itself, so dropping it here loses nothing.
                    _logger?.Warning(AppLogCategory.Audio,
                        $"Engine was replaced while opening '{label}'; discarding the stale source.");
                    try { opened.Dispose(); } catch { /* ignored */ }
                    return;
                }

                _source = opened;
                _loadedPath = label;
                BindSongClock(engine, opened);
                ApplyGain();
                try { opened.Pan = (float)Math.Clamp(_pan, -1.0, 1.0); } catch { /* ignored */ }
                try { opened.PitchSemitones = (float)Math.Clamp(_pitchShift, -24.0, 24.0); } catch { /* ignored */ }
                try { opened.TempoRatio = (float)(_playbackSpeed <= 0 ? 1.0 : _playbackSpeed); } catch { /* ignored */ }
                if (IsPlaying)
                {
                    try { opened.Seek(TimeSpan.Zero); } catch { /* ignored */ }
                    try { opened.Play(); } catch { /* ignored */ }
                    foreach (var s in _stems.Values)
                    {
                        try { s.Source?.Seek(TimeSpan.Zero); } catch { /* ignored */ }
                        try { s.Source?.Play(); } catch { /* ignored */ }
                    }
                    _playhead.Reset();
                }
            }
        }
    }

    public double GetTotalAudioLengthMs()
    {
        var src = _source;
        if (src == null) return _lastKnownLengthMs;
        try
        {
            var len = src.Length.TotalMilliseconds;
            if (len > 0) _lastKnownLengthMs = len;
            return len;
        }
        catch
        {
            return _lastKnownLengthMs;
        }
    }

    public Task<WaveformSamples> GenerateWaveformAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return Task.FromResult(WaveformSamples.Empty);

        return Task.Run(() =>
        {
            try
            {
                var peaks = JuceAudioEngine.GenerateWaveform(filePath, WaveformBaseBlockMs);
                return WaveformSamples.FromBasePeaks(peaks, WaveformBaseBlockMs);
            }
            catch (Exception ex)
            {
                _logger?.Error(AppLogCategory.Audio, $"Waveform generation failed for '{filePath}'.", ex);
                return WaveformSamples.Empty;
            }
        });
    }

    /// <summary>Base resolution of the waveform pyramid. Lives on
    /// <see cref="WaveformSamples.GenerationBlockMs" /> so the disk cache can
    /// REFUSE an entry generated at a different width — see that constant for
    /// why it is 2 ms and what changing it costs.</summary>
    private const double WaveformBaseBlockMs = WaveformSamples.GenerationBlockMs;

    public void FreeAudio()
    {
        lock (_gate) FreeAudioLocked();
    }

    private void FreeAudioLocked()
    {
        try { _source?.Dispose(); } catch { /* ignored */ }
        _source = null;
        _loadedPath = null;
        IsPlaying = false;
        _lastKnownLengthMs = 0.0;
        _playhead.Reset();
        _lastKnownPosition.Reset(0.0, _playhead.Epoch);
        ClearStemsLocked();
    }
}
