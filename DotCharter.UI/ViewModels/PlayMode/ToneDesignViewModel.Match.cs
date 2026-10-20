// ToneDesignViewModel: "Match from audio" - ToneMatchSource, the isolate-and-match run and its winner published as the designed tone.
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Tone.Ai;
using DotCharter.Services.Ai.Match;
using DotCharter.Services.Ai.Match.Search;
using DotCharter.Services.Ai.Match.Model;
using DotCharter.Services.Ai.Match.Similarity;
using DotCharter.Tone.Audio.Dsp.Presets;
using DotCharter.Tone.UI.ViewModels.PlayMode;

namespace DotCharter.UI.ViewModels.PlayMode;

/// <summary>
///     What the panel needs to know about the song in order to match a tone FROM
///     it: where the audio is, which tone-change spans the chart declares, and
///     where the playhead sits. The HOST answers this — the chart, the beat map
///     and the transport are its business, and
///     <see cref="ToneChangeSpans.Resolve" /> is the one place that turns
///     placements into time ranges.
/// </summary>
/// <param name="AudioPath">Absolute path to the song's ORIGINAL mix. Never a
/// stem: the isolator owns the separation and its cache, and an Opus stem is
/// playback-only.</param>
/// <param name="Spans">Every tone-change span in time order, or empty.</param>
/// <param name="PositionMs">The playhead, which picks the span the player is
/// standing in.</param>
/// <param name="SongLengthMs">Used only for the no-tone-changes fallback.</param>
public sealed record ToneMatchSource(
    string? AudioPath,
    IReadOnlyList<ToneSpan> Spans,
    double PositionMs,
    double SongLengthMs);

/// <summary>
///     "Match from audio" — the second way into the same panel.
///
///     <para>
///         <b>Design a tone asks a language model what a rig should be; this asks
///         the AUDIO.</b> The song's guitar is isolated for the span the player is
///         standing in, a matcher renders candidate rigs over a dry DI and ranks
///         them by distance, and the winner is published as
///         <c>ToneDesignViewModel.Result</c> — the SAME property a
///         designed tone lands in. That is the whole integration: Hear it, Save as
///         tone, Discard and Tweak are not re-implemented here and never learn
///         that the rig came from audio rather than from a model.
///     </para>
///     <para>
///         <b>Every dependency is optional and arrives through
///         <see cref="ConfigureMatch" />, not the constructor.</b> The composition
///         root resolves them with <c>GetService</c>, so a build without the match
///         slice leaves <see cref="IsMatchAvailable" /> false and the button is not
///         on screen at all — a capability the app cannot honour must never look
///         live. Keeping them off the constructor also keeps this partial's merge
///         surface to members that exist nowhere else.
///     </para>
///     <para>
///         <b>The reference DI is the calibration-friendly half.</b> Matching
///         renders candidates over a dry guitar; rendering over the PLAYER's own
///         guitar and pickups is what makes the answer transfer to their rig, so
///         "Record 8 s of your playing" is offered first and the bundled clip is
///         the fallback. The recorder reads
///         <see cref="ILiveInputCapture.ReadInputCaptureWindow" /> — the
///         NON-destructive window — never <c>ReadInputCapture</c>, which is
///         single-consumer and feeding live note detection.
///     </para>
///     <para>
///         Nothing here runs on the audio thread, nothing blocks the UI thread,
///         and there is no <c>async void</c>. Cancellation is one token for the
///         whole flow, so the button cancels a recording, an isolation and a
///         search alike.
///     </para>
/// </summary>
public sealed partial class ToneDesignViewModel
{
    /// <summary>How long "Record my playing" captures for. Long enough for a few
    /// bars of anything, short enough that a player will actually do it.</summary>
    internal static readonly TimeSpan DiCaptureLength = TimeSpan.FromSeconds(8);

    /// <summary>The capture ring is ~0.7 s (see
    /// <see cref="ILiveInputCapture.ReadInputCapture(float[])" />), so the
    /// recorder polls well inside it. A poll that misses the ring is a GAP, and
    /// the recorder says so rather than splicing over it.</summary>
    internal static readonly TimeSpan DiPollInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>How much of the song to match when the chart declares no tone
    /// changes at all. A whole song averaged into one spectrum is a confident
    /// answer about nothing.</summary>
    internal static readonly TimeSpan FallbackSpan = TimeSpan.FromSeconds(30);

    /// <summary>Where the bundled reference DI lives, relative to the app
    /// directory. Absent in a build that did not ship it, which is a MESSAGE and
    /// not an exception.</summary>
    internal const string BundledReferenceDiRelativePath = "Assets/Ai/reference-di.wav";

    private Func<IToneMatcher?> _matcher = static () => null;
    private Func<IStemIsolator?> _isolator = static () => null;
    private Func<IRigEstimator?> _estimator = static () => null;
    private Func<IAudioPlaybackService?> _audio = static () => null;

    /// <summary>Injected so the recorder's poll loop costs a test nothing. The
    /// product passes <see cref="Task.Delay(TimeSpan, CancellationToken)" />.</summary>
    private Func<TimeSpan, CancellationToken, Task> _delay = Task.Delay;

    /// <summary>Overridable for tests and for a host that ships the clip
    /// elsewhere; resolved against the app directory by default.</summary>
    private Func<string?> _bundledReferenceDi = static () =>
        Path.Combine(AppContext.BaseDirectory, BundledReferenceDiRelativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Reads the bundled clip: the one file-to-clip decoder the match lanes share.</summary>
    private readonly IAudioClipLoader _clips = new AudioClipLoader();

    /// <summary>
    ///     The BUILT-IN dry guitar: a clip this build generates rather than reads.
    ///     <para>
    ///         <b>It is what makes "Match from audio" work without recording
    ///         first.</b> <see cref="BundledReferenceDiRelativePath" /> names a
    ///         file no build actually deploys — the asset glob EMBEDS it and
    ///         nothing copies it out — so the first press of Match on a
    ///         fresh install would answer "no reference DI" and the feature would look
    ///         broken. The host supplies
    ///         <c>Similarity.ReferenceDi.Medley</c>, the same fixed 20 s
    ///         Karplus-Strong take the retrieval library was rendered over, which
    ///         is also the better default: matching over the clip the library
    ///         itself used removes one whole source of disagreement.
    ///     </para>
    ///     <para>
    ///         A FUNC and not a direct call, for the reason every other dependency
    ///         here is one: this view model resolves nothing for itself. Null by
    ///         default, so a host that wires nothing behaves exactly as before.
    ///     </para>
    /// </summary>
    private Func<MonoAudio?> _builtInReferenceDi = static () => null;

    private CancellationTokenSource? _matchCts;

    /// <summary>The dry guitar every candidate is rendered over. Held for the life
    /// of the panel so a second match does not re-record or re-read it.</summary>
    private MonoAudio? _referenceDi;

    /// <summary>
    ///     The host hands the match slice in. Every argument is a
    ///     <c>Func</c> resolved per call rather than a captured instance, for the
    ///     same reason <c>IToneDesignService</c> is: a service can be registered
    ///     (or absent) independently of this view model's lifetime.
    /// </summary>
    /// <param name="matcher">The search itself. Null ⇒ the whole feature is
    /// hidden.</param>
    /// <param name="isolator">Guitar-from-song separation.</param>
    /// <param name="estimator">Optional one-shot prior from a trained model.</param>
    /// <param name="audio">Optional: the engine, for recording the player's own
    /// DI. Without it only the bundled clip is offered.</param>
    /// <param name="delay">Test seam for the recorder's poll loop.</param>
    /// <param name="bundledReferenceDi">Test seam for the bundled clip's
    /// path.</param>
    /// <param name="builtInReferenceDi">The clip this build GENERATES when
    /// nothing was recorded and no file is on disk — the product passes
    /// <c>Similarity.ReferenceDi.Medley</c>. Without it a fresh install cannot
    /// match at all until the player records; see
    /// <see cref="_builtInReferenceDi" />.</param>
    /// <param name="modelDownloads">How the player GETS the estimator's model.
    /// Null ⇒ the offer row is off screen and the prior stays whatever it is;
    /// the match itself is unaffected either way.</param>
    public void ConfigureMatch(
        Func<IToneMatcher?> matcher,
        Func<IStemIsolator?> isolator,
        Func<IRigEstimator?>? estimator = null,
        Func<IAudioPlaybackService?>? audio = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<string?>? bundledReferenceDi = null,
        Func<MonoAudio?>? builtInReferenceDi = null,
        Func<IRigEstimatorModelDownloadService?>? modelDownloads = null)
    {
        _matcher = matcher;
        _isolator = isolator;
        if (estimator != null) _estimator = estimator;
        if (modelDownloads != null) _modelDownloads = modelDownloads;
        if (audio != null) _audio = audio;
        if (delay != null) _delay = delay;
        if (bundledReferenceDi != null) _bundledReferenceDi = bundledReferenceDi;
        if (builtInReferenceDi != null) _builtInReferenceDi = builtInReferenceDi;

        OnPropertyChanged(nameof(IsMatchAvailable));
        OnPropertyChanged(nameof(CanRecordDi));
        MatchCommand.NotifyCanExecuteChanged();
        RecordDiCommand.NotifyCanExecuteChanged();
        RefreshMatchModelOffer();
    }

    // ---- the trained model, and how the player GETS it ------------------------
    //
    // The estimator is an optional PRIOR: without it the flow still matches, by
    // search alone. That is why BuildPriorAsync's `is not { IsAvailable: true }`
    // is a silent `return null` — and why, before this block existed, the model
    // lane was invisible from here in both directions. Nothing said the guess
    // was unavailable, and nothing offered to make it available.

    private Func<IRigEstimatorModelDownloadService?> _modelDownloads = () => null;

    /// <summary>True when a downloader exists and the model is not installed —
    /// i.e. when there is a one-click offer to make.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMatchModelOffer))]
    [NotifyCanExecuteChangedFor(nameof(DownloadMatchModelCommand))]
    private bool _matchModelMissing;

    /// <summary>True while the model is downloading from this panel.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMatchModelOffer))]
    [NotifyCanExecuteChangedFor(nameof(DownloadMatchModelCommand))]
    private bool _matchModelDownloading;

    /// <summary>The offer's own line: its size, or the installer's current step.</summary>
    [ObservableProperty] private string _matchModelOffer = string.Empty;

    /// <summary>Whether the offer row is on screen at all. Hidden while a MATCH
    /// is running: the model cannot join a search already in flight, and a
    /// button that silently would not apply is worse than no button.</summary>
    public bool ShowMatchModelOffer => (MatchModelMissing || MatchModelDownloading) && !IsMatching;

    /// <summary>Recomputes the offer. A file-presence check only — never
    /// <see cref="IRigEstimator.IsAvailable" />, which builds an ONNX session
    /// and would do it on the UI thread that is drawing this panel.</summary>
    public void RefreshMatchModelOffer()
    {
        var downloads = _modelDownloads();
        MatchModelMissing = downloads is { IsInstalled: false };
        if (MatchModelMissing && !MatchModelDownloading)
            MatchModelOffer = Loc.Format("ToneMatch_ModelOffer", downloads!.ApproximateMegabytes);
        else if (!MatchModelMissing)
            MatchModelOffer = string.Empty;
    }

    private bool CanDownloadMatchModel => MatchModelMissing && !MatchModelDownloading;

    /// <summary>
    ///     The one-click download, mirroring how lyric auto-sync offers its own
    ///     acoustic model: progress on the line the user is already reading,
    ///     failure reported there too, and never a throw into the panel.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanDownloadMatchModel))]
    private async Task DownloadMatchModelAsync()
    {
        var downloads = _modelDownloads();
        if (downloads is null || MatchModelDownloading) return;

        MatchModelDownloading = true;
        string? outcome = null;
        try
        {
            // InlineProgress, not Progress<T>: the latter posts, so its last
            // step lands AFTER the outcome is written and the row ends on
            // "Unpacking…" over an installed model. Same reason the match
            // flow's own status uses it.
            var progress = new InlineProgress(line => Post(() => MatchModelOffer = line));
            var result = await downloads
                .EnsureInstalledAsync(progress, CancellationToken.None)
                .ConfigureAwait(true);

            if (!result.Installed)
                outcome = Loc.Format("ToneMatch_ModelFailed", result.Reason ?? string.Empty);
        }
        catch (OperationCanceledException)
        {
            outcome = Loc["ToneMatch_Cancelled"];
        }

        MatchModelDownloading = false;

        // Refresh first, then the outcome over it — the other order has the
        // refresh rewrite the offer text and lose the reason the download failed.
        RefreshMatchModelOffer();
        if (outcome is not null) MatchModelOffer = outcome;
    }

    /// <summary>
    ///     Where the song, its tone-change spans and the playhead come from.
    ///     Assigned by the host that knows — <c>PlayModeToneRigViewModel.cs</c>'s
    ///     <c>BuildRigScreen</c>, beside <c>SongContextProvider</c>, which
    ///     is the only place that can reach
    ///     <c>ProjectState.ActiveArrangement.ToneChanges</c> and the beat map.
    ///     <para>
    ///         <b>A host that assigns nothing still matches.</b> The transport
    ///         knows its own file (<see cref="IPlaybackTransport.CurrentAudioPath" />),
    ///         so an unassigned provider degrades to "the first
    ///         <see cref="FallbackSpan" /> of whatever is loaded" rather than to a
    ///         dead button — the chart editor's rig host wires neither a flow song
    ///         nor this, and it can still match.
    ///     </para>
    /// </summary>
    public Func<ToneMatchSource?>? MatchSourceProvider { get; set; }

    /// <summary>The host's answer when it has one, and the transport's own loaded
    /// file when it does not. Spans are only ever the host's: a tone change is a
    /// CHART fact and the engine knows nothing about it.</summary>
    private ToneMatchSource? ResolveSource()
    {
        var provided = MatchSourceProvider?.Invoke();
        if (provided is { AudioPath.Length: > 0 }) return provided;

        var audio = _audio();
        if (audio is null || string.IsNullOrWhiteSpace(audio.CurrentAudioPath)) return provided;

        return new ToneMatchSource(
            audio.CurrentAudioPath,
            provided?.Spans ?? Array.Empty<ToneSpan>(),
            provided?.PositionMs ?? audio.CurrentTimeMs,
            provided?.SongLengthMs ?? 0);
    }

    // ---- state ----------------------------------------------------------------

    /// <summary>False in a build with no matcher registered. The button, the
    /// results block and the DI row are all off screen in that case.</summary>
    public bool IsMatchAvailable => _matcher() != null;

    /// <summary>True only while the DI recorder or the match flow is in
    /// flight.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MatchCommand))]
    [NotifyCanExecuteChangedFor(nameof(RecordDiCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelMatchCommand))]
    [NotifyPropertyChangedFor(nameof(ShowMatchModelOffer))]
    private bool _isMatching;

    /// <summary>The flow's own progress line — deliberately NOT
    /// <c>Status</c>, which belongs to the design conversation. Both can
    /// be on screen at once and they mean different things.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMatchStatus))]
    private string _matchStatus = string.Empty;

    public bool HasMatchStatus => MatchStatus.Length > 0;

    /// <summary>
    ///     A REAL bar for the one stage of a match that is long enough to need
    ///     one: the first-match library build (96 renders, ~4 min). The status
    ///     line is the matcher's <c>IProgress&lt;string&gt;</c> and that contract
    ///     is additive-only, so the fraction is read back OUT of the line
    ///     (<see cref="RigLibraryProgress.TryParseBuilding" />, the inverse of the
    ///     one place that writes it) rather than through a second channel.
    ///     0..1; meaningful only while <see cref="HasMatchProgress" />.
    /// </summary>
    [ObservableProperty] private double _matchProgressFraction;

    /// <summary>True while the status line is a library-build line; the bar is
    /// shown exactly then. Every other stage keeps the line alone — an
    /// indeterminate bar over "Searching for a rig…" would say nothing the
    /// words do not.</summary>
    [ObservableProperty] private bool _hasMatchProgress;

    partial void OnMatchStatusChanged(string value)
    {
        if (RigLibraryProgress.TryParseBuilding(value, out var done, out var total))
        {
            MatchProgressFraction = (double)done / total;
            HasMatchProgress = true;
        }
        else
        {
            HasMatchProgress = false;
            MatchProgressFraction = 0;
        }
    }

    /// <summary>Every fault this flow can reach, already localized. The service's
    /// own words go to the log, never here.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMatchError))]
    private string _matchError = string.Empty;

    public bool HasMatchError => MatchError.Length > 0;

    /// <summary>What the candidates were rendered over, in the player's words:
    /// their own playing, or the bundled clip.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReferenceDi))]
    private string _referenceDiLabel = string.Empty;

    public bool HasReferenceDi => _referenceDi != null;

    /// <summary>True when the engine is there to record from. Without it the
    /// bundled clip is the only source and the record button is hidden.</summary>
    public bool CanRecordDi => _audio() != null;

    /// <summary>Ranked best-first. Empty until a match has answered, which is
    /// what collapses the whole results block.</summary>
    public ObservableCollection<ToneMatchCandidateViewModel> MatchCandidates { get; } = [];

    public bool HasMatchCandidates => MatchCandidates.Count > 0;

    /// <summary>Selecting a candidate PUBLISHES it as
    /// <c>ToneDesignViewModel.Result</c>, which is what makes Hear it,
    /// Save as tone, Discard and Tweak operate on it with no knowledge that it
    /// came from audio.</summary>
    [ObservableProperty] private ToneMatchCandidateViewModel? _selectedMatchCandidate;

    partial void OnSelectedMatchCandidateChanged(ToneMatchCandidateViewModel? value)
    {
        if (value is null) return;
        Result = new ToneDesignResultViewModel(value.AsDesignResult, value.ChainSummary, Loc);
    }

    /// <summary>
    ///     <b>Discard drops the candidate list too, and this is the hook that does
    ///     it.</b> <c>Discard</c> clears <c>Result</c> and the
    ///     conversation; leaving a ranked list on screen underneath it would offer
    ///     the player a row whose tone the panel has just thrown away and put
    ///     back — the next click would silently re-apply it. Hooking the RESULT
    ///     rather than editing <c>Discard</c> keeps the match flow entirely in this
    ///     file.
    /// </summary>
    protected override void OnResultChangedCore(ToneDesignResultViewModel? value)
    {
        if (value != null || MatchCandidates.Count == 0) return;
        ClearMatch();
    }

    private void ClearMatch()
    {
        MatchCandidates.Clear();
        // Null is the no-op branch of OnSelectedMatchCandidateChanged, so this
        // cannot loop back into Result.
        SelectedMatchCandidate = null;
        OnPropertyChanged(nameof(HasMatchCandidates));
        MatchStatus = string.Empty;
        MatchError = string.Empty;
    }

    private bool CanMatch => !IsMatching && !IsRunning && IsMatchAvailable;

    private bool CanCancelMatch => IsMatching;

    // ---- the flow -------------------------------------------------------------

    /// <summary>
    ///     Isolate → search → publish. Each stage reports through
    ///     <see cref="MatchStatus" /> and every failure ends as a MESSAGE: a
    ///     matcher that is not registered, a host with no song, a separation that
    ///     produced nothing and a cancel are four different sentences, because
    ///     "matching failed" tells a player nothing they can act on.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanMatch))]
    private async Task MatchAsync()
    {
        var matcher = _matcher();
        var isolator = _isolator();
        if (matcher is null || isolator is null)
        {
            FailMatch("ToneMatch_Fault_NotAvailable");
            return;
        }

        var source = ResolveSource();
        if (source is null || string.IsNullOrWhiteSpace(source.AudioPath))
        {
            FailMatch("ToneMatch_Fault_NoAudio");
            return;
        }

        MatchError = string.Empty;
        var cts = _matchCts = new CancellationTokenSource();
        IsMatching = true;
        try
        {
            var di = await EnsureReferenceDiAsync(cts.Token).ConfigureAwait(true);
            if (di is null)
            {
                FailMatch("ToneMatch_Fault_NoReferenceDi");
                return;
            }

            var (from, to) = PickSpan(source, FallbackSpan);
            MatchStatus = Loc["ToneMatch_Isolating"];
            // InlineProgress, not Progress<T>: the framework type defers the line to a
            // later dispatcher pass (or the thread pool), so a matcher's "searching"
            // could land AFTER the cancel branch wrote "Cancelled" — which is exactly
            // how CancelDuringTheSearch_ReportsCancelled failed on a loaded box. Same
            // rule as the design flow above.
            var progress = new InlineProgress(line => Post(() => MatchStatus = line));

            var target = await isolator
                .IsolateGuitarAsync(source.AudioPath!, from, to, progress, cts.Token)
                .ConfigureAwait(true);

            // An isolator that answers with nothing is not an exception — the
            // stem cache can legitimately hold silence for a span with no guitar
            // in it — but it is the end of the flow and the player has to be told
            // which of the two it was.
            //
            // **And "nothing" is not only an EMPTY buffer.** A separated stem for a
            // stretch of song with no guitar in it is a full-length buffer of
            // digital silence, which is not `Length == 0` and went straight through
            // this guard into a match that reported a confident percentage about an
            // all-zero feature vector. The same trim the matcher applies decides it,
            // so the panel and the matcher cannot disagree about what "has playing
            // in it" means.
            if (target.Samples.Length == 0 ||
                !MatchTargetAudio.IsMeasurable(MatchTargetAudio.Trim(target)))
            {
                FailMatch("ToneMatch_Fault_NoStems");
                return;
            }

            MatchStatus = Loc["ToneMatch_Searching"];
            var prior = await BuildPriorAsync(target, cts.Token).ConfigureAwait(true);
            var result = await matcher
                .MatchAsync(new ToneMatchRequest(target, di, prior), progress, cts.Token)
                .ConfigureAwait(true);

            Publish(result);
        }
        catch (OperationCanceledException)
        {
            // The contract promises a cancel is a cancel, not a fault: the status
            // line says so and no error banner opens.
            MatchStatus = Loc["ToneMatch_Cancelled"];
            MatchError = string.Empty;
        }
        catch (Exception ex)
        {
            FailMatch("ToneMatch_Fault_Failed", ex);
        }
        finally
        {
            IsMatching = false;
            if (ReferenceEquals(_matchCts, cts)) _matchCts = null;
            cts.Dispose();
        }
    }

    /// <summary>Cancels whichever stage is in flight — the recorder and the match
    /// share one token, so one button is honest at every stage.</summary>
    [RelayCommand(CanExecute = nameof(CanCancelMatch))]
    private void CancelMatch()
    {
        var cts = _matchCts;
        if (cts == null) return;
        try { cts.Cancel(); }
        catch (ObjectDisposedException) { /* the run's finally already disposed it */ }
    }

    /// <summary>
    ///     The design result wins the prior, and the estimator is the fallback —
    ///     not the other way round. A player who has just described the tone they
    ///     want has told us more than a model guessing from a stem, and the
    ///     contract carries exactly ONE prior.
    /// </summary>
    private async Task<ToneMatchPrior?> BuildPriorAsync(MonoAudio target, CancellationToken ct)
    {
        if (Result is { } designed) return new ToneMatchPrior(designed.Tone.Rig, "design");

        var estimator = _estimator();
        if (estimator is not { IsAvailable: true }) return null;

        MatchStatus = Loc["ToneMatch_Estimating"];
        try
        {
            var estimate = await estimator.EstimateAsync(target, ct).ConfigureAwait(true);
            return new ToneMatchPrior(estimate.Rig, $"estimator:{estimate.ModelId}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger?.Warning(AppLogCategory.Errors, "Rig estimator failed; the match runs search-only.", ex);
            // A prior is an optimisation. An estimator that failed must not take
            // the search down with it — it is the half of the flow that is
            // allowed to be absent by design.
            return null;
        }
    }

    private void Publish(ToneMatchResult result)
    {
        MatchCandidates.Clear();

        // RANKING distance, not the measured one: the matcher marks an implausible
        // rig down (no amp on a guitar part, two amps, a chain the sampler would
        // never draw) and re-sorting on the raw measurement would silently undo it.
        var ranked = result.Candidates.OrderBy(c => c.RankingDistance).ToList();
        for (var i = 0; i < ranked.Count; i++)
        {
            MatchCandidates.Add(new ToneMatchCandidateViewModel(
                ranked[i],
                i,
                ChainSummary(ranked[i].Rig),
                Result?.Tone.Category ?? UnknownMatchCategory,
                result.Notes,
                Loc));
        }

        OnPropertyChanged(nameof(HasMatchCandidates));

        if (MatchCandidates.Count == 0)
        {
            FailMatch("ToneMatch_Fault_NoCandidates");
            return;
        }

        // Best first, and SELECTED — publishing a ranked list and making the
        // player click the top row is a step that only ever has one answer.
        SelectedMatchCandidate = MatchCandidates[0];
        MatchStatus = Loc.Format("ToneMatch_Found", MatchCandidates.Count);
    }

    /// <summary>
    ///     The matcher answers with a RIG and a distance; nothing in the contract
    ///     carries a tone CATEGORY. Experimental is the honest bucket for "we do
    ///     not know" — it keeps a matched tone out of the curated rails instead of
    ///     filing every one of them under Lead.
    /// </summary>
    internal const DspToneCategory UnknownMatchCategory = DspToneCategory.Experimental;

    /// <summary>
    ///     <b>There is no percentage formula here.</b>
    ///     <para>
    ///         A formula <c>MatchPercent(distance, floor)</c> =
    ///         <c>100 × floor / max(distance, floor)</c>, where <c>floor</c> is the
    ///         target's self-distance. On real material that floor sits ABOVE every
    ///         candidate's distance, so the expression saturates and the panel shows
    ///         <b>100 % for every row</b> — even on a match
    ///         whose tones are plainly wrong.
    ///     </para>
    ///     <para>
    ///         The score is computed in the SERVICE lane now
    ///         (<c>Similarity.MatchScale</c>) and arrives on
    ///         <see cref="ToneMatchCandidate.Score" />, because it needs a second
    ///         MEASURED anchor — one render of an information-free reference rig over
    ///         the same DI — that only the matcher can produce. A view model cannot
    ///         invent a scale out of a distance, and this method's whole history is
    ///         what happens when one tries. Read
    ///         <c>DotCharter.Services/Ai/Match/Similarity/MatchScale.cs</c> for the
    ///         formula, both anchors and the calibration numbers.
    ///     </para>
    ///     <para>
    ///         <b>The panel's job is now to SHOW the three states</b>, not to compute
    ///         one: a percentage, "unrated" with its reason, and the caveat when a
    ///         candidate sits inside the target's own variation. See
    ///         <see cref="ToneMatchCandidateViewModel" />.
    ///     </para>
    /// </summary>
    /// <param name="score">The matcher's score for this candidate, or null when the
    /// run produced none (a fake matcher, a hand-built candidate).</param>
    /// <param name="loc">For the unrated reason and the caveat line.</param>
    internal static string MatchText(MatchScore? score, ILocalizationService loc)
    {
        ArgumentNullException.ThrowIfNull(loc);
        if (score?.Percent is { } percent) return loc.Format("ToneMatch_Percent", percent);
        return loc.Format("ToneMatch_UnratedWhy", UnratedReasonText(score?.UnratedReason, loc));
    }

    /// <summary>The unrated REASON in the app's language. An unknown code (a newer
    /// service, a hand-built score) falls back to "this result carries no score"
    /// rather than printing a machine token at a player.</summary>
    /// <param name="reason">One of <see cref="MatchUnrated" />, or null.</param>
    /// <param name="loc">The app's strings.</param>
    internal static string UnratedReasonText(string? reason, ILocalizationService loc)
    {
        ArgumentNullException.ThrowIfNull(loc);
        return reason switch
        {
            MatchUnrated.NoReference => loc["ToneMatch_Unrated_NoReference"],
            MatchUnrated.ReferenceTooClose => loc["ToneMatch_Unrated_ReferenceTooClose"],
            MatchUnrated.TargetTooShort => loc["ToneMatch_Unrated_TargetTooShort"],
            MatchUnrated.BlockGated => loc["ToneMatch_Unrated_BlockGated"],
            MatchUnrated.NoDistance => loc["ToneMatch_Unrated_NoDistance"],
            _ => loc["ToneMatch_Unrated_NotScored"]
        };
    }

    /// <summary>
    ///     Which stretch of song to match.
    ///     <para>
    ///         <b>The span the playhead is standing in</b>, because that is the
    ///         tone the charter placed there and the one the player is looking at.
    ///         A span too short to say anything about
    ///         (<see cref="ToneSpan.IsUseful" />) is skipped exactly as the chart
    ///         layer already decides it should be, and a chart with no usable span
    ///         at all falls back to the first <paramref name="fallback" /> of the
    ///         song — never the whole song, which averages every tone in it into
    ///         one confident answer about nothing.
    ///     </para>
    /// </summary>
    internal static (TimeSpan From, TimeSpan To) PickSpan(ToneMatchSource source, TimeSpan fallback)
    {
        var useful = source.Spans.Where(s => s.IsUseful).ToList();
        var hit = useful.FirstOrDefault(s => source.PositionMs >= s.StartMs && source.PositionMs < s.EndMs);

        // `FirstOrDefault` on a struct gives a zero-length span, which is exactly
        // the "not in one" answer — but it must not be confused with a real span
        // that starts at 0, so the length is what decides.
        if (hit.DurationMs <= 0 && useful.Count > 0) hit = useful[0];

        if (hit.DurationMs > 0)
            return (TimeSpan.FromMilliseconds(hit.StartMs), TimeSpan.FromMilliseconds(hit.EndMs));

        // NO USABLE CHART SPAN: `fallback` of song FROM THE PLAYHEAD, not from zero.
        //
        // Answering (0, fallback) and ignoring `PositionMs` is the shape of "the matched tone comes out cut off": a
        // chart with no tone changes — and every song that
        // is not in the library — would match the OPENING of the song wherever the player
        // was standing. Measured on `Polkadot Stingray - Sakasma`, whose separated
        // guitar stem is digital silence for its first ten seconds and −70 dBFS for
        // five more: half of the 30 s window handed to the matcher would be nothing at
        // all, and the tone would be built from the other half of an intro the player was
        // not listening to. Playhead 0 still gives (0, fallback).
        var song = source.SongLengthMs > 0 ? source.SongLengthMs : fallback.TotalMilliseconds;
        var length = Math.Min(song, fallback.TotalMilliseconds);
        // Slide back when the playhead is inside the last `length` of the song, so a
        // match near the end still gets a full window instead of a sliver.
        var start = Math.Clamp(source.PositionMs, 0, Math.Max(0, song - length));
        return (TimeSpan.FromMilliseconds(start), TimeSpan.FromMilliseconds(start + length));
    }

    // ---- the reference DI -----------------------------------------------------

    /// <summary>
    ///     Records the player's own dry guitar. The button exists because matching
    ///     over the PLAYER's pickups and hands is what makes the answer transfer
    ///     to their rig — a candidate that matches a stranger's DI can still be
    ///     wrong on theirs.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanMatch))]
    private async Task RecordDiAsync()
    {
        var audio = _audio();
        if (audio is null)
        {
            FailMatch("ToneMatch_Fault_NoInput");
            return;
        }

        MatchError = string.Empty;
        var cts = _matchCts = new CancellationTokenSource();
        IsMatching = true;
        try
        {
            var captured = await RecordDiCoreAsync(audio, DiCaptureLength, cts.Token).ConfigureAwait(true);
            if (captured is null) return;

            _referenceDi = captured;
            ReferenceDiLabel = Loc.Format("ToneMatch_DiFromYou", (int)Math.Round(captured.Duration.TotalSeconds));
            OnPropertyChanged(nameof(HasReferenceDi));
            MatchStatus = string.Empty;
        }
        catch (OperationCanceledException)
        {
            MatchStatus = Loc["ToneMatch_Cancelled"];
        }
        finally
        {
            IsMatching = false;
            if (ReferenceEquals(_matchCts, cts)) _matchCts = null;
            cts.Dispose();
        }
    }

    /// <summary>
    ///     The poll loop. Reads the NON-destructive window
    ///     (<see cref="ILiveInputCapture.ReadInputCaptureWindow" />) and
    ///     stitches the snapshots by their ABSOLUTE stream position, which
    ///     <see cref="ILiveInputCapture.TryGetInputCaptureSync" /> reports —
    ///     the streaming <c>ReadInputCapture</c> is single-consumer and is feeding
    ///     live note detection, so a recorder on it would starve the thing that
    ///     judges the player's notes.
    ///     <para>
    ///         A poll that arrives after the ring has wrapped leaves a HOLE. The
    ///         recorder reports it and gives up rather than splicing two unrelated
    ///         moments together: a spliced DI produces a confident match against
    ///         audio nobody played.
    ///     </para>
    /// </summary>
    private async Task<MonoAudio?> RecordDiCoreAsync(
        IAudioPlaybackService audio, TimeSpan length, CancellationToken ct)
    {
        var rate = audio.InputSampleRate;
        if (rate <= 0)
        {
            FailMatch("ToneMatch_Fault_NoInput");
            return null;
        }

        var wanted = (int)(length.TotalSeconds * rate);
        var window = new float[Math.Max(1, (int)(DiPollInterval.TotalSeconds * 3 * rate))];
        var samples = new List<float>(wanted);

        // Absolute position of the sample AFTER the last one taken, so a snapshot
        // that overlaps the previous one is trimmed instead of duplicated.
        //
        // It is ANCHORED before the first sample is kept, and that is the whole
        // reason the loop starts with a `continue`. The window read is
        // non-destructive and always hands back the newest ~0.6 s — which, on the
        // first poll, is audio from BEFORE the player pressed the button. A
        // streaming consumer drains the stale ring (see LivePlayViewModel's
        // activation drain); a windowed one cannot, so it anchors instead and
        // keeps only what was captured after the press.
        ulong? consumedTo = null;

        // A POLL BUDGET, because every other exit from this loop depends on a
        // DEVICE making progress. A capture that stops advancing — an engine
        // whose sync counter is live but whose ring stays empty — spins here with
        // the cancel button as the only way out, and the first version of this
        // loop did exactly that the moment the gap check was taken out of it.
        // Four times the polls the length needs is well past anything a busy box
        // produces.
        var polls = 0;
        var maxPolls = (int)(length.TotalSeconds / DiPollInterval.TotalSeconds) * 4 + 8;

        while (samples.Count < wanted)
        {
            ct.ThrowIfCancellationRequested();

            if (++polls > maxPolls)
            {
                FailMatch("ToneMatch_Fault_CaptureStalled");
                return null;
            }

            if (consumedTo is null)
            {
                if (audio.TryGetInputCaptureSync(out var anchor, out _))
                {
                    consumedTo = anchor;
                    MatchStatus = Loc.Format("ToneMatch_Recording", 0);
                }

                // No sync yet means no input block has ever arrived. Waiting is
                // right: the engine may have just been reconfigured, and the
                // cancel button is live throughout.
                await _delay(DiPollInterval, ct).ConfigureAwait(true);
                continue;
            }

            var n = audio.ReadInputCaptureWindow(window);
            if (n > 0 && audio.TryGetInputCaptureSync(out var totalCaptured, out _))
            {
                // The window's LAST sample is the newest captured one, so the
                // block starts `n` samples before the total.
                var start = totalCaptured >= (ulong)n ? totalCaptured - (ulong)n : 0;
                var end = start + (ulong)n;
                var from = consumedTo.Value;

                // The oldest sample in this snapshot is NEWER than the newest one
                // already taken: the ring wrapped between polls and the audio in
                // between is gone. Splicing the two halves together would hand the
                // matcher a DI nobody played, and it would match confidently.
                if (start > from)
                {
                    FailMatch("ToneMatch_Fault_CaptureGap");
                    return null;
                }

                var skip = (int)Math.Min((ulong)n, from - start);
                for (var i = skip; i < n; i++) samples.Add(window[i]);
                consumedTo = end > from ? end : from;
            }

            MatchStatus = Loc.Format(
                "ToneMatch_Recording",
                Math.Min(100, (int)(100L * samples.Count / Math.Max(1, wanted))));

            if (samples.Count >= wanted) break;
            await _delay(DiPollInterval, ct).ConfigureAwait(true);
        }

        if (samples.Count > wanted) samples.RemoveRange(wanted, samples.Count - wanted);
        return new MonoAudio(samples.ToArray(), rate);
    }

    /// <summary>
    ///     The recorded clip if there is one, else a clip file this build shipped,
    ///     else the GENERATED one, else null — which the caller turns into the one
    ///     message that says a match needs a dry guitar to render over.
    ///     <para>
    ///         The recorded take wins because it is the player's own pickups. A
    ///         file wins over the generated clip because a host that deliberately
    ///         ships one means it; no build ships one today, which is exactly why
    ///         the generated clip exists.
    ///     </para>
    /// </summary>
    private async Task<MonoAudio?> EnsureReferenceDiAsync(CancellationToken ct)
    {
        if (_referenceDi is { } already) return already;

        var path = _bundledReferenceDi();
        if (!string.IsNullOrWhiteSpace(path))
        {
            // The existence check is part of the load, so it runs on the worker too.
            MatchStatus = Loc["ToneMatch_LoadingDi"];
            var loaded = await Task.Run(() => _clips.TryLoad(path), ct).ConfigureAwait(true);
            if (loaded is not null) return Adopt(loaded);
        }

        // Generated, not decoded — but it still runs off the UI thread: the medley
        // is four 5 s Karplus-Strong takes and the first caller pays for all of
        // them.
        MatchStatus = Loc["ToneMatch_LoadingDi"];
        var built = await Task.Run(_builtInReferenceDi, ct).ConfigureAwait(true);
        return built is { Samples.Length: > 0 } ? Adopt(built) : null;

        MonoAudio Adopt(MonoAudio clip)
        {
            _referenceDi = clip;
            ReferenceDiLabel = Loc["ToneMatch_DiBundled"];
            OnPropertyChanged(nameof(HasReferenceDi));
            return clip;
        }
    }

    private void FailMatch(string key, Exception? exception = null)
    {
        // "See the log for details" was a promise this method did not keep: the
        // catch-all above swallowed the exception and nothing was written
        // anywhere. The reason now goes to the log (Errors) AND onto the panel,
        // so a user without the log still sees what failed.
        var reason = exception is null ? string.Empty : $" {exception.GetType().Name}: {exception.Message}";
        MatchError = Loc[key] + reason;
        MatchStatus = string.Empty;
        if (exception is not null)
            Logger?.Error(AppLogCategory.Errors, "Match from audio failed.", exception);
    }

    /// <summary>Test seam, the sibling of
    /// <c>SetResultForTests</c>: publishes finished candidates without a
    /// matcher, so the headless smoke tests can lay the results block out. A block
    /// nothing ever shows is never laid out, and that is where a silent resource
    /// fault hides.</summary>
    internal void SetMatchCandidatesForTests(ToneMatchResult result) => Publish(result);
}

/// <summary>
///     One candidate row. Everything on it is derived ONCE — a template that
///     recomputed a chain summary or a percentage per binding pass would do it on
///     the UI thread, once per row, on every pass.
///     <para>
///         <b>No headline number without the breakdown behind it (W5b).</b>
///         <see cref="MatchPercent" /> is nullable and <see cref="IsRated" /> is the
///         state a template switches on: a percentage, or UNRATED with a reason.
///         Zero is a measurement ("no better than a plain amp and cab"); unrated is
///         not a number at all, and the two must not render the same.
///         <see cref="Breakdown" /> carries the per-feature-block rows the panel
///         binds under the pill, and <see cref="ScaleText" /> says what the two ends
///         of the scale mean.
///     </para>
/// </summary>
public sealed class ToneMatchCandidateViewModel
{
    public ToneMatchCandidateViewModel(
        ToneMatchCandidate candidate,
        int rank,
        string chainSummary,
        DspToneCategory category,
        IReadOnlyList<string> notes,
        ILocalizationService loc)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(loc);

        Rank = rank;
        Distance = candidate.Distance;
        Score = candidate.Score;
        MatchPercent = candidate.Score?.Percent;
        IsRated = MatchPercent is not null;
        UnratedReason = candidate.Score?.UnratedReason;
        UnratedText = IsRated
            ? string.Empty
            : ToneDesignViewModel.UnratedReasonText(UnratedReason, loc);
        ChainSummary = chainSummary;
        Origin = candidate.Origin;
        Title = loc.Format("ToneMatch_CandidateTitle", rank + 1);
        MatchText = ToneDesignViewModel.MatchText(candidate.Score, loc);
        ScaleText = loc["ToneMatch_ScaleExplains"];

        // The caveat, not the score: a candidate at or inside the passage's own
        // variation is as close as this metric can SAY, and the remaining percent is
        // not reachable by searching harder.
        SelfDistancePercent = candidate.Score?.SelfDistancePercent;
        WithinTargetNoise = candidate.Score?.WithinTargetNoise ?? false;
        NoiseCaveatText = WithinTargetNoise && SelfDistancePercent is { } floor
            ? loc.Format("ToneMatch_WithinNoise", floor)
            : string.Empty;

        Breakdown = candidate.Score is null
            ? []
            : candidate.Score.WorstBlocks(4)
                .Select(b => new ToneMatchBlockViewModel(
                    b.Name,
                    (int)Math.Round(Math.Clamp(b.Share, 0, 1) * 100),
                    !double.IsNaN(b.ReferenceContribution) && b.Contribution > b.ReferenceContribution,
                    loc))
                .ToList();

        BreakdownText = Breakdown.Count == 0
            ? string.Empty
            : string.Join(", ", Breakdown.Select(b => b.Text));

        // The tone a matched candidate becomes if the player keeps it. USER
        // origin, like every other tone this panel produces: the factory apply
        // path loudness-normalizes, and a matched tone re-levelled on its way to
        // the chain is not the tone that was matched.
        Tone = new DspTonePreset(
            $"match-{rank + 1}",
            Title,
            category,
            Rationale(candidate, loc, BreakdownText),
            candidate.Rig,
            DspToneOrigin.User);

        // Shaped as a design result so the existing result block, and the four
        // actions under it, take it without knowing where it came from. The
        // confidence pill shows the match % — the same number as the row — and 0
        // for an UNRATED one, which the pill's own text then explains rather than
        // implying a measured zero.
        AsDesignResult = new ToneDesignResult(
            Tone,
            Tone.Description,
            (MatchPercent ?? 0) / 100.0,
            notes,
            string.Empty,
            new AiUsage(candidate.Origin, 0, 0));
    }

    /// <summary>The rationale line the row and the saved tone both carry: the
    /// percentage (or the unrated reason), where it came from, and WHICH feature
    /// blocks it lost on — so the number is never shown alone.</summary>
    private static string Rationale(ToneMatchCandidate candidate, ILocalizationService loc, string breakdown)
    {
        var body = candidate.Score?.Percent is { } percent
            ? loc.Format("ToneMatch_Rationale", percent, candidate.Origin)
            : loc.Format("ToneMatch_RationaleUnrated",
                ToneDesignViewModel.UnratedReasonText(candidate.Score?.UnratedReason, loc), candidate.Origin);
        return breakdown.Length == 0 ? body : body + " " + loc.Format("ToneMatch_RationaleBlocks", breakdown);
    }

    public int Rank { get; }
    public string Title { get; }
    public string ChainSummary { get; }
    public double Distance { get; }

    /// <summary>The matcher's whole score object, for a consumer that wants the raw
    /// anchors. Null when the run produced no score.</summary>
    public MatchScore? Score { get; }

    /// <summary>0–100, or NULL for unrated. Never conflate null with 0.</summary>
    public int? MatchPercent { get; }

    /// <summary>True when <see cref="MatchPercent" /> is a number.</summary>
    public bool IsRated { get; }

    /// <summary>The <see cref="MatchUnrated" /> code, or null when rated.</summary>
    public string? UnratedReason { get; }

    /// <summary>The unrated reason in the app's language; empty when rated.</summary>
    public string UnratedText { get; }

    /// <summary>The pill text: "46 % match", or "Unrated — …".</summary>
    public string MatchText { get; }

    /// <summary>One line saying what 0 % and 100 % mean on this scale.</summary>
    public string ScaleText { get; }

    /// <summary>Where the passage's own variation lands on the scale, or null.</summary>
    public int? SelfDistancePercent { get; }

    /// <summary>True when this candidate is at or inside that variation.</summary>
    public bool WithinTargetNoise { get; }

    /// <summary>The caveat line for that case; empty otherwise.</summary>
    public string NoiseCaveatText { get; }

    /// <summary>The per-feature-block rows behind the headline, worst first.</summary>
    public IReadOnlyList<ToneMatchBlockViewModel> Breakdown { get; }

    /// <summary>The same breakdown as one line, for a row that has no room for a
    /// list.</summary>
    public string BreakdownText { get; }

    public string Origin { get; }
    public DspTonePreset Tone { get; }
    public ToneDesignResult AsDesignResult { get; }
}

/// <summary>
///     One feature block under a candidate's headline: what it is called, how much
///     of the remaining distance it is, and whether the candidate is still WORSE
///     there than an information-free amp-and-cab.
/// </summary>
public sealed class ToneMatchBlockViewModel
{
    public ToneMatchBlockViewModel(string block, int sharePercent, bool worseThanReference, ILocalizationService loc)
    {
        ArgumentNullException.ThrowIfNull(loc);
        Block = block;
        SharePercent = sharePercent;
        WorseThanReference = worseThanReference;
        // A missing key resolves to the KEY itself (LocalizationService.GetString),
        // so a block the resx has not named yet shows its raw id rather than
        // `ToneMatch_Block_foo` — a new feature block is a doc change, not a crash.
        var key = $"ToneMatch_Block_{block}";
        var named = loc[key];
        Label = string.Equals(named, key, StringComparison.Ordinal) ? block : named;
        Text = loc.Format("ToneMatch_BlockShare", Label, sharePercent);
    }

    /// <summary>The block's id, as <c>ToneFeatureLayout</c> names it.</summary>
    public string Block { get; }

    /// <summary>A player-facing name for it.</summary>
    public string Label { get; }

    /// <summary>How much of this candidate's remaining distance sits in this block.</summary>
    public int SharePercent { get; }

    /// <summary>True when the reference rig does BETTER here — the honest "the
    /// search did not get this part".</summary>
    public bool WorseThanReference { get; }

    /// <summary>The bound line: "Dynamics 41 %".</summary>
    public string Text { get; }
}
