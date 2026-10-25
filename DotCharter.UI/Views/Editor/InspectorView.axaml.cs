using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using DotCharter.UI.Controls;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.Views.Editor.Inspector;
using DotCharter.UI.Views.Shell;

namespace DotCharter.UI.Views.Editor;

public partial class InspectorView : UserControl, IPrewarmableSurfaces
{
    // Threshold (px) at which the inspector switches to a 2-column masonry layout.
    private const double WideLayoutThreshold = 720;

    private bool? _lastIsWide;

    // one rewarmer per tab panel. The startup prewarm lays every hidden tab
    // out once, usually before a project is open; what the project then changes
    // behind a hidden tab (its lists, and the IsVisible / text / enabled state a
    // project or arrangement switch flips) was laid out on the click instead —
    // measured 150-320 ms for Mixer, Song and Phrases on the owner's machine.
    private readonly List<HiddenSurfaceRewarmer> _tabRewarmers = [];
    private ProjectState? _watchedState;

    public InspectorView()
    {
        InitializeComponent();
        if (this.FindControl<OverflowTabStrip>("InspectorTabRow") is { } strip
            && this.FindControl<ToggleButton>("InspectorTabMore") is { } more)
        {
            InspectorTabMoreMenu.Attach(strip, more, index =>
            {
                if (DataContext is InspectorViewModel vm) SelectTab(vm, index);
            });
        }
        LayoutUpdated += OnLayoutUpdated;
        if (this.FindControl<Grid>("InspectorTabPanels") is { } panels)
        {
            foreach (var tab in panels.Children)
            {
                _tabRewarmers.Add(tab switch
                {
                    SongInspectorView song => song.Rewarmer,
                    // a tab with a section rail rewarms its hidden sections too.
                    MixerInspectorView mixer => new HiddenSurfaceRewarmer(tab, () => mixer.Rail.Bodies),
                    LyricsInspectorView lyrics => new HiddenSurfaceRewarmer(tab, () => lyrics.Rail.Bodies),
                    _ => new HiddenSurfaceRewarmer(tab)
                });
            }
        }
    }

    /// <summary>The tab panels' rewarmers, in panel order — the test hook.</summary>
    internal IReadOnlyList<HiddenSurfaceRewarmer> TabRewarmers => _tabRewarmers;

    /// <summary>Asks every HIDDEN tab for one background re-realization.</summary>
    internal void RequestTabRewarm()
    {
        foreach (var rewarmer in _tabRewarmers) rewarmer.Request();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        WatchProjectState(DataContext as InspectorViewModel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        WatchProjectState(null);
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (TopLevel.GetTopLevel(this) is not null) WatchProjectState(DataContext as InspectorViewModel);
    }

    // ProjectState is an app singleton: subscribed only while attached, so a
    // re-created pane (float / redock) never leaks the old view.
    private void WatchProjectState(InspectorViewModel? vm)
    {
        var state = vm?.ProjectState;
        if (ReferenceEquals(state, _watchedState)) return;
        _watchedState?.PropertyChanged -= OnProjectStateChanged;
        _watchedState = state;
        _watchedState?.PropertyChanged += OnProjectStateChanged;
    }

    private void OnProjectStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ProjectState.ActiveProject) or nameof(ProjectState.ActiveArrangement))
            RequestTabRewarm();
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        var isWide = Bounds.Width >= WideLayoutThreshold;
        if (_lastIsWide == isWide) return;
        _lastIsWide = isWide;

        if (isWide != Classes.Contains("Wide"))
        {
            if (isWide) Classes.Add("Wide");
            else Classes.Remove("Wide");
        }
    }

    /// <summary>
    ///     The seven inactive tab panels. All eight <c>UserControl</c>s are
    ///     constructed with this view, but Avalonia skips <c>MeasureCore</c> for
    ///     an invisible element — so until this landed, every tab the user had
    ///     not clicked THIS SESSION paid its whole first layout on the click.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The inspector is a DOCK pane: it is materialized by the app
    ///         <c>ViewLocator</c> inside <c>BottomDock</c>, so it is named in no
    ///         markup the shell can <c>FindControl</c> and
    ///         <c>MainWindow.HiddenShellSurfaces()</c> could never list it. The
    ///         shell reaches it by walking <c>BottomDock</c> instead — see
    ///         <c>MainWindow.PrewarmableSurfaceOwners()</c>.
    ///     </para>
    ///     <para>
    ///         The Sound tab's own gated cards are one level further down and the
    ///         prewarmer does not recurse, so <see cref="SoundInspectorView" />
    ///         lists them itself and the shell asks it separately, AFTER this —
    ///         parent before child, or the child measures against a slot nothing
    ///         has arranged.
    ///     </para>
    /// </remarks>
    public IReadOnlyList<Control> HiddenSurfaces() =>
    [
        .. HiddenSubtreePrewarmer.HiddenChildrenOf(this.FindControl<Grid>("InspectorTabPanels")),
        // …then the hidden sections of the tabs with a section rail, after
        // their tab: a section inside a tab panel is a second gate the sibling
        // sweep cannot see, and only one section of each tab is ever visible.
        .. HiddenSubtreePrewarmer.HiddenNamedDescendants(
            this.FindControl<Grid>("InspectorTabPanels"), NestedPaneNames)
    ];

    /// <summary>The gated sections nested inside a tab panel that the tab sweep
    /// stops short of: the section bodies of the Mixer, Song and Lyrics rails,
    /// biggest tab first. The Sound tab's sections are its own owner (see
    /// <see cref="SoundTab" />).</summary>
    internal static readonly string[] NestedPaneNames =
    [
        "MixerLevelsCard", "MixerStemsBody", "MixerPlaybackFxBody", "MixerEqBody", "MixerMidiBody",
        "SongKeyBody", "SongTonesBody", "SongSectionsBody",
        "LyricsRawBody", "LyricsSyllableBody", "LyricsTransferBody"
    ];

    /// <summary>The Sound tab instance, or null before the template has run.
    /// Handed to the shell so it can ask the tab for its own gated cards without
    /// crossing a namescope <c>FindControl</c> cannot cross.</summary>
    internal SoundInspectorView? SoundTab =>
        this.FindControl<Grid>("InspectorTabPanels")?.Children.OfType<SoundInspectorView>().FirstOrDefault();

    private void TabHeader_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton btn || DataContext is not InspectorViewModel vm) return;

        // Re-check the button if the user clicked the already-active tab —
        // ToggleButton flips its IsChecked on click, but for tabs we want
        // the active button to remain pressed.
        if (btn.Tag is not string tagText
            || !int.TryParse(tagText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
        {
            return;
        }

        if (vm.SelectedTabIndex == index)
        {
            btn.IsChecked = true;
            return;
        }

        SelectTab(vm, index);
    }

    private static void SelectTab(InspectorViewModel vm, int index)
    {
        // what the switch costs on this box, in the app log.
        Shell.SurfaceShowTiming.Begin($"Inspector tab {index}");
        vm.SelectedTabIndex = index;
    }
}
