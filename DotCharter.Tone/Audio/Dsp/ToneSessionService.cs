using DotCharter.Tone.Audio.Dsp.Presets;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Tone.Audio.Dsp;

/// <summary>
///     Gives the tone rig a life longer than one engine.
///     <para>
///         Before this existed the pedalboard started EMPTY on every launch. A
///         fresh install had no amp at all — plug in a guitar and you hear a raw
///         DI — and a rig the player spent ten minutes building was gone at exit
///         unless they remembered "Save as new". Worse, it was gone MID-SESSION
///         too: every engine rebuild (a device change, a buffer-size change, the
///         watchdog's recovery) creates a new native engine whose chain is
///         empty, so changing audio device silently wiped the board.
///     </para>
///     <para>
///         Two jobs, both driven by events the chain already raises:
///         <b>restore</b> on <see cref="IDspChainService.EngineAttached" /> (the
///         one moment a chain is legitimately empty and repopulating it cannot
///         fight the player), and <b>persist</b> on a low-frequency poll so a
///         knob moved five minutes ago is still there tomorrow.
///     </para>
///     <para>
///         <b>Why polling rather than a change event.</b>
///         <see cref="IDspChainService.NodesChanged" /> fires only for
///         COMPOSITION changes — add/remove — by design. A knob turn raises
///         nothing, and it must not: param writes happen at pointer rate during
///         a drag, and hanging a config write off them would put file I/O on the
///         UI thread sixty times a second. So the rig is snapshotted on a timer
///         and written only when a cheap signature says it actually changed,
///         which is zero I/O for an idle board.
///     </para>
///     <para>
///         Throw-free throughout: this runs inside the audio engine's attach
///         path, and a tone that fails to restore must never take the engine
///         down with it.
///     </para>
/// </summary>
public sealed class ToneSessionService : IDisposable
{
    /// <summary>How often the live chain is compared against what is stored. Long
    /// enough to be invisible (a handful of dictionary reads over ~6 nodes), short
    /// enough that a crash loses at most this much knob-twiddling.</summary>
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    /// <summary>How long <see cref="Dispose" /> waits for a persist that the
    /// timer had ALREADY started before it runs its own final flush. Only ever
    /// reached by a store write that is hung on the disk; a normal persist is
    /// microseconds.</summary>
    internal static readonly TimeSpan DisposeDrainTimeout = TimeSpan.FromSeconds(10);

    private readonly TimeSpan _pollInterval;

    private readonly IDspChainService _chain;
    private readonly IRigConfigService _config;
    private readonly IDspTonePresetService _presets;
    private readonly IProfileToneRigStore _rigStore;
    private readonly IAppLogger? _logger;
    private readonly object _gate = new();

    /// <summary>
    ///     Which SEAT this instance keeps (ABI v28). One service per seat:
    ///     each has its own chain, its own stored rig and its own restore, because
    ///     two players' boards are two independent things to remember.
    ///     <para>
    ///         Seat is one axis and PROFILE is the other: every seat's rig is
    ///         stored under the ACTIVE profile
    ///         (<see cref="IProfileToneRigStore" />), so a profile carries a whole
    ///         set of seat rigs and switching profile brings all of them.
    ///     </para>
    /// </summary>
    private int Player => _chain.Player;

    private System.Threading.Timer? _timer;
    private string _lastPersistedSignature = string.Empty;
    private bool _restoring;
    // Ticks on every entry to AND exit from a restore. PersistIfChanged captures
    // it before it snapshots the chain and re-checks it before it writes, so a
    // snapshot taken across a restore is discarded rather than saved as the
    // player's board. Guarded by _gate; see PersistIfChanged for why a plain
    // "_restoring right now?" re-check is not enough.
    private long _restoreGeneration;
    private bool _restorePending;

    /// <summary>Whether the DEFERRED restore must clear a seat the profile stores
    /// nothing for. Kept beside <see cref="_restorePending" /> rather than folded
    /// into it: they answer different questions (is there a pending restore, and
    /// what did it ask for). See <see cref="Restore" />.</summary>
    private bool _restorePendingClear;

    private bool _disposed;

    public ToneSessionService(
        IDspChainService chain,
        IRigConfigService config,
        IDspTonePresetService presets,
        IProfileToneRigStore rigStore,
        IAppLogger? logger = null)
        : this(chain, config, presets, rigStore, logger, PollInterval)
    {
    }

    /// <summary>Test seam: <paramref name="pollInterval" /> is the persist cadence.</summary>
    internal ToneSessionService(
        IDspChainService chain,
        IRigConfigService config,
        IDspTonePresetService presets,
        IProfileToneRigStore rigStore,
        IAppLogger? logger,
        TimeSpan pollInterval)
    {
        _chain = chain;
        _config = config;
        _presets = presets;
        _rigStore = rigStore ?? throw new ArgumentNullException(nameof(rigStore));
        _logger = logger;
        _pollInterval = pollInterval;
    }

    /// <summary>Starts listening. Safe to call once; a second call is a no-op.
    /// If an engine is ALREADY attached (the audio service came up before this
    /// service was constructed) the restore runs immediately, because
    /// <see cref="IDspChainService.EngineAttached" /> has already been and
    /// gone.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _timer is not null) return;
            _chain.EngineAttached += OnEngineAttached;
            _timer = new System.Threading.Timer(_ => PersistIfChanged(), null, _pollInterval, _pollInterval);
        }

        // BOTH halves are required (the subscription above AND the inline call
        // below); without either the app applies no tone rig at all.
        //
        // This inline call exists for exactly ONE case: the engine attached
        // before we subscribed, so the EngineAttached we would have restored on
        // has already been and gone. It must test HasEngine and not only "the
        // chain has no nodes" — which is ALSO what a chain with no engine looks
        // like, and on the shipped startup path that is the normal state: App
        // builds the DSP chains during DI and StartAll() runs from a background
        // task, while the JUCE engine is created lazily seconds later (measured:
        // ~5 s after the chains). A restore fired then would run the player's
        // whole saved rig at EngineHandle == 0, DspNativeInterop.AddNode would
        // return -1 before making a single native call, and the service would
        // log "restored 'Active rig' … FailedOps = 3" as though it had worked.
        //
        // Worse than the noise: OnEngineAttached is re-entrancy-guarded, and the
        // guard must not DISCARD a concurrent attach. The doomed restore and the
        // real engine attach are on different threads, so whenever they
        // overlap, a swallowed genuine EngineAttached is never retried — the
        // board would stay empty for the whole session. So a pending attach
        // re-runs instead of being dropped (see OnEngineAttached).
        if (_chain.HasEngine && _chain.GetNodes().Count == 0) OnEngineAttached();
    }

    /// <summary>
    ///     True while the live chain is NOT the player's board: a song's tone
    ///     drives it (the chart synth, the editor's tone preview, a Play Mode run) and
    ///     the player's board waits in that driver's snapshot. Nothing is persisted
    ///     then — not by the poll and not by <see cref="Flush" /> — so an exit or a
    ///     crash mid-song leaves the player's last saved board on disk, never the
    ///     song's tone. Set by the composition root; null (RigLab, tests) is never.
    /// </summary>
    public Func<bool>? BoardDisplaced { get; set; }

    private bool IsBoardDisplaced => BoardDisplaced?.Invoke() == true;

    /// <summary>Writes the live rig into the ACTIVE profile's store now, if it
    /// differs from what is stored. Call before shutdown so the last few seconds
    /// are not lost — and before a profile switch repoints the store, so the
    /// outgoing player keeps their own last edits.</summary>
    public void Flush() => PersistIfChanged();

    public void Dispose()
    {
        System.Threading.Timer? timer;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            timer = _timer;
            _timer = null;
        }

        try { _chain.EngineAttached -= OnEngineAttached; } catch { /* ignored */ }

        // Timer.Dispose() — the parameterless overload — does NOT wait for a
        // callback that is already executing, so the final flush below could
        // race a timer-thread PersistIfChanged: two concurrent SetRig writes of
        // the same file and two unsynchronized _lastPersistedSignature writes.
        // Dispose(WaitHandle) signals once every
        // in-flight callback has returned; the wait is bounded so a store hung
        // on the disk cannot hold shutdown hostage.
        if (timer is not null)
        {
            using var drained = new ManualResetEvent(false);
            if (timer.Dispose(drained) && !drained.WaitOne(DisposeDrainTimeout))
            {
                _logger?.Warning(AppLogCategory.Audio,
                    "ToneSessionService: a persist in flight did not finish within " +
                    $"{DisposeDrainTimeout.TotalSeconds:0} s of Dispose; flushing alongside it.");
            }
        }

        PersistIfChanged();
    }

    // ---- restore -------------------------------------------------------------

    /// <summary>
    ///     Restores the rig. Runs INLINE on the attaching thread, which for a
    ///     device change means inside <c>JuceAudioPlaybackService._gate</c> —
    ///     deliberately, and here is the analysis so the next reader does not
    ///     have to redo it.
    ///     <para>
    ///         <b>Cost:</b> roughly six <c>AddNode</c>s and thirty
    ///         <c>SetParam</c>s, all microsecond-scale P/Invokes, inside a lock
    ///         that is already held across an engine CREATE (tens to hundreds of
    ///         ms). <c>LoadAppConfig</c> is cached after its startup warm, so the
    ///         config read is a hit, not a file read. A capture's
    ///         <c>modelPath</c> is accepted immediately and loads asynchronously,
    ///         so no model parsing happens here either.
    ///     </para>
    ///     <para>
    ///         <b>Why not off-thread:</b> the pedalboard rebuilds itself when this
    ///         same <c>EngineAttached</c> reaches it — POSTED, so it lands after
    ///         this restore whatever the subscription order — and restoring inline
    ///         is what makes the chain it then reads already populated. Deferring
    ///         this would publish an EMPTY chain first and let the board render a
    ///         board with no pedals on it, then repopulate: a visible flash for no
    ///         benefit. (Not <c>NodesChanged</c>, which this comment claimed until
    ///         0.62 — that also fires for the board's OWN add/remove, so nothing
    ///         could subscribe to it, and nothing did: with the rig open, an engine
    ///         rebuild left every card bound to a dead node id.)
    ///     </para>
    ///     <para>
    ///         <b>No deadlock:</b> the apply path touches only
    ///         <c>IDspChainService</c>, which has its own unrelated gate. It does
    ///         raise <c>NodesChanged</c>, which the audio service handles by
    ///         requesting 48 kHz — but that handler hops to a
    ///         <c>Task.Run</c>, so it merely waits for this rebuild to release
    ///         the gate rather than deadlocking against it.
    ///     </para>
    /// </summary>
    private void OnEngineAttached() => Restore(clearWhenNothingStored: false);

    /// <summary>
    ///     Re-applies THIS seat's rig for the profile the store is scoped to NOW.
    ///     Called after a profile switch, once
    ///     <see cref="IProfileToneRigStore.Rescope" /> has repointed the store —
    ///     never before, or the outgoing player's board is what gets loaded.
    ///     <para>
    ///         <b>It waits for an engine, like every other restore path here.</b>
    ///         "The chain has no nodes" is not "no engine is attached"
    ///         (<see cref="IDspChainService.HasEngine" /> is the only member that
    ///         answers the second), and firing a rig at a null handle turns every
    ///         module into a failed managed op that no native code ever sees. With
    ///         no engine there is nothing to fix up anyway: the chain is empty, so
    ///         nothing of the previous profile is live, and the next
    ///         <see cref="IDspChainService.EngineAttached" /> restores from the
    ///         now-correct store.
    ///     </para>
    ///     <para>
    ///         <b>Nothing stored CLEARS the board</b> — the one place this differs
    ///         from the attach path. On an engine attach the chain is already empty
    ///         so "apply nothing" and "clear" are the same thing; on a profile
    ///         switch they are not. Leaving the previous player's pedals up would
    ///         both show the wrong board and, five seconds later, persist it into
    ///         the new profile as if they had built it.
    ///     </para>
    /// </summary>
    public void ReloadForActiveProfile()
    {
        lock (_gate)
        {
            if (_disposed) return;
        }

        if (!_chain.HasEngine)
        {
            // Re-arm the persist compare so the first poll after the engine
            // arrives measures against the new profile, not the old one's rig.
            lock (_gate) _lastPersistedSignature = string.Empty;
            return;
        }

        Restore(clearWhenNothingStored: true);
    }

    /// <summary>
    ///     The one restore path, shared by an engine attach
    ///     (<paramref name="clearWhenNothingStored" /> false) and a profile switch
    ///     (true). Both need the deferral loop below, which is why they share this
    ///     rather than each having their own.
    /// </summary>
    private void Restore(bool clearWhenNothingStored)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_restoring)
            {
                // DEFER, never drop. A restore already running belongs to the
                // engine that was live when it started; the attach arriving now
                // is a DIFFERENT (or the first real) engine whose native chain is
                // empty. Returning here — which is what this did — meant nothing
                // ever restored that engine, because EngineAttached does not fire
                // again until the next rebuild. That is the whole-session "no
                // tone rig at all" failure.
                _restorePending = true;
                // ...and the deferred request's own INTENT must survive with it,
                // which is what the loop below reads back. A profile switch
                // deferred behind an in-flight restore still has to CLEAR a seat
                // the incoming profile stores nothing for; re-running with the
                // ORIGINAL flag instead leaves the outgoing player's board up and
                // lets the next poll save it as the new player's. Pinned by
                // ProfileToneRigTests.AProfileSwitchLandingMidRestore_IsDeferred_AndStillClears.
                //
                // The OR (rather than a plain assignment) is the conservative
                // reading when two requests are deferred into one re-run. It is
                // deliberately NOT claimed as a guard: it is currently
                // unobservable, because the only caller passing false is an engine
                // attach and an attach empties the chain, so there is never
                // anything left for the clear it would drop to remove. It costs
                // one character and stays correct if a third caller appears.
                _restorePendingClear |= clearWhenNothingStored;
                return;
            }

            _restoring = true;
            // Tick on the way IN and again on the way OUT, so a persist whose
            // snapshot spanned a whole restore — start to finish — sees a
            // different generation and drops its partial board. A plain
            // "is a restore running now?" re-check cannot see that case.
            _restoreGeneration++;
            _restorePendingClear = false;
        }

        try
        {
            // Loop rather than recurse: an attach that lands while RestoreOnce is
            // running is picked up here instead of being lost.
            while (true)
            {
                RestoreOnce(clearWhenNothingStored);
                lock (_gate)
                {
                    if (_disposed || !_restorePending) return;
                    _restorePending = false;
                    clearWhenNothingStored = _restorePendingClear;
                    _restorePendingClear = false;
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                _restoring = false;
                _restoreGeneration++;
                _restorePending = false;
                _restorePendingClear = false;
            }
        }
    }

    private void RestoreOnce(bool clearWhenNothingStored)
    {
        try
        {
            var preset = ResolveStartupTone();
            if (preset is null)
            {
                if (!clearWhenNothingStored) return;
                preset = EmptyRig;
            }

            // ApplyToPlayer, not Apply: this service exists once per seat and
            // must put its rig on ITS OWN chain. Seat 0 resolves to exactly the
            // pre-v28 Apply.
            var result = _presets.ApplyToPlayer(Player, preset);

            // A line that says "restored" while nothing reached the chain is the
            // log that hid this bug for a release: the owner's own report was
            // "restored 'Active rig' … Added = 0 … FailedOps = 3", read as an
            // engine-side rejection. Name the failure, and say whether an engine
            // was even there — that is the one fact that separates the two.
            if (result is { Added: 0, FailedOps: > 0 })
            {
                _logger?.Warning(AppLogCategory.Audio,
                    $"Tone session: '{preset.Name}' could NOT be restored for player {Player + 1} — " +
                    $"{result.FailedOps} op(s) failed and no module reached the chain " +
                    $"({(_chain.HasEngine ? "an engine IS attached" : "NO engine is attached")}). {result}.");
            }
            else
            {
                _logger?.Info(AppLogCategory.Audio,
                    $"Tone session: restored '{preset.Name}' onto a fresh engine for player {Player + 1} ({result}).");
            }

            // The signature is taken from the LIVE chain, not from what we asked
            // for: the applier fills defaults and the loudness pass appends a
            // trailing gain, so the chain that comes back is legitimately not the
            // preset that went in. Seeding from the preset would make the very
            // next poll see a "change" and rewrite config for nothing.
            _lastPersistedSignature = SignatureOfLiveChain();
        }
        catch (Exception ex)
        {
            _logger?.Warning(AppLogCategory.Audio,
                $"Tone session: restoring the saved rig failed; the board stays empty. {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    ///     The tone a fresh engine should come up with: the player's own last rig
    ///     when there is one and restoring is enabled, else the configured
    ///     first-run default. Returns null when neither applies (nothing stored,
    ///     no default configured) — the pre-0.37 empty board, still reachable by
    ///     clearing <c>DefaultTonePresetId</c>.
    /// </summary>
    private DspTonePreset? ResolveStartupTone()
    {
        var config = _config.LoadRigConfig();

        if (config.RestoreToneOnStartup && StoredRig() is { Modules.Count: > 0 } stored)
            return stored.ToPreset();

        // The first-run DEFAULT is seat 0's only. It exists so a fresh install
        // sounds like an amp rather than a raw DI for the player who is actually
        // playing — and the shipped default is capture-backed, so applying it to
        // every seat would load a NAM model per UNUSED seat on every startup and
        // every device change. A seat past 0 with no tone of its own restores
        // nothing.
        if (Player > 0) return null;

        var defaultId = (config.DefaultTonePresetId ?? string.Empty).Trim();
        if (defaultId.Length == 0) return null;

        var preset = _presets.FindById(defaultId);
        if (preset is null)
        {
            // A default that does not exist any more is a real possibility across
            // versions (a preset can be renamed or dropped). Say so once rather
            // than silently starting empty, which reads to the player as "the
            // amp is broken".
            _logger?.Warning(AppLogCategory.Audio,
                $"Tone session: DefaultTonePresetId '{defaultId}' matches no preset; starting with an empty board.");
        }

        return preset;
    }

    /// <summary>
    ///     This seat's stored rig, under the ACTIVE PROFILE. A seat with no entry
    ///     returns null so it falls through to the first-run default preset rather
    ///     than inheriting player 1's board — or the previous PROFILE's board.
    /// </summary>
    private UserTonePresetDto? StoredRig() => _rigStore.GetRig(Player);

    /// <summary>
    ///     The "no rig" rig. Applying it to a live chain is what CLEARS a board —
    ///     <see cref="RsToneConversion.DcRigApplier" /> removes every node that has
    ///     no target module — which is how a profile with nothing stored gets an
    ///     empty board instead of the previous profile's.
    /// </summary>
    private static DspTonePreset EmptyRig => new(
        "active", "Empty rig", DspToneCategory.Clean, string.Empty,
        new RsToneConversion.DcRigPreset([]), DspToneOrigin.User);

    // ---- persist -------------------------------------------------------------

    /// <summary>
    ///     Writes the live rig into the profile's store — but ONLY when what it
    ///     snapshotted is a FULLY BUILT rig.
    ///     <para>
    ///         <b>A half-built rig must never be written over the player's saved
    ///         one.</b> A <c>_restoring</c> check taken under
    ///         <see cref="_gate" />, with the lock released and the snapshot and the
    ///         write then run unlocked, is not enough. So: the 1 s persist timer passes the check
    ///         → a device change / NAM 48 kHz request / watchdog recovery raises
    ///         <c>EngineAttached</c> → <see cref="Restore" /> begins adding the
    ///         saved nodes ONE AT A TIME → <see cref="SnapshotLiveRig" /> returns
    ///         2 of the rig's 6 → non-empty, so the <c>Count == 0</c> guard (which
    ///         exists for exactly this class of loss) lets it through → the
    ///         player's board is SHORTENED on disk. The
    ///         deferral-path tests do not cover persist-during-restore.
    ///     </para>
    ///     <para>
    ///         The fix is a GENERATION compare, not a longer lock hold across the
    ///         snapshot: <see cref="_restoreGeneration" /> ticks on every entry to
    ///         and exit from a restore, so a snapshot that spans one is detected
    ///         even when the restore has finished by the time we look. The compare
    ///         and the write happen together under <see cref="_gate" />, which is
    ///         what closes the window — re-checking and then writing outside it
    ///         just makes the window smaller.
    ///     </para>
    /// </summary>
    private void PersistIfChanged()
    {
        long generation;
        lock (_gate)
        {
            if (_disposed && _timer is not null) return;
            if (_restoring) return;
            generation = _restoreGeneration;
        }

        // A song's tone is on the board: it is not the player's to save.
        if (IsBoardDisplaced) return;

        try
        {
            var rig = SnapshotLiveRig();
            var modules = rig.Modules;
            // An empty chain is NOT persisted. It is indistinguishable from the
            // engine being down (or not yet up), and overwriting a good saved rig
            // with nothing — on a device change, say — would lose the very thing
            // this service exists to keep. Clearing the board is not a state worth
            // remembering anyway.
            if (modules.Count == 0) return;

            var signature = Signature(rig);

            lock (_gate)
            {
                // A restore started, or started AND finished, while we were
                // snapshotting: the list above is a partial board. Drop it — the
                // next poll reads the finished rig, and its signature will differ
                // from whatever is stored, so nothing is lost by waiting.
                if (_restoring || _restoreGeneration != generation) return;
                if (signature == _lastPersistedSignature) return;
                // …and again AFTER the snapshot: a song tone that took the board
                // over while we were reading it sets the flag before its first
                // apply, so a snapshot that saw any of it sees the flag here.
                if (IsBoardDisplaced) return;

                var dto = UserTonePresetDto.FromPreset(new DspTonePreset(
                    "active", "Active rig", DspToneCategory.Clean, string.Empty,
                    rig, DspToneOrigin.User));

                // Into the ACTIVE PROFILE's store, for THIS seat. The store's scope is
                // pinned rather than resolved per write, which is what makes a flush
                // ordered before a profile switch land on the outgoing player.
                // Inside the lock so a restore cannot start between the check and
                // the write. Lock order is ToneSessionService._gate ->
                // ProfileToneRigStore._gate and nothing takes them the other way.
                _rigStore.SetRig(Player, dto);
                // …and the signature is written under the same lock it is read
                // under at ReloadForActiveProfile, which it was not before.
                _lastPersistedSignature = signature;
            }
        }
        catch (Exception ex)
        {
            _logger?.Warning(AppLogCategory.Audio,
                $"Tone session: persisting the live rig failed. {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>The live rig — the shared <see cref="DcRigSnapshot" /> loop, which
    /// carries the string params and the branch tag (without them a restored rig
    /// brings back a MODELLESS namamp, which is a silent amp) — WITH its scenes
    /// (<see cref="DcRigSnapshot.FromChain" />): the scenes are managed state beside
    /// the chain, so a snapshot of the nodes alone would persist a rig that forgot
    /// them at the next launch.</summary>
    private DcRigPreset SnapshotLiveRig() => DcRigSnapshot.FromChain(_chain);

    private string SignatureOfLiveChain() => Signature(SnapshotLiveRig());

    /// <summary><see cref="Signature(IReadOnlyList{DcRigModule})" /> plus the
    /// rig's scenes, so renaming or storing a scene is a change worth writing.</summary>
    internal static string Signature(DcRigPreset rig)
    {
        var modules = Signature(rig.Modules);
        if (rig.Scenes is not { } scenes) return modules;
        var dto = RigSceneSetDto.From(scenes);
        return modules + "scenes:" + System.Text.Json.JsonSerializer.Serialize(dto);
    }

    /// <summary>
    ///     A cheap value-identity of a rig, used only to decide whether a write is
    ///     needed. Everything that round-trips through
    ///     <see cref="UserTonePresetDto" /> is in it — module order, params,
    ///     bypass, string params and branch tags — so a change this misses is a
    ///     change that would not have been persisted anyway. Params are ordered
    ///     because a dictionary's enumeration order is not a contract.
    /// </summary>
    internal static string Signature(IReadOnlyList<DcRigModule> modules)
    {
        var sb = new System.Text.StringBuilder(modules.Count * 48);
        foreach (var m in modules)
        {
            sb.Append(m.ModuleTypeId).Append(m.Bypassed ? "!" : "").Append('@').Append(m.Branch).Append('{');
            foreach (var p in m.Params.OrderBy(p => p.Key, StringComparer.Ordinal))
                sb.Append(p.Key).Append('=').Append(p.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(',');
            if (m.StringParams is { Count: > 0 } strings)
            {
                foreach (var s in strings.OrderBy(s => s.Key, StringComparer.Ordinal))
                    sb.Append(s.Key).Append("=\"").Append(s.Value).Append("\",");
            }

            if (m.ControlAssignments.Count > 0)
                sb.Append("controls:").Append(System.Text.Json.JsonSerializer.Serialize(m.ControlAssignments));
            sb.Append("};");
        }

        return sb.ToString();
    }
}
