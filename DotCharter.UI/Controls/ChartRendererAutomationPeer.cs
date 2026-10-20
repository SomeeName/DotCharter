using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace DotCharter.UI.Controls;

/// <summary>
/// The chart timeline's accessibility face. <see cref="ChartRenderer" />
///     draws through composed Vulkan and has no visual children, so without a
///     peer UI Automation saw one unnamed group: no arrangement, no playhead, no
///     selection — a screen-reader user could not tell what the editor's keys
///     had just selected.
///     <para>
///         <b>Name</b> is the surface (arrangement + playhead in bars and beats),
///         <b>Value</b> the selection ("Chord Am, bar 12 beat 3, 4 notes"), and
///         <b>HelpText</b> the user's own bindings for the keys that move it. The
///         value is read-only: editing happens in the inspector, which is named.
///     </para>
///     <para>
///         <b>Announcements.</b> While its owner is attached the peer listens to
///         the app-wide selection (and lets go on detach: the service is a
///         singleton and the timeline is re-parented on every pop-out), and to
///         the owner's KeyUp — by then a seek verb has moved the playhead, which
///         is in the Name. Each burst is coalesced into one
///         <see cref="Refresh" /> on the dispatcher: a click-select is a Clear
///         plus an Add, and reading "No selection" between them would be noise.
///         It exists only once a client asked for it, so nobody pays for this
///         while no screen reader runs.
///     </para>
///     <para>
///         The words come from the pure <see cref="ChartTimelineDescription" />.
///         The peer reads view-model state through its owner's <c>Editor</c> and
///         never touches the renderer's Vulkan backend.
///     </para>
/// </summary>
internal sealed class ChartRendererAutomationPeer : ControlAutomationPeer, IValueProvider
{
    private ISelectionService? _selection;
    private bool _refreshQueued;
    private string _announcedName;
    private string _announcedValue;

    public ChartRendererAutomationPeer(ChartRenderer owner) : base(owner)
    {
        _announcedName = DescribeName();
        _announcedValue = DescribeValue();

        // All on the owner itself, so they live and die with it.
        // The attach state is passed in, not read: inside the Detached event the
        // control still reports itself attached.
        owner.AttachedToVisualTree += (_, _) => SyncSelectionSubscription(attached: true);
        owner.DetachedFromVisualTree += (_, _) => SyncSelectionSubscription(attached: false);
        owner.PropertyChanged += OnOwnerPropertyChanged;
        owner.AddHandler(InputElement.KeyUpEvent, (_, _) => QueueRefresh(), RoutingStrategies.Bubble, handledEventsToo: true);
        SyncSelectionSubscription(owner.IsAttachedToVisualTree());
    }

    private new ChartRenderer Owner => (ChartRenderer)base.Owner;

    public bool IsReadOnly => true;

    public string? Value => DescribeValue();

    public void SetValue(string? value) =>
        throw new InvalidOperationException("The chart timeline's selection is read-only to automation clients.");

    /// <summary>Raises Name and Value changes for what moved since the last call.</summary>
    internal void Refresh()
    {
        var name = DescribeName();
        if (name != _announcedName)
        {
            RaisePropertyChangedEvent(AutomationElementIdentifiers.NameProperty, _announcedName, name);
            _announcedName = name;
        }

        var value = DescribeValue();
        if (value != _announcedValue)
        {
            RaisePropertyChangedEvent(ValuePatternIdentifiers.ValueProperty, _announcedValue, value);
            _announcedValue = value;
        }
    }

    protected override string GetNameCore() => DescribeName();

    protected override string GetHelpTextCore() => Owner.Editor is { } editor
        ? ChartTimelineDescription.DescribeKeys(editor.UserSettings.Keybinds, editor.Localization)
        : string.Empty;

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Custom;

    protected override string GetClassNameCore() => nameof(ChartRenderer);

    private Arrangement? Arrangement => Owner.OverrideArrangement ?? Owner.Editor?.ActiveArrangement;

    private IList<BeatMarker> Beats => Owner.Editor?.Beats ?? (IList<BeatMarker>)[];

    // Words in the EDITOR's language: XAML builds the renderer, so its
    // localization comes from the view model it is bound to. Unbound, the
    // timeline draws nothing and has nothing to say.
    private string DescribeName() => Owner.Editor is { } editor
        ? ChartTimelineDescription.DescribeSurface(Arrangement, Beats, editor.PlayheadMs, editor.Localization)
        : string.Empty;

    private string DescribeValue() => Owner.Editor is { } editor
        ? ChartTimelineDescription.DescribeSelection(editor.Selection, Arrangement, Beats, editor.Localization)
        : string.Empty;

    private void OnOwnerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != ChartRenderer.EditorProperty && e.Property != ChartRenderer.OverrideArrangementProperty) return;
        SyncSelectionSubscription(Owner.IsAttachedToVisualTree());
        QueueRefresh();
    }

    private void SyncSelectionSubscription(bool attached)
    {
        var target = attached ? Owner.Editor?.Selection : null;
        if (ReferenceEquals(_selection, target)) return;
        _selection?.SelectionChanged -= OnSelectionChanged;
        _selection = target;
        _selection?.SelectionChanged += OnSelectionChanged;
    }

    private void OnSelectionChanged(object? sender, EventArgs e) => QueueRefresh();

    private void QueueRefresh()
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _refreshQueued = false;
            Refresh();
        }, DispatcherPriority.Background);
    }
}
