using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace DotCharter.Tone.UI.Controls;

/// <summary>
///     Reports changes of a control's EFFECTIVE visibility — its own
///     <c>IsVisible</c> AND every visual ancestor's — because Avalonia 12
///     exposes no public change notification for it.
///     <para>
///         <b>The bug this exists to end.</b>
///         <c>IsEffectivelyVisible</c> is a computed CLR property, not an
///         <c>AvaloniaProperty</c> (the change event moved internal), so every
///         <c>change.Property.Name == nameof(IsEffectivelyVisible)</c> branch
///         this repo carried was DEAD CODE that never fired once — six
///         surfaces' park/wake logic silently did nothing: the menu backdrop's
///         timer gating, both 3D canvases' render parking, the rig's meter
///         clock, and both song lists' cover re-warm. Each compiled, read as
///         the sanctioned pattern, and was cited by docs as such.
///     </para>
///     <para>
///         The watcher subscribes <c>IsVisibleProperty</c> on the control and
///         each ancestor, rebuilding the chain on every visual attach/detach
///         (reparenting always detaches first, so the chain cannot go stale).
///         The callback fires on CHANGES only — construction sets the baseline
///         silently, and a detached control counts as not visible. UI thread
///         only, like the trees it walks.
///     </para>
/// </summary>
public sealed class EffectiveVisibilityWatcher : IDisposable
{
    private readonly Control _control;
    private readonly Action<bool> _onChanged;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly bool _baselineSet;
    private bool _attached;
    private bool _last;

    public EffectiveVisibilityWatcher(Control control, Action<bool> onChanged)
    {
        _control = control;
        _onChanged = onChanged;

        control.AttachedToVisualTree += OnAttached;
        control.DetachedFromVisualTree += OnDetached;

        // Constructed in a ctor in every product use, i.e. before the first
        // attach — the baseline is therefore "not visible", and the first
        // attach reports the first real flip.
        _last = false;
        _baselineSet = true;
        Resubscribe();
    }

    /// <summary>The value last reported (or the construction baseline) — test seam.</summary>
    public bool IsEffectivelyVisible => _last;

    private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _attached = true;
        Resubscribe();
        Evaluate();
    }

    private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        Resubscribe();
        Evaluate();
    }

    private void Resubscribe()
    {
        foreach (var subscription in _subscriptions) subscription.Dispose();
        _subscriptions.Clear();

        Visual? visual = _control;
        while (visual != null)
        {
            // GetObservable fires immediately with the current value; the
            // baseline flag keeps that from reporting a phantom change.
            _subscriptions.Add(visual.GetObservable(Visual.IsVisibleProperty)
                .Subscribe(new AnonymousObserver<bool>(_ => Evaluate())));
            visual = visual.GetVisualParent();
        }
    }

    private bool CurrentlyVisible()
        => _attached && _control.IsEffectivelyVisible;

    private void Evaluate()
    {
        if (!_baselineSet) return;
        var visible = CurrentlyVisible();
        if (visible == _last) return;
        _last = visible;
        _onChanged(visible);
    }

    public void Dispose()
    {
        _control.AttachedToVisualTree -= OnAttached;
        _control.DetachedFromVisualTree -= OnDetached;
        foreach (var subscription in _subscriptions) subscription.Dispose();
        _subscriptions.Clear();
    }

    /// <summary>Minimal observer so this file needs no Rx package — Avalonia's
    /// GetObservable hands back a plain <see cref="IObservable{T}" />.</summary>
    private sealed class AnonymousObserver<T>(Action<T> onNext) : IObserver<T>
    {
        public void OnNext(T value) => onNext(value);
        public void OnCompleted() { }
        public void OnError(Exception error) { }
    }
}
