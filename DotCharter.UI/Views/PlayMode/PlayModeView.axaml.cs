using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.UI.ViewModels.PlayMode;
using DotCharter.UI.Views.Shell;
using DotCharter.UI.Platform;

namespace DotCharter.UI.Views.PlayMode;

public partial class PlayModeView : UserControl, IPrewarmableSurfaces, IPrewarmConstruction
{
    /// <summary>
    ///     Every Play Mode screen and overlay the user is not currently on. All
    ///     ~19 of them are siblings in one root <c>Panel</c>, gated by
    ///     <c>IsVisible</c>, so each pays its first measure on the first
    ///     navigation into it (1-41 ms each, ~234 ms across the set) — which is
    ///     precisely the hitch between the menu and Song Select, or between a run
    ///     and its Results. The shell's prewarm takes them to ~0.2 ms.
    /// </summary>
    public IReadOnlyList<Control> HiddenSurfaces()
    {
        // The deferred screens' bodies do not EXIST yet — they are
        // ContentTemplates, and a hidden host never runs the layout pass that
        // would build one. Nothing can be listed (or found by name) until they
        // are, so this materializes them first: the same move
        // DialogContainerView.HiddenSurfaces() makes when it builds 34 dialog
        // views merely to list them.
        BuildDeferredScreenBodies();

        // The rig overlay's screen is GATED off the boot path (DeferredRigScreen):
        // its DataContext binding answers null until something wants it. Want it
        // here — this runs at Background priority after the first frame — so the
        // overlay below is prewarmed over a real screen, not over null.
        (DataContext as PlayModeViewModel)?.ToneRig.EnsureRigScreen();
        // …and the rig VIEW is a ContentTemplate body too: built here, after
        // the screen exists, so the tone drawer's names below resolve.
        HiddenSubtreePrewarmer.BuildDeferredBody(this.FindControl<ContentControl>(RigScreenHostName));

        var surfaces = new List<Control>(HiddenSubtreePrewarmer.HiddenChildrenOf(Content as Panel));
        foreach (var name in GameplaySettingsPaneNames)
        {
            if (FindByName(name) is { } pane)
                surfaces.Add(pane);
        }

        foreach (var name in LaunchPanelNames)
        {
            if (FindByName(name) is { } panel)
                surfaces.Add(panel);
        }

        surfaces.AddRange(HiddenSubtreePrewarmer.HiddenNamedDescendants(this, NestedScreenBodyNames));
        return surfaces;
    }

    /// <summary>
    ///     The root-level screens whose markup is declared as a
    ///     <c>ContentTemplate</c> under a <c>ContentControl</c> host of this name
    ///     rather than inline — none of them visible at boot.
    ///     <para>
    ///         <b>Why.</b> <c>MainWindow</c>'s <c>DataContextChanged</c> fills
    ///         <c>PlayModeHost</c> SYNCHRONOUSLY, before <c>Window.Show()</c>, so
    ///         every element this view declares is constructed in front of the
    ///         first frame: measured as the <c>bind=</c> phase of the
    ///         <c>StartupPhases:</c> line, and A/B'd against a boot into the chart
    ///         editor (which skips the fill entirely) at 6,039 ms versus 666 ms.
    ///         A <c>DataTemplate</c> body is deferred content: it is built when a
    ///         layout pass reaches the presenter, and a hidden host never runs
    ///         one — so the construction moves to whichever comes first, the
    ///         prewarm drain after the first frame or the user's own navigation.
    ///     </para>
    ///     <para>
    /// <b>Song Select and Results are here too.</b> This view drives
    ///         both — Song Select's search keys and the Results reveal — and a
    ///         template body generates no fields, so it reaches them through
    ///         <see cref="SongSelect" /> and <see cref="Results" />: a lookup that
    ///         builds the body when it is first wanted. Song Select is Home's
    ///         first destination, so the shell's drain builds and realizes it
    ///         right behind Home (<c>MainWindowPrewarm.HomeAndItsDestinations</c>).
    ///     </para>
    /// </summary>
    internal static readonly string[] DeferredScreenHostNames =
    [
        "GameplaySettingsOverlayHost", "TuningCheckScreenRootHost", "PauseScreenRootHost",
        SongSelectHostName, ResultsHostName
    ];

    /// <summary>The host of Song Select's deferred body.</summary>
    internal const string SongSelectHostName = "SongSelectScreenRootHost";

    /// <summary>The host of the Results screen's deferred body.</summary>
    internal const string ResultsHostName = "ResultsScreenRootHost";

    /// <summary>
    ///     The host of the Amp &amp; FX rig VIEW inside the rig overlay — a
    ///     <c>ContentTemplate</c> over <c>ToneRig.AmpFxRig</c>, so the ~450-element
    /// rig is not built in the <c>bind=</c> phase. It is not in
    ///     <see cref="DeferredScreenHostNames" /> because its gate stays on
    ///     <c>RigScreenRoot</c> (which also holds the song-tone bar), and because its
    ///     content is the rig SCREEN, which stays <c>null</c> until
    ///     <c>EnsureRigScreen()</c> — a null content builds nothing at all.
    /// </summary>
    internal const string RigScreenHostName = "RigScreenHost";

    private readonly HashSet<string> _deferredScreenBodiesBuilt = [];

    /// <summary>
    ///     What <see cref="HiddenSurfaces" /> builds before it can list anything, one
    /// drain turn each: each deferred screen body, then the rig screen and
    ///     view. Together they were most of a 0.7-1.9 s listing turn.
    /// </summary>
    public IEnumerable<Action> ConstructionSteps()
    {
        foreach (var name in DeferredScreenHostNames)
            yield return () => BuildDeferredScreenBody(name);

        yield return () =>
        {
            (DataContext as PlayModeViewModel)?.ToneRig.EnsureRigScreen();
            HiddenSubtreePrewarmer.BuildDeferredBody(this.FindControl<ContentControl>(RigScreenHostName));
        };
    }

    /// <summary>
    ///     Builds the deferred bodies WITHOUT laying them out: the host's own
    ///     template plus one <c>ContentPresenter.UpdateChild()</c>, which is the
    ///     construction half only. The expensive half — style application,
    ///     measure and arrange — stays with the prewarm drain, which pays one
    ///     surface per <c>Background</c> turn.
    ///     <para>
    ///         Never writes <c>IsVisible</c>: the hosts' gates are owned by
    ///         bindings, and this view holds the surfaces that grab focus.
    ///         Idempotent and throw-free — a body that cannot be built just stays
    ///         cold and pays on the first navigation, exactly as it does today.
    ///     </para>
    /// </summary>
    private void BuildDeferredScreenBodies()
    {
        foreach (var name in DeferredScreenHostNames)
            BuildDeferredScreenBody(name);
    }

    /// <summary>
    ///     Builds one deferred body (see <see cref="BuildDeferredScreenBodies" />).
    ///     A host that has no template yet — the view is not in a styled tree —
    ///     is NOT marked built, so the prewarm or the next lookup tries again
    ///     instead of leaving the screen cold for good.
    /// </summary>
    internal void BuildDeferredScreenBody(string name)
    {
        if (this.FindControl<ContentControl>(name) is not { } host) return;
        if (!_deferredScreenBodiesBuilt.Add(name)) return;
        try
        {
            host.ApplyTemplate();
            host.Presenter?.UpdateChild();
            // The body is a lifted screen VIEW: its own template is what
            // puts the screen's markup in the visual tree, where an inline body
            // put it — construction still, no style pass, no layout.
            if (host.Presenter?.Child is not ContentControl view)
            {
                _deferredScreenBodiesBuilt.Remove(name);
                return;
            }

            view.ApplyTemplate();
            view.Presenter?.UpdateChild();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Deferred build of {name} failed: {ex}");
        }
    }

    /// <summary>
    ///     The screen view inside a deferred host, or null. With
    ///     <paramref name="build" /> the body is built first if nothing has built
    ///     it yet; without, a body nobody has wanted stays unbuilt (the reveal's
    ///     skip runs on EVERY key and click, and must not construct Results).
    /// </summary>
    private T? DeferredScreen<T>(string hostName, bool build) where T : Control
    {
        if (build) BuildDeferredScreenBody(hostName);
        return this.FindControl<ContentControl>(hostName)?.Presenter?.Child as T;
    }

    /// <summary>Song Select's view, built on demand.</summary>
    private Screens.SongSelectView? SongSelect => DeferredScreen<Screens.SongSelectView>(SongSelectHostName, build: true);

    /// <summary>The Results view, built on demand.</summary>
    private Screens.ResultsView? Results => DeferredScreen<Screens.ResultsView>(ResultsHostName, build: true);

    /// <summary>The Results view if something has built it already; never builds.</summary>
    private Screens.ResultsView? ResultsIfBuilt => DeferredScreen<Screens.ResultsView>(ResultsHostName, build: false);

    /// <summary>
    ///     An element by <c>x:Name</c>, across the template namescopes the
    /// deferred hosts introduce and the screen views' own.
    ///     <c>FindControl</c> only sees THIS view's namescope, and a
    ///     <c>ContentTemplate</c> body or a lifted screen view registers its names
    ///     in its own — so a name looked up in this view's namescope would silently return null
    ///     and the surface would drop out of the prewarm list without a word. The
    ///     LOGICAL tree is the last resort: a view nested inside a screen view is
    ///     not in the visual tree until its own template has applied.
    /// </summary>
    private Control? FindByName(string name) =>
        this.FindControl<Control>(name)
        ?? this.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == name)
        ?? this.GetLogicalDescendants().OfType<Control>().FirstOrDefault(c => c.Name == name);

    /// <summary>The settings modal's six section panes plus its results pane —
    /// ONE gated overlay root with seven
    /// gated bodies inside it, which the sibling sweep above cannot reach. Named
    /// AFTER the overlay root in <see cref="HiddenSurfaces" /> because a gated
    /// parent has to be realized before its gated children.</summary>
    internal static readonly string[] GameplaySettingsPaneNames =
    [
        "GameSettingsAudioPane", "GameSettingsDevicesPane", "GameSettingsGameplayPane",
        "GameSettingsGraphicsPane", "GameSettingsAppearancePane", "GameSettingsKeybindsPane",
        "GameSettingsResultsPane"
    ];

    /// <summary>
    ///     The launch screen's two inline panels. They are a case the sibling
    ///     sweep above CANNOT reach for the opposite reason to the settings modal's:
    ///     <c>StartScreenRoot</c> is the boot screen, so it is VISIBLE and never
    ///     appears in <c>HiddenChildrenOf</c> at all — while these two gated
    ///     children inside it have still never been measured. Without this the
    ///     first click on "Profiles" pays the panel's whole realization on the
    ///     app's very first surface.
    /// </summary>
    internal static readonly string[] LaunchPanelNames = ["LaunchProfilesPanel", "LaunchGuestPanel"];

    /// <summary>
    ///     Gated bodies one level further down than every list above reaches —
    ///     each nested inside a screen root or modal body that IS
    ///     prewarmed, and each 30+ markup elements of its own (the
    ///     <c>PrewarmCoverageTests</c> bar). Listed LAST, so every gated parent
    ///     is realized first; resolved through the logical tree because the LAN
    ///     lobby's names live inside nested <c>UserControl</c>s whose visual
    ///     trees do not exist yet at listing time. Parent before child within the
    ///     list too.
    /// </summary>
    internal static readonly string[] NestedScreenBodyNames =
    [
        "ResultsTimingPanel",
        // The rig's tone drawer: a second hidden root INSIDE the prewarmed rig
        // overlay, so the overlay's prewarm never reached it and the first click
        // on the tone pill laid out sixty preset cards (owner: "pressing this
        // causes lag/short freeze"). The drawer is
        // inside the Tones VIEW (a UserControl, `RigTonesMode`), whose template
        // is not applied until it is measured — so the view is realized FIRST
        // (which puts the drawer in the visual tree), then the drawer, then the
        // designer's column inside it.
        "RigTonesMode",
        "RigToneBrowser",
        "RigToneDesignPanel"
    ];

    /// <summary>
    ///     Drives the settings modal's live input meter and latency chip. It
    ///     runs ONLY while the modal is open and is stopped on detach — a Play
    ///     Mode surface may not leave a clock ticking behind the editor, and
    ///     this one reads the audio capture ring on every tick.
    ///     <para>20 Hz at <c>Background</c> priority: the meter repaints only
    ///     when its LIT BAR COUNT changes, so a faster tick would buy nothing
    ///     and a slower one would make the meter feel dead.</para>
    /// </summary>
    private readonly DispatcherTimer _settingsMeterTimer = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromMilliseconds(50)
    };

    private PlayModeViewModel? _vm;

    /// <summary>Where this view publishes the Get Ready screen's key presses;
    /// the view model subscribes (<see cref="PlayModeViewModel.AttachKeySource" />).</summary>
    private readonly AvaloniaKeyGestureSource _keys = new();

    public PlayModeView()
    {
        InitializeComponent();

        // The animated menu backdrop is the custom-drawn MenuBackdropControl:
        // it gates its own ~30 fps timer on attach + effective visibility
        // (the 0.16 Wave-1 backdropLive semantics live inside the control),
        // so no gating wiring is needed here.

        // Results reveal (0.15.5 B3): staged score count-up → grade stamp →
        // record flash, orchestrated off IsResultsScreen. Reduced-motion
        // escape: any key or click while it runs skips to the final state
        // (tunnel + handledEventsToo so no child can swallow the skip).
        _settingsMeterTimer.Tick += (_, _) =>
        {
            _vm?.Settings.PollGameplaySettingsMeters();
            _vm?.PollGetReadyMeters();
        };

        DataContextChanged += (_, _) => WireViewModel();
        AddHandler(KeyDownEvent, (_, _) => SkipReveal(),
            RoutingStrategies.Tunnel, handledEventsToo: true);
        // The Get Ready screen's own shortcuts. TUNNEL, so a focused footer
        // Button cannot turn Enter into "click whatever has focus" — the hint
        // says ENTER START and it has to mean it.
        AddHandler(KeyDownEvent, OnGetReadyKeyDown, RoutingStrategies.Tunnel);
        // Song Select's search-box keyboard: Ctrl+F focuses the box from
        // anywhere on the screen, Esc empties it, Down hands the list the
        // keyboard. Same TUNNEL reason as above for Ctrl+F (the box itself may
        // already hold focus, and a TextBox would otherwise keep the chord);
        // the box's own two keys are resolved inside the handler.
        AddHandler(KeyDownEvent, OnSongSelectKeyDown, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, (_, _) => SkipReveal(),
            RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private void WireViewModel()
    {
        if (_vm != null)
        {
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
            _vm.Settings.PropertyChanged -= OnSettingsPropertyChanged;
            _vm.DetachKeySource(_keys);
        }

        _vm = DataContext as PlayModeViewModel;
        // Audio-reactive backdrop EQ (0.16 5.3): the control polls this at
        // its own throttled tick — same capture tap as the pre-play signal
        // meter, no extra timer, nothing polled while the backdrop is parked.
        MenuBackdrop.InputPeakProvider = _vm is { } vm ? vm.ReadBackdropInputPeak : null;
        if (_vm == null)
        {
            _settingsMeterTimer.Stop();
            return;
        }

        _vm.PropertyChanged += OnViewModelPropertyChanged;
        // The settings modal is its own view model: its open flag is
        // announced there, not on Play Mode.
        _vm.Settings.PropertyChanged += OnSettingsPropertyChanged;
        _vm.AttachKeySource(_keys);
        if (_vm.IsResultsScreen) StartReveal();
        SyncSettingsMeterTimer();
        // The DataContext can arrive either side of the attach; the launch
        // warm-up is latched, so asking from both is free and asking from
        // neither would leave the boot screen's chips empty forever.
        if (_attached && _vm.IsStartScreen) _vm.EnterLaunchScreen();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlayModeViewModel.IsTuningCheckScreen))
        {
            SyncSettingsMeterTimer();
            return;
        }

        if (e.PropertyName != nameof(PlayModeViewModel.IsResultsScreen)) return;
        if (_vm is { IsResultsScreen: true }) StartReveal();
        else CancelReveal();
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlayModeSettingsViewModel.IsGameplaySettingsOpen))
            SyncSettingsMeterTimer();
    }

    private bool _attached;

    /// <summary>Whether the meter clock is running — the seam a headless test reads
    /// to prove it follows the modal (it is a private timer otherwise).</summary>
    internal bool IsMeterClockRunning => _settingsMeterTimer.IsEnabled;

    /// <summary>The meter clock follows the two surfaces of THIS view that show
    /// live audio — the settings modal and the Get Ready screen — and never
    /// outlives the view's attachment. The Amp &amp; FX rig is not a branch here;
    /// it is its own view and runs its own clock on the same
    /// 50 ms cadence, gated on its own effective visibility, because a screen
    /// hosted from two places cannot borrow one host's timer.</summary>
    private void SyncSettingsMeterTimer()
    {
        if (_attached && _vm is { Settings.IsGameplaySettingsOpen: true } or { IsTuningCheckScreen: true })
            _settingsMeterTimer.Start();
        else _settingsMeterTimer.Stop();
    }

    /// <summary>
    ///     The Get Ready footer's printed shortcuts. The mapping is
    ///     <see cref="GetReadyKeys" /> — the SAME list the strip is drawn from —
    ///     and Escape is deliberately absent from what this claims: the app has
    ///     one Escape chain, in the shell, and claiming it here would navigate
    ///     back twice.
    /// </summary>
    private void OnGetReadyKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || _vm is not { IsTuningCheckScreen: true }) return;

        // A text input keeps its keys. The screen carries combo boxes and a
        // slider, and a bare "T" has to stay typeable inside one.
        // GetFocusedElement returns the INNERMOST focused element, which for a
        // NumericUpDown or an editable ComboBox is its own inner TextBox — so
        // this one test covers every text-entry control on the screen.
        var typing = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox;

        // The screen's view model decides what the key means (GetReadyKeys).
        if (_keys.Publish(e.Key, typing)) e.Handled = true;
    }

    /// <summary>
    ///     Song Select's keyboard flow (Ctrl+F, and Esc / Down inside the search
    ///     box), and ONLY Song Select's: gated on that screen showing, because this
    ///     handler sits on the root of a view that holds every Play Mode screen. It
    ///     is registered HERE, on the whole surface, so Ctrl+F reaches the box from
    ///     anywhere on the screen; what the keys do is
    ///     <see cref="Screens.SongSelectView.HandleSearchKeys" />, next to the box
    /// and the list.
    /// </summary>
    private void OnSongSelectKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || _vm is not { IsSongSelectScreen: true }) return;
        SongSelect?.HandleSearchKeys(e);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        SyncSettingsMeterTimer();
        // The launch screen is the app's boot surface — but `CurrentScreen`
        // STARTS at `Start`, so `OnCurrentScreenChanged` never fires for it and
        // nothing else would ever fill its chips. Same shape as
        // `PhraseCatalogView`'s `EnsureLoaded()`: the view's attach is the
        // open-it-early path, and the method is latched + Background-deferred
        // internally so this is never work in front of the first frame.
        if (_vm is { IsStartScreen: true }) _vm.EnterLaunchScreen();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        _settingsMeterTimer.Stop();
    }

    // ---- results reveal ---------------------------------------------------
    // The reveal itself lives with the controls it animates, in
    // Screens/ResultsView; this view decides WHEN: as the screen turns
    // on, as it is left, and on any key or click (the reduced-motion skip).

    private void StartReveal()
    {
        CancelReveal();
        if (_vm == null) return;
        Results?.StartReveal(_vm);
    }

    private void CancelReveal() => ResultsIfBuilt?.CancelReveal();

    /// <summary>Skips a running reveal straight to the final state; no-op when
    /// none is running (so ordinary input on the results screen is free).</summary>
    private void SkipReveal() => ResultsIfBuilt?.SkipReveal(_vm);
}
