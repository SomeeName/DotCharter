using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using DotCharter.UI.Services;

namespace DotCharter.UI.Views.Shell;

/// <summary>
///     What a floating window the shell opens carries over from <c>MainWindow</c>:
///     the interface scale (a <see cref="LayoutTransformControl" /> named
///     <see cref="WindowUiScale.ScalerName" />, following <see cref="UiScaleController" />
///     until the window closes) and the flow direction of <c>MainWindow</c>'s root,
///     which follows the language. The 3D preview
///     and timeline pop-outs wrap their content; a Dock float window, whose content
///     Dock sets, gets the scaler around its template's content presenter.
/// </summary>
internal static class FloatingWindowShell
{
    private const string ContentPresenterPart = "PART_ContentPresenter";

    /// <summary>Makes <paramref name="content" /> the window's content inside a scaler; the caller detaches it again with <c>scaler.Child = null</c>.</summary>
    public static LayoutTransformControl Host(Window window, Control content, Visual flowSource)
    {
        var scaler = new LayoutTransformControl { Name = WindowUiScale.ScalerName, Child = content };
        window.Content = scaler;
        Follow(window, scaler, flowSource);
        return scaler;
    }

    /// <summary>For a window whose content a library sets: the scaler goes around the template's content presenter.</summary>
    public static LayoutTransformControl HostTemplated(Window window, Visual flowSource)
    {
        var scaler = new LayoutTransformControl { Name = WindowUiScale.ScalerName };
        // Every template the window gets while open (a theme or style change re-templates it), not only the first.
        window.TemplateApplied += OnTemplateApplied;
        window.PropertyChanged += OnWindowPropertyChanged;
        window.Closed += OnClosed;
        Follow(window, scaler, flowSource);
        return scaler;

        void OnTemplateApplied(object? sender, TemplateAppliedEventArgs e)
        {
            if (e.NameScope.Find<ContentPresenter>(ContentPresenterPart) is not { } presenter) return;
            Unwrap(scaler);
            WrapInPlace(presenter, scaler);
        }

        // The old template is torn down only through controls it made itself: the presenter goes back in first.
        void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == TemplatedControl.TemplateProperty) Unwrap(scaler);
        }

        void OnClosed(object? sender, EventArgs e)
        {
            window.TemplateApplied -= OnTemplateApplied;
            window.PropertyChanged -= OnWindowPropertyChanged;
            window.Closed -= OnClosed;
        }
    }

    /// <summary>Undoes <see cref="WrapInPlace" />: the presenter goes back where <paramref name="scaler" /> sits, which leaves the template.</summary>
    private static void Unwrap(LayoutTransformControl scaler)
    {
        var presenter = scaler.Child;
        scaler.Child = null;
        switch (scaler.GetVisualParent())
        {
            case Decorator decorator:
                decorator.Child = presenter;
                break;
            case Panel panel:
                var index = panel.Children.IndexOf(scaler);
                panel.Children.RemoveAt(index);
                if (presenter is not null) panel.Children.Insert(index, presenter);
                break;
        }
    }

    /// <summary>Puts <paramref name="scaler" /> where <paramref name="presenter" /> sits in its template, with the presenter inside it.</summary>
    internal static bool WrapInPlace(Control presenter, LayoutTransformControl scaler)
    {
        switch (presenter.GetVisualParent())
        {
            case Decorator decorator:
                decorator.Child = null;
                scaler.Child = presenter;
                decorator.Child = scaler;
                return true;
            case Panel panel:
                var index = panel.Children.IndexOf(presenter);
                panel.Children.RemoveAt(index);
                scaler.Child = presenter;
                panel.Children.Insert(index, scaler);
                return true;
            default:
                return false;
        }
    }

    private static void Follow(Window window, LayoutTransformControl scaler, Visual flowSource)
    {
        var flow = window.Bind(Visual.FlowDirectionProperty, flowSource.GetObservable(Visual.FlowDirectionProperty));
        Apply(UiScaleController.Scale);
        UiScaleController.ScaleChanged += Apply;
        // A static event roots its subscriber: unhooked when the window closes.
        window.Closed += OnClosed;
        return;

        void Apply(double scale) => scaler.LayoutTransform = WindowUiScale.TransformFor(scale);

        void OnClosed(object? sender, EventArgs e)
        {
            UiScaleController.ScaleChanged -= Apply;
            flow.Dispose();
            window.Closed -= OnClosed;
        }
    }
}
