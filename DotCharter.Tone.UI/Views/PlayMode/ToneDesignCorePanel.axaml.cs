using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Tone.UI.ViewModels.PlayMode;

namespace DotCharter.Tone.UI.Views.PlayMode;

/// <summary>
///     The "Design a tone" panel both hosts share — the prompt, the conversation
///     and the result — over <c>ToneDesignCoreViewModel</c>. RigLab hosts it as
///     is; DotCharter's <c>ToneDesignPanel</c> wraps it and adds its matcher
///     blocks through <see cref="ExtrasTemplate" />.
///     <para>
///         The one piece of code here follows the conversation: when a request is
///         sent or an answer arrives, the thread scrolls just far enough to show
///         it — the new "You" bubble, or the answer card's actions. Without it a
///         second answer landed below the fold and the panel looked as if nothing
///         had happened.
///     </para>
/// </summary>
public partial class ToneDesignCorePanel : UserControl
{
    /// <summary>The host's own blocks, drawn under the conversation with the
    /// panel's view model as their data. A TEMPLATE, so a collapsed panel
    /// realizes none of it; null draws nothing.</summary>
    public static readonly StyledProperty<IDataTemplate?> ExtrasTemplateProperty =
        AvaloniaProperty.Register<ToneDesignCorePanel, IDataTemplate?>(nameof(ExtrasTemplate));

    private ToneDesignCoreViewModel? _vm;
    private bool _followQueued;

    /// <summary>Builds the panel.</summary>
    public ToneDesignCorePanel() => InitializeComponent();

    /// <inheritdoc cref="ExtrasTemplateProperty" />
    public IDataTemplate? ExtrasTemplate
    {
        get => GetValue(ExtrasTemplateProperty);
        set => SetValue(ExtrasTemplateProperty, value);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Hook(this.IsAttachedToVisualTree() ? DataContext as ToneDesignCoreViewModel : null);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Hook(DataContext as ToneDesignCoreViewModel);
        // A panel opened over a conversation that already happened shows its
        // newest turn, not its oldest.
        if (_vm is { } vm && (vm.Turns.Count > 0 || vm.ShowResult)) QueueFollow();
    }

    /// <summary>Unhooks on detach: the view model outlives any one view of it
    /// (the browser owns it), so a hooked handler would root a dead panel.</summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Hook(null);
        base.OnDetachedFromVisualTree(e);
    }

    private void Hook(ToneDesignCoreViewModel? vm)
    {
        if (ReferenceEquals(vm, _vm)) return;
        if (_vm is { } old)
        {
            old.PropertyChanged -= OnViewModelChanged;
            old.Turns.CollectionChanged -= OnTurnsChanged;
        }

        _vm = vm;
        if (vm is null) return;
        vm.PropertyChanged += OnViewModelChanged;
        vm.Turns.CollectionChanged += OnTurnsChanged;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ToneDesignCoreViewModel.ShowResult) && _vm is { ShowResult: true })
            QueueFollow();
        else if (e.PropertyName == nameof(ToneDesignCoreViewModel.IsExpanded) && _vm is { IsExpanded: true })
            QueueFollow();
    }

    private void OnTurnsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add) QueueFollow();
    }

    /// <summary>One follow per burst (a finished design adds a turn AND shows the
    /// card), run after the layout that realizes what it scrolls to.</summary>
    private void QueueFollow()
    {
        if (_followQueued) return;
        _followQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _followQueued = false;
            FollowConversation();
        }, DispatcherPriority.Background);
    }

    /// <summary>Scrolls the thread just far enough to show the newest thing in
    /// it: the answer card — its BOTTOM, where the actions are, when the card is
    /// taller than the thread — while an answer is up, the newest turn
    /// otherwise.</summary>
    internal void FollowConversation()
    {
        if (_vm is null) return;
        var target = _vm.ShowResult
            ? Named("ToneDesignResult")
            : (Named("ToneDesignTurns") as ItemsControl) is { ItemCount: > 0 } turns
                ? turns.ContainerFromIndex(turns.ItemCount - 1)
                : null;
        if (target is null) return;

        var viewport = (Named("ToneDesignScroll") as ScrollViewer)?.Viewport.Height ?? 0;
        var height = target.Bounds.Height;
        target.BringIntoView(viewport > 0 && height > viewport
            ? new Rect(0, height - viewport, target.Bounds.Width, viewport)
            : new Rect(target.Bounds.Size));
    }

    /// <summary>A named control inside the body template, which keeps its own
    /// name scope — <c>FindControl</c> from here cannot see into it.</summary>
    private Control? Named(string name) =>
        this.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == name);
}
