using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Tone.Ai;
using DotCharter.Tone.Audio.Dsp.Presets;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.Infrastructure.Shell;

namespace DotCharter.Tone.UI.ViewModels.PlayMode;

/// <summary>
///     "Design a tone" — the AI panel at the TOP of the tone drawer
///     (<see cref="PresetBrowserViewModel" />). The user types what they want,
///     <see cref="IToneDesignService" /> answers with a validated
///     <see cref="DspTonePreset" />, and the four actions underneath reuse the
///     drawer's OWN seams rather than inventing a second way to touch the rig:
///     <b>Hear it</b> goes through the same apply path a preset card click takes,
///     <b>Save as tone</b> through the same <c>SaveUser</c> the save-as dialog
///     commits, <b>Tweak</b> sends a follow-up request carrying the conversation
///     so far, and <b>Discard</b> re-applies the snapshot taken before Hear it.
///
///     <para>
///         <b>The panel is COLLAPSED to its header until it is expanded.</b> The
///         drawer already pays a first-layout cost for a rail, a card grid and a
///         capture panel; a body that realized with it would add its own to every
///         first open, against the ≤150 ms switch budget. The view puts the body
///         in a <c>ContentControl</c>'s <c>ContentTemplate</c> so a collapsed
///         panel has no body subtree at all — not an invisible one — and
///         <c>Perf/ToneDesignUiPerfSmokeTests</c> asserts exactly that.
///     </para>
///     <para>
///         <b>Nothing here blocks the UI thread and there is no
///         <c>async void</c>.</b> The service call is awaited, progress arrives
///         on whatever thread the provider reports from and is marshalled back
///         through <c>post</c> (the UI dispatcher in the product, inline in
///         tests).
///     </para>
///     <para>
///         <b>When no provider is configured the panel is ONE line.</b>
///         <see cref="IsConfigured" /> is false, the body collapses to the setup
///         sentence plus <c>OpenSettingsTabCommand</c>, and no input, no
///         command and no result is offered — a capability the app cannot honour
///         must never look live.
///     </para>
///     <para>
///         <b>The "ChatGPT (copy &amp; paste)" route needs no provider</b>
///         <see cref="CopyPromptCommand" /> puts the exact prompt a
///         design would send on the clipboard; the player pastes the chat's whole
///         answer into <see cref="ReplyText" /> and <see cref="ImportReplyCommand" />
///         runs it through the service's own reply pipeline, landing in the same
///         result card and the same conversation — so a Tweak can go either way
///         next. It is reachable while <see cref="IsConfigured" /> is false (the
///         setup banner opens it), which is the whole point: a player with a chat
///         subscription and no API key. A failed import says so under the paste box
///         and changes nothing else.
///     </para>
///     <para>
///         <b>This is the CORE, shared by DotCharter and RigLab</b> (it lives in
///         DotCharter.Tone.UI). DotCharter's
///         <c>ToneDesignViewModel</c> derives from it and adds "Match from audio"
///         and "Match by section", which need the charting stack; the protected
///         members below are exactly what those two partials reach.
///     </para>
/// </summary>
public partial class ToneDesignCoreViewModel : ObservableObject
{
    /// <summary>Resolved lazily and per read, never cached: the service is
    /// registered by the composition root and a user can configure a provider
    /// while this view model is alive, which is precisely when
    /// <see cref="IToneDesignService.IsConfigured" /> flips.</summary>
    private readonly Func<IToneDesignService?> _service;
    private readonly IAppLogger? _logger;

    /// <summary>The drawer's apply seam — <c>PresetBrowserViewModel.ApplyPreset</c>,
    /// the same one a card click uses (queue, selected seat, board re-sync).</summary>
    private readonly Action<DspTonePreset> _apply;

    /// <summary>Wraps the LIVE chain as a USER-origin preset. User origin matters:
    /// the factory path loudness-normalizes, and a "restore what I was playing"
    /// that comes back at a different level has not restored it.</summary>
    private readonly Func<DspTonePreset> _snapshotLive;

    /// <summary>The drawer's save seam — <c>IDspTonePresetService.SaveUser</c>
    /// through the browser, so the tone lands in My Tones with
    /// <see cref="DspToneOrigin.User" /> and the library/dropdown rebuild.</summary>
    private readonly Func<string, DspToneCategory, string, DcRigPreset, DspTonePreset> _saveUser;

    /// <summary>Module type id → the catalogue's display name. Identity by default
    /// so a host with no chain service still prints something readable.</summary>
    private readonly Func<string, string> _moduleName;

    private readonly ILocalizationService _loc;

    /// <summary>Marshals a worker-thread report onto the UI thread. The product
    /// passes the dispatcher's post; tests omit it and it runs inline. Reached
    /// through <see cref="PostToUi" />, never directly, so a report that is ALREADY
    /// on the UI thread is applied in place instead of being queued.</summary>
    private readonly Action<Action> _post;

    /// <summary>The system clipboard, for "Copy prompt". Null in a host with none
    /// (tests): the copy step is then not offered.</summary>
    private readonly ISystemClipboard? _clipboard;

    /// <summary>The request the last COPIED prompt was built for, and whether it
    /// started a fresh conversation. An import answers THAT request — the chat saw
    /// it, whatever the box says now. Null until a prompt is copied; dropped by a
    /// successful import, a provider design and Discard.</summary>
    private (ToneDesignRequest Request, bool Fresh)? _manualPending;

    /// <summary>The conversation so far, in order, exactly as the contract's
    /// <see cref="ToneDesignRequest.History" /> wants it. A tweak sends it so
    /// "less gain" edits this chain instead of starting over.</summary>
    private readonly List<ToneDesignTurn> _history = [];

    private CancellationTokenSource? _cts;

    /// <summary>The service this view model is SUBSCRIBED to, so the handler is
    /// hooked exactly once however many times <see cref="EnsureSubscribed" /> is
    /// reached. Null until the composition root has one to resolve.</summary>
    private IToneDesignService? _subscribed;

    /// <summary>The last published configured state. CACHED on purpose: the
    /// service's own <c>IsConfigured</c> goes through
    /// <c>IConfigurationService.LoadAppConfig</c>, and this property is read by a
    /// XAML binding on every layout pass of the drawer. The cache is refreshed by
    /// <see cref="IToneDesignService.ConfiguredChanged" /> — subscribing is the
    /// whole reason it can be a cache rather than a settings load.</summary>
    private bool _configuredCache;

    /// <summary>What was playing before <see cref="HearItCommand" /> was first
    /// pressed. Null until then, which is what makes Discard a no-op rather than
    /// a surprise re-apply.</summary>
    private DspTonePreset? _restorePoint;

    /// <summary>Creates the panel's view model over the host's seams.</summary>
    /// <param name="service">The design service, resolved per read.</param>
    /// <param name="apply">The drawer's apply seam.</param>
    /// <param name="snapshotLive">Wraps the live chain as a user preset.</param>
    /// <param name="saveUser">The drawer's save seam.</param>
    /// <param name="localization">The host's strings (the rig hands its own down through
    /// <see cref="ToneDesignHostSeams.Localization" />).</param>
    /// <param name="moduleName">Module type id to display name.</param>
    /// <param name="post">Marshals onto the UI thread; inline by default.</param>
    /// <param name="logger">Optional app logger.</param>
    /// <param name="clipboard">The system clipboard, for the copy &amp; paste
    /// route's "Copy prompt"; null offers no copy.</param>
    public ToneDesignCoreViewModel(
        Func<IToneDesignService?> service,
        Action<DspTonePreset> apply,
        Func<DspTonePreset> snapshotLive,
        Func<string, DspToneCategory, string, DcRigPreset, DspTonePreset> saveUser,
        ILocalizationService localization,
        Func<string, string>? moduleName = null,
        Action<Action>? post = null,
        IAppLogger? logger = null,
        ISystemClipboard? clipboard = null)
    {
        _service = service;
        _logger = logger;
        _apply = apply;
        _snapshotLive = snapshotLive;
        _saveUser = saveUser;
        _moduleName = moduleName ?? (static id => id);
        _loc = localization;
        _post = post ?? (action => action());
        _clipboard = clipboard;
        EnsureSubscribed();
    }

    /// <summary>The strings, for a derived host's own blocks.</summary>
    protected ILocalizationService Loc => _loc;

    /// <summary>The app logger, when the host gave one.</summary>
    protected IAppLogger? Logger => _logger;

    /// <summary>Marshals onto the UI thread (inline in tests).</summary>
    protected Action<Action> Post => _post;

    /// <summary>The drawer's save seam — see the field.</summary>
    protected Func<string, DspToneCategory, string, DcRigPreset, DspTonePreset> SaveUserSeam => _saveUser;

    /// <summary>
    ///     Resolves the service once and hooks
    ///     <see cref="IToneDesignService.ConfiguredChanged" />. Idempotent, and
    ///     RE-tried while the service is still null: the composition root wires the
    ///     drawer before the AI slice in some hosts, and a build that ships without
    ///     the slice never gets one at all.
    /// </summary>
    private void EnsureSubscribed()
    {
        if (_subscribed is not null) return;
        var service = _service();
        if (service is null) return;

        _subscribed = service;
        service.ConfiguredChanged += OnServiceConfiguredChanged;
        _configuredCache = service.IsConfigured;
    }

    /// <summary>Raised on whatever thread saved the settings — the Settings page's
    /// Save is on the UI thread today, but the contract does not promise it, so
    /// this goes through the same <c>post</c> the progress reports use.</summary>
    private void OnServiceConfiguredChanged(object? sender, EventArgs e)
        => _post(() =>
        {
            _configuredCache = _subscribed?.IsConfigured ?? false;
            RaiseConfigured();
        });

    /// <summary>Unhooks the service. The drawer owns this view model and outlives
    /// most of the app, but the service is a SINGLETON — an unhooked handler here
    /// would root a whole drawer for the life of the process once a host starts
    /// rebuilding it.</summary>
    public void Detach()
    {
        var service = _subscribed;
        _subscribed = null;
        service?.ConfiguredChanged -= OnServiceConfiguredChanged;
    }

    /// <summary>Song title + artist of whatever the host is playing, folded into
    /// the request as context. Assigned by the host that KNOWS (Play Mode); the
    /// chart editor's rig leaves it null and the request simply carries none.</summary>
    public Func<(string? Title, string? Artist)>? SongContextProvider { get; set; }

    /// <summary>The tuning the rig's tuner is set to ("Drop D (D A D G B E)"), folded
    /// into every request so the design suits the strings the player has on. Set by
    /// the rig (<see cref="PresetBrowserViewModel" />); null carries none.</summary>
    public Func<string?>? TuningProvider { get; set; }

    /// <summary>Opens Settings ▸ AI. Assigned by whichever shell hosts the rig —
    /// the two do it differently and neither is reachable from here.</summary>
    public Action? OpenSettings { get; set; }

    // ---- panel state ----------------------------------------------------------

    /// <summary>Header-only until the user asks for it. See the class remarks:
    /// this is a LAYOUT decision, not a cosmetic one.</summary>
    [ObservableProperty] private bool _isExpanded;

    /// <summary>True when a provider is configured. False collapses the body to
    /// the one-line setup sentence.</summary>
    public bool IsConfigured
    {
        get
        {
            // Still a live read while nothing has been subscribed to — a build
            // whose service arrives late must not be stuck on a false cached
            // before the first ConfiguredChanged.
            if (_subscribed is null)
            {
                EnsureSubscribed();
                if (_subscribed is null) return _service() is { IsConfigured: true };
            }

            return _configuredCache;
        }
    }

    /// <summary>The inverse, for the setup line's own visibility — a binding
    /// cannot negate.</summary>
    public bool IsNotConfigured => !IsConfigured;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DesignCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyPromptCommand))]
    [NotifyPropertyChangedFor(nameof(NeedsPrompt))]
    [NotifyPropertyChangedFor(nameof(ShowPromptHint))]
    private string _description = string.Empty;

    /// <summary>
    ///     True while Design cannot run because there is nothing to design FROM.
    ///     <para>
    ///         It exists so the button can say WHY it is disabled instead of just
    ///         being grey (W4): "a disabled control states its reason or is not
    ///         rendered", and Design is the block's own verb — hiding it would
    ///         hide the feature, which is the other half of that rule and the
    ///         wrong half here. It is deliberately NOT "CanDesign inverted": a
    ///         run in flight also disables the button, and "type a prompt" is a
    ///         lie while the model is answering the prompt you typed.
    ///     </para>
    /// </summary>
    public bool NeedsPrompt => !IsRunning && string.IsNullOrWhiteSpace(Description);

    /// <summary>The composer's "describe it first" line: <see cref="NeedsPrompt" />
    /// while the box IS the prompt and the status line has nothing to say (the two
    /// share one slot beside the button). After an answer the box is the
    /// refinement and Design is not the button on screen, so the line would
    /// explain nothing.</summary>
    public bool ShowPromptHint => NeedsPrompt && !HasResult && !HasStatus;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TweakCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyPromptCommand))]
    private string _tweakText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DesignCommand))]
    [NotifyCanExecuteChangedFor(nameof(TweakCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyPromptCommand))]
    [NotifyCanExecuteChangedFor(nameof(ImportReplyCommand))]
    [NotifyPropertyChangedFor(nameof(NeedsPrompt))]
    [NotifyPropertyChangedFor(nameof(ShowPromptHint))]
    [NotifyPropertyChangedFor(nameof(ShowResult))]
    [NotifyPropertyChangedFor(nameof(ShowIntro))]
    private bool _isRunning;

    /// <summary>The service's own <c>IProgress&lt;string&gt;</c> line
    /// ("Asking Claude…", "Checking 7 modules…"), plus this view model's own
    /// outcomes (saved, restored, cancelled).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    [NotifyPropertyChangedFor(nameof(ShowPromptHint))]
    private string _status = string.Empty;

    public bool HasStatus => Status.Length > 0;

    /// <summary>The user-facing failure, chosen from
    /// <see cref="ToneDesignException.Fault" />. The provider's own words go to
    /// the log, never here.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    [NotifyPropertyChangedFor(nameof(ShowIntro))]
    private string _errorText = string.Empty;

    public bool HasError => ErrorText.Length > 0;

    /// <summary>True for the two faults a user can actually fix from Settings.
    /// Every other fault gets the message alone — an action that cannot help is
    /// worse than none.</summary>
    [ObservableProperty] private bool _errorOffersSettings;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    [NotifyPropertyChangedFor(nameof(ShowResult))]
    [NotifyPropertyChangedFor(nameof(ShowPromptHint))]
    [NotifyPropertyChangedFor(nameof(ShowIntro))]
    [NotifyPropertyChangedFor(nameof(ShowDesignButton))]
    [NotifyPropertyChangedFor(nameof(ShowTweakButton))]
    [NotifyCanExecuteChangedFor(nameof(CopyPromptCommand))]
    [NotifyCanExecuteChangedFor(nameof(HearItCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveToneCommand))]
    [NotifyCanExecuteChangedFor(nameof(TweakCommand))]
    [NotifyCanExecuteChangedFor(nameof(DiscardCommand))]
    private ToneDesignResultViewModel? _result;

    public bool HasResult => Result != null;

    /// <summary>
    ///     The answer card is drawn while there is a result and no request is in
    ///     flight. During a tweak the conversation reads "you asked … / the model
    ///     is answering", and the previous answer's card under the new question
    ///     would put an answer BELOW the question it did not answer.
    /// </summary>
    public bool ShowResult => HasResult && !IsRunning;

    /// <summary>
    ///     The empty conversation: nothing asked, nothing answered, nothing
    ///     running, nothing failed. The panel then says what to ask for instead
    ///     of showing an empty column.
    /// </summary>
    public bool ShowIntro => Turns.Count == 0 && !HasResult && !IsRunning && !HasError;

    /// <summary>Forwards the generated <c>OnResultChanged</c> hook to a derived
    /// host: a partial method cannot be implemented from another assembly, and
    /// DotCharter's matcher drops its candidate list when the result goes.</summary>
    partial void OnResultChanged(ToneDesignResultViewModel? value) => OnResultChangedCore(value);

    /// <summary>Called after <see cref="Result" /> changed. Does nothing here.</summary>
    /// <param name="value">The new result, or null.</param>
    protected virtual void OnResultChangedCore(ToneDesignResultViewModel? value)
    {
    }

    /// <summary>The conversation as rows, so a tweak's history is visible rather
    /// than implied. Mirrors <see cref="_history" /> one-for-one.</summary>
    public ObservableCollection<ToneDesignTurnViewModel> Turns { get; } = [];

    // ---- commands -------------------------------------------------------------

    /// <summary>Opening the panel does not re-read anything: the service raises
    /// <see cref="IToneDesignService.ConfiguredChanged" /> when a provider is
    /// configured in Settings, and this only picks up a service that was not
    /// resolvable when the panel was built. The header is a two-way
    /// <c>ToggleButton</c> — that IS the gesture, and a command beside it would be
    /// a second door onto one boolean.</summary>
    partial void OnIsExpandedChanged(bool value)
    {
        // Nothing to re-read: ConfiguredChanged keeps the cache current. This used
        // to load the whole AppConfig on the way open, on the UI thread, inside the
        // ≤150 ms switch budget.
        EnsureSubscribed();
    }

    /// <summary>Re-publishes the configured state. Public because the settings
    /// tab and the host both change it from outside this view model.</summary>
    public void RaiseConfigured()
    {
        OnPropertyChanged(nameof(IsConfigured));
        OnPropertyChanged(nameof(IsNotConfigured));
        OnPropertyChanged(nameof(ShowBody));
        OnPropertyChanged(nameof(ShowSetupBanner));
        OnPropertyChanged(nameof(ShowDesignButton));
        OnPropertyChanged(nameof(ShowTweakButton));
        DesignCommand.NotifyCanExecuteChanged();
        TweakCommand.NotifyCanExecuteChanged();
    }

    private bool CanDesign => !IsRunning && IsConfigured && Description.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanDesign))]
    private Task DesignAsync() => RunAsync(Description.Trim(), fresh: true);

    private bool CanTweak => !IsRunning && IsConfigured && HasResult && TweakText.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanTweak))]
    private Task TweakAsync() => RunAsync(TweakText.Trim(), fresh: false);

    private bool CanCancel => IsRunning;

    /// <summary>Cancels the in-flight request. The service contract promises
    /// <see cref="OperationCanceledException" /> rather than a fault for this, so
    /// it is reported as "Cancelled" and not as a failure.</summary>
    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        var cts = _cts;
        if (cts == null) return;
        try { cts.Cancel(); }
        catch (ObjectDisposedException) { /* the run's finally already disposed it */ }
    }

    private async Task RunAsync(string text, bool fresh)
    {
        var service = _service();
        if (service is null || !service.IsConfigured)
        {
            Fail(ToneDesignFault.NotConfigured);
            return;
        }

        if (fresh)
        {
            ClearConversation();
            Result = null;
        }

        // A provider answer supersedes a prompt copied for a chat.
        _manualPending = null;
        AddTurn(ToneDesignRole.User, text);
        var request = BuildRequest(text, fresh, _history);

        ErrorText = string.Empty;
        ErrorOffersSettings = false;
        Status = _loc["ToneDesign_Working"];

        var cts = _cts = new CancellationTokenSource();
        IsRunning = true;
        try
        {
            // NOT Progress<string>. That type captures a SynchronizationContext
            // and POSTS — on the UI thread it defers the line to a later dispatcher
            // pass, and with no context at all it hands it to the thread pool. Both
            // mean "Asking Claude…" is on screen at some point AFTER the provider
            // said so, and in a test it is after the assertion: the status guard
            // failed on a loaded box for exactly that reason. This one applies the
            // line the moment Report returns.
            var progress = new InlineProgress(line => PostToUi(() => Status = line));
            var result = await service.DesignAsync(request, progress, cts.Token).ConfigureAwait(true);

            Result = new ToneDesignResultViewModel(result, ChainSummary(result.Tone.Rig), _loc);
            AddTurn(ToneDesignRole.Assistant, result.Tone.Name);
            TweakText = string.Empty;
            // "12.4 s · ~$0.04" — what the design just cost, in the line that was
            // carrying "Asking Claude…" a moment ago. Empty for a result that
            // measured nothing (a test double), never "0.0 s".
            Status = Result.MetaText;
        }
        catch (ToneDesignException ex)
        {
            Fail(ex.Fault);
        }
        catch (OperationCanceledException)
        {
            Status = _loc["ToneDesign_Cancelled"];
        }
        finally
        {
            IsRunning = false;
            if (ReferenceEquals(_cts, cts)) _cts = null;
            cts.Dispose();
        }
    }

    /// <summary>
    ///     The request for <paramref name="text" />, exactly as a design sends it:
    ///     <paramref name="history" /> ending on the new request (a COPY — the
    ///     service may hold it past an await, and the next turn appends to the live
    ///     list), the current tone for a refinement, the song and the tuning. The
    ///     copy &amp; paste route builds its request here too, so the prompt it copies
    ///     is the prompt a provider would have been sent.
    /// </summary>
    private ToneDesignRequest BuildRequest(string text, bool fresh, IEnumerable<ToneDesignTurn> history)
    {
        var (title, artist) = SongContextProvider?.Invoke() ?? (null, null);
        return new ToneDesignRequest(
            text,
            history.ToList(),
            fresh ? null : Result?.Tone,
            string.IsNullOrWhiteSpace(title) ? null : title,
            string.IsNullOrWhiteSpace(artist) ? null : artist,
            TuningProvider?.Invoke());
    }

    /// <summary>Runs <paramref name="action" /> on the UI thread, IN PLACE when it
    /// is already there: a dispatcher post from the UI thread is a
    /// deferral nobody asked for, and status text that lands a frame late reads as
    /// a hang.</summary>
    private void PostToUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }

        _post(action);
    }

    /// <summary>An <see cref="IProgress{T}" /> that calls its handler SYNCHRONOUSLY
    /// on the reporting thread. <see cref="Progress{T}" /> cannot be used here — see
    /// the call site.</summary>
    protected sealed class InlineProgress(Action<string> handler) : IProgress<string>
    {
        public void Report(string value) => handler(value);
    }

    private void AddTurn(ToneDesignRole role, string text)
    {
        _history.Add(new ToneDesignTurn(role, text));
        // Only the LAST turn is "current": an answer that is the last turn is the
        // one the result card draws, so its own row steps aside for the card.
        if (Turns.Count > 0) Turns[^1].IsLast = false;
        Turns.Add(new ToneDesignTurnViewModel(role, text) { IsLast = true });
        OnPropertyChanged(nameof(ShowIntro));
    }

    /// <summary>Empties the conversation (a fresh Design, Discard).</summary>
    private void ClearConversation()
    {
        _history.Clear();
        Turns.Clear();
        OnPropertyChanged(nameof(ShowIntro));
    }

    private void Fail(ToneDesignFault fault)
    {
        ErrorText = _loc[FaultKey(fault)];
        ErrorOffersSettings =
            fault is ToneDesignFault.NotConfigured or ToneDesignFault.Authentication;
        Status = string.Empty;
    }

    /// <summary>One key per fault. A switch rather than string concatenation so
    /// <c>LocalizationSweepTests</c>' code-lookup half can see every key.</summary>
    private static string FaultKey(ToneDesignFault fault) => fault switch
    {
        ToneDesignFault.NotConfigured => "ToneDesign_Fault_NotConfigured",
        ToneDesignFault.Network => "ToneDesign_Fault_Network",
        ToneDesignFault.Authentication => "ToneDesign_Fault_Authentication",
        ToneDesignFault.RateLimited => "ToneDesign_Fault_RateLimited",
        ToneDesignFault.ModelRefused => "ToneDesign_Fault_ModelRefused",
        ToneDesignFault.InvalidOutput => "ToneDesign_Fault_InvalidOutput",
        _ => "ToneDesign_Fault_Unknown"
    };

    // ---- the "ChatGPT (copy & paste)" route ------------------------------------

    /// <summary>The copy &amp; paste block is open. The composer's "ChatGPT" toggle
    /// owns it while a provider is configured; without one the setup banner's
    /// "Use ChatGPT" opens it, and the same toggle closes it again.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBody))]
    [NotifyPropertyChangedFor(nameof(ShowSetupBanner))]
    private bool _isManualOpen;

    /// <summary>The conversation and the composer are on screen: a provider is
    /// configured, or the player opened the copy &amp; paste route without one.</summary>
    public bool ShowBody => IsConfigured || IsManualOpen;

    /// <summary>The not-configured banner: no provider, and the copy &amp; paste
    /// route not opened from it either.</summary>
    public bool ShowSetupBanner => !IsConfigured && !IsManualOpen;

    /// <summary>Design is the composer's verb only while a provider can answer it.
    /// Without one the button is not drawn at all — a capability the app cannot
    /// honour must never look live — and the copy &amp; paste block is the verb.</summary>
    public bool ShowDesignButton => IsConfigured && !HasResult;

    /// <summary>Tweak, likewise.</summary>
    public bool ShowTweakButton => IsConfigured && HasResult;

    /// <summary>The route exists: there is a design service to build a prompt and
    /// read an answer (every product host has one). "Use ChatGPT" is not offered
    /// without it.</summary>
    public bool IsManualAvailable => (_subscribed ?? _service()) is not null;

    /// <summary>The chat's whole answer, pasted — prose around a fenced block is
    /// fine.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportReplyCommand))]
    private string _replyText = string.Empty;

    /// <summary>Why the last copy or import failed, shown under the paste box — the
    /// route's own line, so a bad paste never touches the conversation's error or
    /// its answer.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasManualError))]
    private string _manualErrorText = string.Empty;

    /// <summary>True when <see cref="ManualErrorText" /> has a line.</summary>
    public bool HasManualError => ManualErrorText.Length > 0;

    /// <summary>A prompt is being built for the clipboard. Its own flag rather than
    /// <see cref="IsRunning" />: a copy asks nobody, so it must not put the
    /// answer card aside or offer Cancel.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyPromptCommand))]
    private bool _isCopying;

    /// <summary>Opens the copy &amp; paste block — the setup banner's door.</summary>
    [RelayCommand]
    private void OpenManual() => IsManualOpen = true;

    /// <summary>What the composer's box holds: the prompt before an answer, the
    /// refinement after — the same box a Design or a Tweak would send.</summary>
    private string ActiveText => (HasResult ? TweakText : Description).Trim();

    private bool CanCopyPrompt =>
        !IsRunning && !IsCopying && _clipboard is not null && ActiveText.Length > 0;

    /// <summary>
    ///     Step 1: builds the prompt a design would send for what the box says —
    ///     a fresh design before an answer, a refinement of the current tone after
    ///     one — and puts it on the clipboard. The conversation does not change: a
    ///     copied prompt is a question nobody has answered yet.
    ///     <para>
    ///         The prompt is built on the thread pool (the capture library is read
    ///         from disk); only the clipboard write comes back to the UI thread.
    ///     </para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCopyPrompt))]
    private async Task CopyPromptAsync()
    {
        var service = _service();
        var clipboard = _clipboard;
        if (service is null || clipboard is null) return;

        var fresh = !HasResult;
        var text = ActiveText;
        var request = BuildRequest(text, fresh,
            (fresh ? [] : _history).Append(new ToneDesignTurn(ToneDesignRole.User, text)));

        ManualErrorText = string.Empty;
        IsCopying = true;
        try
        {
            var manual = await Task.Run(() => service.BuildManualPromptAsync(request, CancellationToken.None))
                .ConfigureAwait(true);
            await clipboard.SetTextAsync(manual.Text).ConfigureAwait(true);
            _manualPending = (request, fresh);
            Status = _loc.Format("ToneDesign_Manual_Copied",
                manual.EstimatedTokens.ToString("N0", CultureInfo.CurrentCulture));
        }
        catch (ToneDesignException ex)
        {
            _logger?.Warning(AppLogCategory.Application, $"Copying the tone prompt failed: {ex.Message} {ex.Detail}");
            ManualErrorText = _loc[FaultKey(ex.Fault)];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The clipboard is the OS's: another app can hold it open.
            _logger?.Warning(AppLogCategory.Application, $"Copying the tone prompt failed: {ex.GetType().Name}: {ex.Message}");
            ManualErrorText = _loc["ToneDesign_Manual_CopyFailed"];
        }
        finally
        {
            IsCopying = false;
        }
    }

    private bool CanImportReply => !IsRunning && ReplyText.Trim().Length > 0;

    /// <summary>
    ///     Step 2: reads the pasted answer through the service's reply pipeline and,
    ///     when it is a usable tone, lands it exactly where a provider's answer
    ///     lands — the result card, and a "You" turn plus an answer in the
    ///     conversation — so the next Tweak can use a provider or another round of
    ///     copy &amp; paste. It answers the request the COPIED prompt was built for;
    ///     with nothing copied, whatever the box says now.
    ///     <para>
    ///         <b>A failed import changes nothing else.</b> The conversation, the
    ///         answer card and its error line are only written on success; a bad
    ///         paste gets its own localized line under the paste box.
    ///     </para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanImportReply))]
    private async Task ImportReplyAsync()
    {
        var service = _service();
        if (service is null) return;

        var reply = ReplyText;
        var (request, fresh) = _manualPending ?? PendingFromTheBox();

        ManualErrorText = string.Empty;
        Status = _loc["ToneDesign_Working"];
        var cts = _cts = new CancellationTokenSource();
        IsRunning = true;
        try
        {
            var progress = new InlineProgress(line => PostToUi(() => Status = line));
            var result = await Task.Run(() => service.ImportReplyAsync(request, reply, progress, cts.Token))
                .ConfigureAwait(true);

            if (fresh) ClearConversation();
            AddTurn(ToneDesignRole.User, request.Description);
            Result = new ToneDesignResultViewModel(result, ChainSummary(result.Tone.Rig), _loc);
            AddTurn(ToneDesignRole.Assistant, result.Tone.Name);
            _manualPending = null;
            ReplyText = string.Empty;
            TweakText = string.Empty;
            ErrorText = string.Empty;
            ErrorOffersSettings = false;
            Status = Result.MetaText;
        }
        catch (ToneDesignException ex)
        {
            _logger?.Info(AppLogCategory.Application, $"A pasted tone answer was not usable: {ex.Message} {ex.Detail}");
            ManualErrorText = ex.Fault == ToneDesignFault.InvalidOutput
                ? _loc["ToneDesign_Manual_InvalidReply"]
                : _loc[FaultKey(ex.Fault)];
            Status = string.Empty;
        }
        catch (OperationCanceledException)
        {
            Status = _loc["ToneDesign_Cancelled"];
        }
        finally
        {
            IsRunning = false;
            if (ReferenceEquals(_cts, cts)) _cts = null;
            cts.Dispose();
        }
    }

    /// <summary>The request an import answers when no prompt was copied from here
    /// (the player asked the chat on their own): the box's text, or the route's
    /// name when the box is empty, so the conversation still has a "You" turn.</summary>
    private (ToneDesignRequest Request, bool Fresh) PendingFromTheBox()
    {
        var fresh = !HasResult;
        var text = ActiveText.Length > 0 ? ActiveText : _loc["ToneDesign_Manual_Title"];
        return (BuildRequest(text, fresh,
            (fresh ? [] : _history).Append(new ToneDesignTurn(ToneDesignRole.User, text))), fresh);
    }

    /// <summary>"Rat → Power Amp → Cab → Mic Position" — the ordered chain in the
    /// catalogue's own words.</summary>
    protected string ChainSummary(DcRigPreset rig) =>
        string.Join(" → ", rig.Modules.Select(m => _moduleName(m.ModuleTypeId)));

    // ---- the four result actions ---------------------------------------------

    /// <summary>Applies the designed tone through the drawer's own apply path,
    /// remembering what was playing first so <see cref="DiscardCommand" /> can put
    /// it back. The snapshot is taken ONCE: hearing two designs in a row must
    /// still return to the tone the player arrived with.</summary>
    [RelayCommand(CanExecute = nameof(HasResult))]
    private void HearIt()
    {
        if (Result is not { } result) return;
        _restorePoint ??= _snapshotLive();
        _apply(result.Tone);
        Status = _loc.Format("ToneDesign_Hearing", result.ToneName);
    }

    /// <summary>Saves the designed tone into the player's library through the
    /// ordinary user-preset path, so it appears in My Tones with
    /// <see cref="DspToneOrigin.User" />.</summary>
    [RelayCommand(CanExecute = nameof(HasResult))]
    private void SaveTone()
    {
        if (Result is not { } result) return;
        var saved = _saveUser(result.ToneName, result.Tone.Category, result.Rationale, result.Tone.Rig);
        Status = _loc.Format("ToneDesign_Saved", saved.Name);
    }

    /// <summary>Puts back the tone that was playing before the first
    /// <see cref="HearItCommand" />, and drops the design. With nothing heard it
    /// only clears the panel — there is nothing to restore.</summary>
    [RelayCommand(CanExecute = nameof(HasResult))]
    private void Discard()
    {
        var restore = _restorePoint;
        _restorePoint = null;
        _manualPending = null;
        Result = null;
        ClearConversation();
        TweakText = string.Empty;

        if (restore is null)
        {
            Status = string.Empty;
            return;
        }

        _apply(restore);
        Status = _loc["ToneDesign_Restored"];
    }

    [RelayCommand]
    private void OpenSettingsTab() => OpenSettings?.Invoke();

    /// <summary>
    ///     Test seam: publishes a finished design without a provider. The
    ///     headless smoke tests need the RESULT block realized — the confidence
    ///     pill, the warnings and the four actions are the markup most likely to
    ///     carry a silent resource fault, and a block nothing ever shows is
    ///     never laid out.
    /// </summary>
    internal void SetResultForTests(ToneDesignResult result, string chainSummary) =>
        Result = new ToneDesignResultViewModel(result, chainSummary, _loc);
}

/// <summary>One finished design, shaped for the panel. Everything on it is
/// derived once — a template that recomputed a chain summary per binding pass
/// would do it on the UI thread.</summary>
public sealed partial class ToneDesignResultViewModel : ObservableObject
{
    public ToneDesignResultViewModel(ToneDesignResult result, string chainSummary, ILocalizationService loc)
    {
        Tone = result.Tone;
        ToneName = result.Tone.Name;
        ChainSummary = chainSummary;
        ChainSteps = ToneChainStep.From(result.Tone.Rig, chainSummary);
        Rationale = result.Rationale;
        // The contract's confidence is 0..1; the panel prints whole percent.
        ConfidencePercent = (int)Math.Round(Math.Clamp(result.Confidence, 0, 1) * 100);
        ConfidenceText = loc.Format("ToneDesign_ConfidenceValue", ConfidencePercent);
        Warnings = result.Warnings;
        // "12.4 s · ~$0.04", or "12.4 s · local" for a model with no list price —
        // which is the honest answer for LM Studio and Ollama, where the marginal
        // cost of a request really is nothing. A result that measured no time (a
        // test double, an offline replay) prints neither half rather than "0.0 s".
        ElapsedText = result.Elapsed > TimeSpan.Zero
            ? loc.Format("ToneDesign_ElapsedSeconds",
                result.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture))
            : string.Empty;
        var usd = AiModelPricing.FormatUsd(result.Usage);
        CostText = usd is null
            ? loc["ToneDesign_CostLocal"]
            : loc.Format("ToneDesign_CostEstimate", usd);
        MetaText = ElapsedText.Length > 0
            ? loc.Format("ToneDesign_ResultMeta", ElapsedText, CostText)
            : string.Empty;
        // A pasted chat answer: this app asked nobody, metered nothing and priced
        // nothing — "local" would be a lie and a time would be the paste's, not the
        // model's. It says where the answer came from instead.
        if (result.Usage.IsManual)
        {
            ElapsedText = string.Empty;
            CostText = string.Empty;
            MetaText = loc["ToneDesign_Manual_Meta"];
        }
        // One wrapping block rather than an items panel: the list is short, and
        // an ItemsControl of bare strings would need its own DataTemplate for
        // nothing. `Environment.NewLine` so it still reads as a list.
        WarningsText = string.Join(Environment.NewLine, result.Warnings);
        DownloadedCapturesText = string.Join(Environment.NewLine,
            (result.DownloadedCaptures ?? []).Select(c =>
                loc.Format("ToneDesign_DownloadedCapture", c.Title, c.Author, c.License)));
    }

    public DspTonePreset Tone { get; }
    public string ToneName { get; }
    public string ChainSummary { get; }

    /// <summary>The chain as the answer card draws it: one chip per unit, in
    /// order, named in the catalogue's words and tinted by the unit's family.</summary>
    public IReadOnlyList<ToneChainStep> ChainSteps { get; }
    public string Rationale { get; }
    public int ConfidencePercent { get; }
    public string ConfidenceText { get; }
    public IReadOnlyList<string> Warnings { get; }
    public string WarningsText { get; }

    /// <summary>"12.4 s", or empty when the result carries no measured time.</summary>
    public string ElapsedText { get; }

    /// <summary>"~$0.04", or the word for "local" when the model id is not in
    /// <see cref="AiModelPricing" />. Never "$0.00" for an unpriced model: a
    /// measured zero and an unknown price are different answers.</summary>
    public string CostText { get; }

    /// <summary>Both of the above joined for the panel's status line, or empty when
    /// there is no time to report.</summary>
    public string MetaText { get; }

    /// <summary>True when <see cref="MetaText" /> has something to show.</summary>
    public bool HasMeta => MetaText.Length > 0;
    public bool HasWarnings => Warnings.Count > 0;

    /// <summary>"From TONE3000: Twin Clean by someone (CC BY 4.0)" — one line per
    /// capture the design downloaded, crediting its author and licence the way the
    /// capture browser does. Empty when none were.</summary>
    public string DownloadedCapturesText { get; }

    /// <summary>True when <see cref="DownloadedCapturesText" /> has a line.</summary>
    public bool HasDownloadedCaptures => DownloadedCapturesText.Length > 0;
    /// <summary>True when the model explained its chain. The explanation is
    /// shown IN the answer card rather than behind a toggle: it is half of the
    /// answer, and with the composer pinned under the conversation a paragraph no
    /// longer pushes the card's actions out of reach.</summary>
    public bool HasRationale => Rationale.Length > 0;
}

/// <summary>One unit of a designed chain, as the answer card's chip draws it.</summary>
/// <param name="Name">The catalogue's display name ("Legacy Amp").</param>
/// <param name="TokenKey">The resource key of the unit family's Fire colour.</param>
public sealed record ToneChainStep(string Name, string TokenKey)
{
    /// <summary>
    ///     The steps of <paramref name="rig" />, named from
    ///     <paramref name="chainSummary" /> — the same names the summary was
    ///     joined from (<c>ChainSummary</c> joins them with " → ", in rig order).
    ///     A summary that does not split into one name per unit (a host that
    ///     passed its own words) falls back to the type ids rather than to a
    ///     wrong pairing.
    /// </summary>
    public static IReadOnlyList<ToneChainStep> From(DcRigPreset rig, string chainSummary)
    {
        var ids = rig.Modules.Select(m => m.ModuleTypeId).ToList();
        var names = chainSummary.Split(" → ");
        var named = names.Length == ids.Count;
        return
        [
            .. ids.Select((id, i) => new ToneChainStep(
                named && names[i].Length > 0 ? names[i] : id,
                Controls.Fire.FireUnitState.TokenKeyFor(Controls.Fire.FireRigCategory.ForModule(id))))
        ];
    }
}

/// <summary>One conversation row. <see cref="IsUser" /> is the template's switch
/// — a binding cannot compare an enum.</summary>
public sealed partial class ToneDesignTurnViewModel : ObservableObject
{
    public ToneDesignTurnViewModel(ToneDesignRole role, string text)
    {
        IsUser = role == ToneDesignRole.User;
        Text = text;
    }

    public bool IsUser { get; }
    public string Text { get; }

    /// <summary>True for the newest turn of the conversation.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEarlierAnswer))]
    private bool _isLast;

    /// <summary>
    ///     An answer the conversation has moved past — drawn as a one-line row
    ///     ("→ Texas Flood Crunch"). The NEWEST answer has no row of its own: the
    ///     result card under the conversation IS that answer, and printing its
    ///     name twice read as two answers.
    /// </summary>
    public bool IsEarlierAnswer => !IsUser && !IsLast;
}
