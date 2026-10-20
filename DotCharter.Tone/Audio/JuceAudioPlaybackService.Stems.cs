// Partial: stem mixer — see JuceAudioPlaybackService.cs for the shared state and locking rules.
using JuceSharp;

namespace DotCharter.Tone.Audio;

public sealed partial class JuceAudioPlaybackService
{
    private sealed class StemEntry
    {
        // ReSharper disable UnusedAutoPropertyAccessor.Local -- used in object initializers + diagnostics.
        public required string Name { get; init; }
        public required string Path { get; init; }
        // ReSharper restore UnusedAutoPropertyAccessor.Local
        public JuceAudioSource? Source;
        public double UserVolume = 1.0;
        public bool UserMuted;
        public bool UserSoloed;
    }

    /// <summary>
    /// The mixer state an engine rebuild has to carry across: which stems were
    /// loaded, from where, and the user's per-stem gain/mute/solo plus whether
    /// they had un-muted the original mix.
    /// </summary>
    internal sealed record StemMixerSnapshot(
        IReadOnlyList<(string Name, string Path, double Volume, bool Muted, bool Soloed)> Stems,
        bool MasterMixMuted);

    /// <summary>Call under <c>_gate</c>. Null when no stems are loaded.</summary>
    internal StemMixerSnapshot? SnapshotStemMixerLocked()
    {
        if (_stems.Count == 0) return null;
        var entries = new List<(string, string, double, bool, bool)>(_stems.Count);
        foreach (var s in _stems.Values)
            entries.Add((s.Name, s.Path, s.UserVolume, s.UserMuted, s.UserSoloed));
        return new StemMixerSnapshot(entries, _masterMixMuted);
    }

    /// <summary>
    /// Re-issues a snapshot taken before an engine teardown.
    /// <para>
    /// `ReplaceEngineGated` rehydrates the song, the instrument VSTi, the guitar
    /// chain and the mirror device — the stem set was the one thing it dropped,
    /// while the device watchdog's own class comment claimed stems were among
    /// the things "restored by the existing ReplaceEngine rehydration logic".
    /// So after any device change, buffer-size change or watchdog recovery a
    /// user mixing stems silently lost them and was handed the original mix back
    /// (the master un-mutes on clear, which is correct on its own but is exactly
    /// what made the loss quiet rather than obvious).
    /// </para>
    /// <para>
    /// Only runs with a live engine: a FAILED rebuild leaves <c>_engine</c> null,
    /// and <c>LoadStemsCore</c> would then call <c>EnsureEngine</c> → a nested
    /// <c>ReplaceEngineCore</c>, which would block on the non-reentrant
    /// <c>_vstGate</c> our caller is still holding.
    /// </para>
    /// </summary>
    internal void RestoreStemsAfterEngineRebuild(StemMixerSnapshot? snapshot)
    {
        if (snapshot == null || snapshot.Stems.Count == 0) return;
        if (_engine == null)
        {
            _logger?.Warning(AppLogCategory.Audio,
                $"Engine rebuild failed; {snapshot.Stems.Count} stem(s) could not be restored.");
            return;
        }

        var requests = new List<StemLoadRequest>(snapshot.Stems.Count);
        foreach (var (name, path, _, _, _) in snapshot.Stems)
            requests.Add(new StemLoadRequest(name, path));

        LoadStemsCore(requests);

        lock (_gate)
        {
            foreach (var (name, _, volume, muted, soloed) in snapshot.Stems)
            {
                if (!_stems.TryGetValue(name, out var entry)) continue;
                entry.UserVolume = volume;
                entry.UserMuted = muted;
                entry.UserSoloed = soloed;
            }
            // LoadStemsCore re-derives this as "stems present ⇒ mute the master";
            // the user may have turned the original mix back on.
            _masterMixMuted = snapshot.MasterMixMuted;
            ApplyGain();
        }
        _logger?.Info(AppLogCategory.Audio,
            $"Restored {requests.Count} stem(s) after the engine rebuild.");
    }

    /// <summary>
    /// Un-groups and disposes a detached stem set. Safe to call UNLOCKED, and
    /// <see cref="LoadStemsCore" /> does: the entries are already out of
    /// <c>_stems</c>, so nothing managed can reach them, exactly as
    /// <c>SwapMasterSource</c> argues for the master source.
    /// </summary>
    private static void DisposeStemEntries(IEnumerable<StemEntry> entries)
    {
        foreach (var s in entries)
        {
            // Un-group BEFORE disposing so the engine's stem-group sub-mixer
            // releases its raw pointer to the source's panner. Disposing
            // while still grouped left a dangling AudioSource* in the
            // sub-mixer that the audio thread then dereferenced — instant
            // 0xC0000005 access violation.
            try { s.Source?.StemGrouped = false; } catch { /* ignored */ }
            try { s.Source?.Dispose(); } catch { /* ignored */ }
            s.Source = null;
        }
    }

    private void ClearStemsLocked()
    {
        DisposeStemEntries(_stems.Values);
        _stems.Clear();

        // The master mix is only muted BECAUSE stems are playing in its place (see
        // LoadStemsAsync: `_masterMixMuted = _stems.Count > 0`). With the stems gone
        // there is nothing left to mute it for, and leaving it set silences the
        // original track.
        //
        // This bit lives here rather than in the public ClearStems() because
        // FreeAudioLocked() — the ENGINE TEARDOWN path — calls straight into this
        // method. Changing audio device while stems were loaded therefore destroyed the
        // stems, kept the master muted, and brought the song back completely silent.
        _masterMixMuted = false;
    }

    // ----- Multi-track stem mixer ---------------------------------------
    public bool HasStems
    {
        get { lock (_gate) return _stems.Count > 0; }
    }

    public IReadOnlyList<string> LoadedStemNames
    {
        get { lock (_gate) return _stems.Keys.ToArray(); }
    }

    /// <summary>
    /// Loads the stem set. Genuinely asynchronous: the body below can create an
    /// engine (an ASIO/WASAPI device open, with a 250 ms sleep between retries)
    /// and takes _gate, which ReplaceEngineGated holds across a full engine
    /// teardown + rebuild. It has an await and a Task.Run: without them
    /// awaiting it from StemMixerViewModel.Rebuild — on the UI thread — would freeze
    /// the window for the length of an engine rebuild, and the freeze would be
    /// invisible because that rebuild runs as a DISCARDED task whose inner
    /// RebuildOnceAsync catches and degrades to "no stems". (It is neither an async void
    /// nor a silent swallow — the inner handler logs at Error.)
    /// </summary>
    public Task LoadStemsAsync(IReadOnlyList<StemLoadRequest> stems)
        => Task.Run(() => LoadStemsCore(stems));

    /// <summary>
    /// Opens a stem set and publishes it as the mixer's sources.
    /// <para>
    /// <b>The 4–6 native decodes happen OUTSIDE <c>_gate</c>.</b> They are the
    /// single longest thing this service could do under the lock — seconds of
    /// JUCE format reading, per stem — while <c>SnapshotStems()</c> (and hence
    /// every one of Play/Pause/Stop/SeekTo) plus <c>HasStems</c>,
    /// <c>LoadedStemNames</c> and the volume setters block on the same gate from
    /// the UI thread. Pressing Space while a song's stems loaded would freeze the window
    /// for the whole set. Phase split, as in <c>SwapMasterSource</c>: read the
    /// engine + mix state under the lock, open unlocked, publish under the lock.
    /// </para>
    /// <para>
    /// <b>The OUTGOING stem set is released BEFORE the decode, never after it.</b>
    /// Since JuceSharp <c>3db99e7</c> every source that fits the engine's preload budget
    /// decodes into RAM whole, so a stem costs its full float32 length — a 5-minute
    /// stereo stem is ~106 MB. Opening the incoming set while the outgoing one was still
    /// held therefore made a project switch peak at DOUBLE the resting audio memory
    /// (measured headless, 7 sources of 60.6 MB each: 965.7 MB peak against 492.7 MB when
    /// the old set goes first), and that peak is most of the working set the owner sees
    /// during a switch. Worse than the peak: <c>AudioEngine::reservePreloadBytes</c> is an
    /// engine-WIDE budget, so holding both sets can push the INCOMING stems over it — they
    /// then fall back to <c>BufferingAudioSource</c> and stay there for the life of the
    /// source, silently reinstating the audio-thread decode-lock stall the preload path
    /// exists to remove. Reproduced headless: with two 34-source sets one incoming source
    /// was forced to stream; disposing first, none were. <c>SwapMasterSource</c> already
    /// releases the previous MASTER source in this order — this is the same order for the
    /// stems. Pinned by <c>Services/Audio/StemSwapReleaseOrderTests</c>.
    /// </para>
    /// <para>
    /// <b>The opens go through <see cref="StemSetOpener" />, never a loop of
    /// <c>OpenFile</c> calls.</b> One after another, six 64 MB stems kept the
    /// mixer silent for 3.6 s after the project opened; the opener reads the files in
    /// order and overlaps the decodes. Pinned by
    /// <c>Services/Audio/StemSetOpenerTests</c>.
    /// </para>
    /// </summary>
    private void LoadStemsCore(IReadOnlyList<StemLoadRequest> stems)
    {
        if (stems.Count == 0) { ClearStems(); return; }

        var total = System.Diagnostics.Stopwatch.StartNew();
        EnsureEngine(new JuceEngineConfig());
        // Flip Rubber Band to the lighter R2 (Faster) engine BEFORE the
        // stem sources are opened so the freshly constructed stretchers
        // pick up the new quality on their first prepareToPlay. Five
        // concurrent R3 (Finer) stretchers (master + 4 stems) was
        // overwhelming the audio thread on typical user CPUs and produced
        // heavy artifacting whenever the user changed tempo or pitch.
        try { _engine?.SetStretchQuality(faster: true); } catch { /* ignored */ }

        var engineReadyAt = total.Elapsed;
        TimeSpan gateHeldAt, releasedAt, openedAt;
        StemSetOpener.Outcome<JuceAudioSource>[] outcomes;
        StemLoadRequest[] candidates;
        lock (_mediaLoadGate)
        {
            gateHeldAt = total.Elapsed;
            JuceAudioEngine? engine;
            float panV, pitchV, tempoV;
            StemEntry[] outgoing = [];
            lock (_gate)
            {
                engine = _engine;
                panV = (float)Math.Clamp(_pan, -1.0, 1.0);
                pitchV = (float)Math.Clamp(_pitchShift, -24.0, 24.0);
                tempoV = (float)(_playbackSpeed <= 0 ? 1.0 : _playbackSpeed);

                // Detach the OUTGOING stem set here — it is disposed below, BEFORE the
                // decode, not after it. See the phase-order remarks on the method.
                if (engine != null && _stems.Count > 0)
                {
                    outgoing = _stems.Values.ToArray();
                    _stems.Clear();
                    // Same reasoning as ClearStemsLocked's: the master is only muted
                    // BECAUSE stems play in its place, and there are none for the length
                    // of the decode. LoadStemsCore re-mutes it when the new set publishes.
                    _masterMixMuted = false;
                    ApplyGain();
                }
            }
            if (engine == null)
            {
                _logger?.Warning(AppLogCategory.Audio, "No audio engine to load stems into.");
                return;
            }

            // Unlocked, and BEFORE the opens below — see the method remarks.
            DisposeStemEntries(outgoing);
            releasedAt = total.Elapsed;

            candidates = stems.Where(IsOpenable).ToArray();

            // Unlocked: the expensive part. NOT a loop of OpenFile calls — the reads
            // run one after another and the native decodes overlap them; see
            // StemSetOpener for why it is neither sequential nor plain-parallel.
            outcomes = StemSetOpener.OpenAll(
                candidates.Select(r => r.Path).ToArray(),
                StemSetOpener.PrefetchIntoFileCache,
                path => engine.OpenFile(path),
                StemSetOpener.DefaultMaxOpensInFlight);
            openedAt = total.Elapsed;

            // Request order, whatever order the opens finished in: it is the mixer's
            // channel order.
            var opened = new List<StemEntry>(candidates.Length);
            for (var i = 0; i < candidates.Length; i++)
            {
                var req = candidates[i];
                if (outcomes[i].Source is { } src)
                    opened.Add(new StemEntry { Name = req.Name, Path = req.Path, Source = src });
                else
                    _logger?.Error(AppLogCategory.Audio, $"Failed to open stem '{req.Name}' from '{req.Path}'.", outcomes[i].Error);
            }

            lock (_gate)
            {
                if (!ReferenceEquals(_engine, engine))
                {
                    // A device change destroyed the engine we opened against.
                    // These sources belong to it; publishing them would hand the
                    // mixer pointers into freed native memory. The OUTGOING set is
                    // already gone (released above, before the decode) and could not
                    // have been kept anyway: it belonged to the destroyed engine.
                    // ReplaceEngineGated's RestoreStemsAfterEngineRebuild is what
                    // brings the user's stems back on the new engine.
                    _logger?.Warning(AppLogCategory.Audio,
                        "Engine was replaced while opening stems; discarding the stale stem sources.");
                    foreach (var e in opened)
                    {
                        try { e.Source?.Dispose(); } catch { /* ignored */ }
                    }
                    return;
                }

                // Normally a no-op — the outgoing set was released before the decode.
                // Kept because _stems is only guaranteed empty on the path THIS call
                // took: FreeAudioLocked / ClearStems can run against a different
                // engine while we are unlocked, and publishing over a non-empty
                // dictionary would drop those sources without un-grouping them.
                ClearStemsLocked();

                foreach (var entry in opened)
                {
                    // Route the stem into the engine's shared stem-group bus
                    // BEFORE setting per-stem tempo/pitch — once grouped, the
                    // per-source stretcher is bypassed and tempo/pitch flow
                    // through the shared group stretcher (set below). Both are
                    // cheap setters, unlike the decode above.
                    try { entry.Source!.StemGrouped = true; } catch { /* ignored */ }
                    try { entry.Source!.Pan = panV; } catch { /* ignored */ }
                    _stems[entry.Name] = entry;
                }

                // Apply the current tempo / pitch to the shared stem-group
                // stretcher so all freshly-loaded stems are in step with the
                // master playback right away (the stems just joined the bus;
                // the bus was lazily created on the first StemGrouped=true).
                if (_stems.Count > 0)
                {
                    try { _engine!.StemGroupTempo = tempoV; } catch { /* ignored */ }
                    try { _engine!.StemGroupPitchSemitones = pitchV; } catch { /* ignored */ }
                }

                // Auto-mute the original mix when stems are present, the stems are
                // now the user's mix. They can toggle the master back on from the UI.
                _masterMixMuted = _stems.Count > 0;
                ApplyGain();

                // Snap every newly-loaded stem to the current master position
                // so that when the user starts (or is already) playing, they
                // are in sample-accurate sync. Stems left at frame 0
                // until Play() is invoked would hard de-sync if
                // the transport is paused at e.g. 1:20 or playing mid-song
                // (the master keeps advancing during the per-stem Seek+Play
                // loop), and only Stop + Play would recover.
                var syncPos = _source?.Position ?? TimeSpan.Zero;
                foreach (var s in _stems.Values)
                {
                    try { s.Source?.Seek(syncPos); } catch { /* ignored */ }
                }

                // If transport is already playing, briefly pause the master so
                // all sources resume from the same frame, then resume them
                // together. Without this, the master keeps streaming during
                // the per-stem Play() loop and the last stem starts measurably
                // behind the first.
                if (IsPlaying && _source != null)
                {
                    try { _source.Pause(); } catch { /* ignored */ }
                    var pos = _source.Position;
                    try { _source.Seek(pos); } catch { /* ignored */ }
                    foreach (var s in _stems.Values)
                    {
                        try { s.Source?.Seek(pos); } catch { /* ignored */ }
                    }
                    try { _source.Play(); } catch { /* ignored */ }
                    foreach (var s in _stems.Values)
                    {
                        try { s.Source?.Play(); } catch { /* ignored */ }
                    }
                }
            }
        }
        _logger?.Info(AppLogCategory.Audio | AppLogCategory.Performance,
            DescribeStemLoad(candidates, outcomes, engineReadyAt, gateHeldAt, releasedAt, openedAt, total.Elapsed));
        _logger?.Info(AppLogCategory.Audio,
            $"Stem mixer loaded {stems.Count} stem(s): [{string.Join(", ", stems.Select(s => s.Name))}].");
    }

    private bool IsOpenable(StemLoadRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.Path)) return false;
        if (File.Exists(req.Path)) return true;
        _logger?.Warning(AppLogCategory.Audio, $"Stem missing on disk: '{req.Path}'.");
        return false;
    }

    /// <summary>
    ///     The per-step timing line: how long the engine and the media
    ///     gate kept the load waiting, then each stem's read and native open, then the
    ///     publish. A slow READ is a cold or slow disk; a slow OPEN is decode + resample.
    /// </summary>
    private static string DescribeStemLoad(StemLoadRequest[] stems, StemSetOpener.Outcome<JuceAudioSource>[] outcomes,
        TimeSpan engineReadyAt, TimeSpan gateHeldAt, TimeSpan releasedAt, TimeSpan openedAt, TimeSpan doneAt)
    {
        var perStem = stems.Select((s, i) =>
            $"{s.Name} read {outcomes[i].BytesRead / (1024.0 * 1024.0):0.#} MB in {outcomes[i].ReadTime.TotalMilliseconds:0} ms"
            + $" + open {outcomes[i].OpenTime.TotalMilliseconds:0} ms");
        return $"Stem load: engine ready at {engineReadyAt.TotalMilliseconds:0} ms, media gate at {gateHeldAt.TotalMilliseconds:0} ms, "
               + $"outgoing set released at {releasedAt.TotalMilliseconds:0} ms, "
               + $"{stems.Length} stem(s) read + opened in {(openedAt - releasedAt).TotalMilliseconds:0} ms "
               + $"({StemSetOpener.DefaultMaxOpensInFlight} open(s) in flight) [{string.Join("; ", perStem)}], "
               + $"published at {doneAt.TotalMilliseconds:0} ms.";
    }

    public void ClearStems()
    {
        lock (_gate)
        {
            if (_stems.Count == 0 && !_masterMixMuted) return;
            ClearStemsLocked(); // also un-mutes the master — see its remarks
            ApplyGain();
        }
        // Stems are gone � restore the higher-quality R3 (Finer) engine
        // so future single-source playback regains full fidelity.
        try { _engine?.SetStretchQuality(faster: false); } catch { /* ignored */ }
    }

    public void SetStemVolume(string name, double linearGain)
    {
        lock (_gate)
        {
            if (_stems.TryGetValue(name, out var s))
            {
                s.UserVolume = Math.Clamp(linearGain, 0.0, 4.0);
                ApplyStemGainsLocked();
            }
        }
    }

    public void SetStemMute(string name, bool muted)
    {
        lock (_gate)
        {
            if (_stems.TryGetValue(name, out var s))
            {
                s.UserMuted = muted;
                ApplyStemGainsLocked();
            }
        }
    }

    public void SetStemSolo(string name, bool soloed)
    {
        lock (_gate)
        {
            if (_stems.TryGetValue(name, out var s))
            {
                s.UserSoloed = soloed;
                // ApplyGain (not ApplyStemGainsLocked) so that the master mix
                // is also re-evaluated against the new solo state.
                ApplyGain();
            }
        }
    }

    public void SetMasterMixMuted(bool muted)
    {
        lock (_gate)
        {
            _masterMixMuted = muted;
            ApplyGain();
        }
    }
}
