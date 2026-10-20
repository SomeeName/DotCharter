using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace DotCharter.Tone.UI.Controls;

/// <summary>
///     The ONE shell every popover that hangs off the transport bar is drawn on:
///     a titled head with an icon and a dismiss hint, a body, and an optional
///     footer strip.
/// </summary>
/// <remarks>
///     <para>
///         From the owner's <c>Play Menus.dc.html</c>, whose SHELL RULES panel
///         states the whole contract: <b>head 40 px · rows 34 px · radius 14/9 ·
///         label left, control right, value in mono · a popover is never wider
///         than 420 px</b>. Before it, eight surfaces each re-implemented that
///         chrome inline — three different head lockups, three different widths
///         (300 / 320 / 460) and, in most of them, no footer at all, so the
///         shortcut that opens the panel was written nowhere.
///     </para>
///     <para>
///         <b>It is a <c>TemplatedControl</c>, not a set of style classes, and
///         that is deliberate.</b> A class can be forgotten one surface at a
///         time; a control cannot. The theme is TYPE-KEYED in
///         <c>Styles/ControlThemes.axaml</c> because that is the only mechanism
///         that reaches a custom <c>TemplatedControl</c> from any surface — and a
///         popover is realized inside a <c>FlyoutPresenter</c>, i.e. in a popup's
///         own namescope, which is exactly where a view-scoped
///         <c>UserControl.Styles</c> entry would have failed silently
///         (<c>KeybindCaptureBox</c>, 0.39.1).
///     </para>
///     <para>
///         The FOOTER is a separate content slot rather than part of the body so
///         it can pin below a body that scrolls, and so the strip disappears
///         entirely when a surface has no footer action —
///         <see cref="HasFooter" /> is maintained here rather than by a converter
///         so the template needs no binding gymnastics.
///     </para>
/// </remarks>
[TemplatePart("PART_Head", typeof(Border))]
[TemplatePart("PART_Footer", typeof(Border))]
public class FirePopover : ContentControl
{
    /// <summary>The glyph in the head's badge. A popover with no icon still
    /// lays out — the badge simply draws empty — but every surface in the
    /// mockup has one.</summary>
    public static readonly StyledProperty<Geometry?> IconProperty =
        AvaloniaProperty.Register<FirePopover, Geometry?>(nameof(Icon));

    /// <summary>The head's title. Localized by the caller.</summary>
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<FirePopover, string?>(nameof(Title));

    /// <summary>
    ///     The key legend on the right of the head. Defaults to <c>ESC</c>,
    ///     which is what every popover in the mockup carries and what actually
    ///     dismisses one; it is a KEY CAP, so it is not translated (neither are
    ///     the gesture strings on the menu rows).
    /// </summary>
    public static readonly StyledProperty<string?> DismissHintProperty =
        AvaloniaProperty.Register<FirePopover, string?>(nameof(DismissHint), "ESC");

    /// <summary>The footer strip's content — typically a quiet action on the
    /// left and the popover's own shortcut, right-aligned.</summary>
    public static readonly StyledProperty<object?> FooterProperty =
        AvaloniaProperty.Register<FirePopover, object?>(nameof(Footer));

    /// <summary>Whether <see cref="Footer" /> has anything in it. The template
    /// hides the whole strip — hairline, fill and inset — when it does
    /// not.</summary>
    public static readonly StyledProperty<bool> HasFooterProperty =
        AvaloniaProperty.Register<FirePopover, bool>(nameof(HasFooter));

    static FirePopover()
    {
        FooterProperty.Changed.AddClassHandler<FirePopover>((popover, e) =>
            popover.SetCurrentValue(HasFooterProperty, e.NewValue is not null));
    }

    /// <summary>
    ///     The class a <c>FlyoutPresenter</c> carries while it hosts a popover.
    ///     The popover draws its own edge, fill and radius, so a presenter that
    ///     kept its card inset framed it a second time. The shell's rails zeroed
    ///     that inset in a style only THEIR flyouts could see; the song lists'
    ///     filter funnels, outside the shell, would still draw the double frame.
    ///     Marking the host from here reaches every surface.
    /// </summary>
    public const string HostClass = "FirePopoverHost";

    private FlyoutPresenter? _host;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _host = this.FindAncestorOfType<FlyoutPresenter>();
        _host?.Classes.Add(HostClass);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _host?.Classes.Remove(HostClass);
        _host = null;
    }

    public Geometry? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? DismissHint
    {
        get => GetValue(DismissHintProperty);
        set => SetValue(DismissHintProperty, value);
    }

    public object? Footer
    {
        get => GetValue(FooterProperty);
        set => SetValue(FooterProperty, value);
    }

    public bool HasFooter
    {
        get => GetValue(HasFooterProperty);
        set => SetValue(HasFooterProperty, value);
    }
}
