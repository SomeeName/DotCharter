using Avalonia.Controls;
using Avalonia.Media;
using DotCharter.UI.Services;

namespace DotCharter.UI.Views.Shell;

/// <summary>
///     The global UI scale (an ACCESSIBILITY setting) on a window that is not
///     the shell.
///     <para>
///         <c>MainWindow</c> has carried a <c>LayoutTransformControl</c> named
///         <see cref="ScalerName" /> since the setting shipped, and Avalonia has
///         no per-control layout transform, so that wrapper is the whole
///         mechanism. The app's two other top-level windows — Session Health and
///         About — had none: a user who set 150 % because they cannot read 100 %
///         got both of those at 100 %, and neither followed an RTL language pack
///         while the shell mirrored around them.
///     </para>
///     <para>
///         The two halves of <see cref="Attach" /> answer the two window shapes.
///         A RESIZABLE window keeps its size and simply reflows scaled content.
///         A window with a pinned <c>Width</c> and <c>CanResize="False"</c>
///         cannot: the transform makes the content wider and the pinned width
///         just clips it, so its declared width and minimum scale with it.
///     </para>
/// </summary>
public static class WindowUiScale
{
    /// <summary>The <c>x:Name</c> of the wrapper every scalable window carries.</summary>
    public const string ScalerName = "RootScaler";

    /// <summary>The transform for a scale factor. Identity (1.0) is NULL, not a
    /// 1× <c>ScaleTransform</c> — a null layout transform is a true no-op, and
    /// that is what <c>MainWindow</c> has always done.</summary>
    public static ITransform? TransformFor(double scale) =>
        scale is > 0.999 and < 1.001 ? null : new ScaleTransform(scale, scale);

    /// <summary>A declared window dimension at the given scale. Leaves
    /// <c>NaN</c> (i.e. <c>SizeToContent</c>) and any non-positive value
    /// exactly as it found it.</summary>
    public static double ScaledLength(double declared, double scale) =>
        double.IsFinite(declared) && declared > 0 ? declared * scale : declared;

    /// <summary>Applies the current scale to <paramref name="window" /> and
    /// follows <see cref="UiScaleController" /> until it closes. A window with
    /// no <see cref="ScalerName" /> wrapper is left alone.</summary>
    public static void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (window.FindControl<LayoutTransformControl>(ScalerName) is not { } scaler) return;

        // Read once: after the first Apply these properties hold SCALED values,
        // so re-reading them would compound.
        var declaredWidth = window.Width;
        var declaredMinWidth = window.MinWidth;
        var declaredHeight = window.Height;
        var declaredMinHeight = window.MinHeight;

        Apply(UiScaleController.Scale);
        UiScaleController.ScaleChanged += Apply;
        // A static event roots its subscriber for the life of the process, and
        // these windows are per-open instances (the diagnostics one is
        // transient by design). Unhooking on close is what keeps them collectable.
        window.Closed += OnClosed;
        return;

        void Apply(double scale)
        {
            scaler.LayoutTransform = TransformFor(scale);

            // A resizable window reflows inside whatever size the user gave it.
            if (window.CanResize) return;

            window.MinWidth = ScaledLength(declaredMinWidth, scale);
            window.MinHeight = ScaledLength(declaredMinHeight, scale);
            window.Width = ScaledLength(declaredWidth, scale);
            window.Height = ScaledLength(declaredHeight, scale);
        }

        void OnClosed(object? sender, EventArgs e)
        {
            UiScaleController.ScaleChanged -= Apply;
            window.Closed -= OnClosed;
        }
    }
}
