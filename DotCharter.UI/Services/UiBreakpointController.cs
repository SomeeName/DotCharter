using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;

namespace DotCharter.UI.Services;

/// <summary>The three layout SHAPES the shell lays out in.</summary>
public enum UiBreakpoint
{
    /// <summary>Below <see cref="UiBreakpointController.CompactBelow" /> logical
    /// DIPs. Style class <c>Compact</c>.</summary>
    Compact,

    /// <summary>The design width band — the shape every surface was authored
    /// against. Wears NO class, so the default look is literally the default
    /// (nothing to un-apply, nothing to keep in step).</summary>
    Default,

    /// <summary>Above <see cref="UiBreakpointController.WideAbove" /> logical
    /// DIPs. Style class <c>Wide</c>.</summary>
    Wide
}

/// <summary>
///     <b>Layout SHAPE by window width — the other half of the interface
///     scale, and not a second scaling mechanism.</b>
///     <para>
///         <see cref="UiScaleController" /> makes everything BIGGER through
///         <c>MainWindow</c>'s <c>RootScaler</c> <c>LayoutTransformControl</c>.
///         That is the fix for legibility and it is the fix for nothing else:
///         a uniform transform lays the shell out at <c>(W/s) x (H/s)</c> and
///         draws it at <c>s</c>, so every ratio "content px / window px" is
///         INVARIANT under it. The 520 px hero column on a 3840 px window at
///         150 % is a 520 px column on a 2560 px logical window — still the
///         same picture, just larger. Coverage ("the screen does not use my
///         monitor") is a pure layout problem and needs
///         different LAYOUT, which is what this type switches on.
///     </para>
///
///     <para>
///         <b>THE WIDTH THIS KEYS ON IS THE INNER, POST-TRANSFORM WIDTH</b> —
///         <c>ClientSize.Width / UiScaleController.Scale</c> — and that choice
///         is load-bearing, not incidental:
///     </para>
///     <list type="bullet">
///         <item>
///             A breakpoint decides layout shape AT THE LOGICAL SIZE THE
///             CONTENT SEES. Everything downstream of it — every token in
///             <c>Ladders.Wide.axaml</c>, every <c>Window.Wide</c> selector — is
///             expressed in those same inner DIPs, because that is the
///             coordinate space inside the transform. Keying on the outer width
///             would hand a 1400 px column to a viewport that is only 2560 DIPs
///             wide.
///         </item>
///         <item>
///             So a 3840 window at 150 % is <see cref="UiBreakpoint.Default" />,
///             not <see cref="UiBreakpoint.Wide" />. That is the intended
///             answer: a user who scaled to 150 % asked for BIGGER content, not
///             for a different layout, and their content genuinely has 2560
///             logical pixels to live in. The two mechanisms COMPOSE this way
///             instead of compounding.
///         </item>
///         <item>
///             It also means the scale moving re-evaluates the breakpoint —
///             hence the <see cref="UiScaleController.ScaleChanged" />
///             subscription in <see cref="Attach" />.
///         </item>
///     </list>
///
///     <para>
///         <b>Two consumers, one switch.</b> (1) The window root wears
///         <c>Compact</c> / <c>Wide</c> as a style CLASS, so a view opts in with
///         <c>Selector="Window.Wide StackPanel#LaunchColumn"</c> and needs no
///         code-behind. (2) The LAYOUT-constant half of the Fire ladder is
///         swapped by merging <c>Styles/FireTokens/Ladders.Wide.axaml</c> over
///         the base ladder; <c>DynamicResource</c> re-resolves on the change, so
///         every rail, cap and column that reads a token moves with no per-view
///         edit. <b>Only LAYOUT constants are breakpointed</b> — never
///         <c>FireSize*</c>, <c>FontSize*</c>, <c>Space*</c> or the line-height
///         ladder. Those are the transform's job and ramping them here would
///         compound with it.
///     </para>
///
///     <para>
///         <b>Why its own type rather than a second arm on
///         <see cref="UiScaleController" />.</b> That controller holds a
///         persisted ACCESSIBILITY SETTING: a user choice, with a manual arm, an
///         auto arm and a config round-trip. A breakpoint is none of those — it
///         is a derived fact about the current window with nothing to persist,
///         no user gesture and a different output type (a class name, not a
///         factor). Folding them together would give one static two unrelated
///         reasons to change, and the scale arm is under active edit on a
///         parallel branch; a separate file keeps both merges trivial.
///     </para>
///
///     <para>
///         Pure and static where it can be: the width -> breakpoint table and
///         its hysteresis are asserted without a window by
///         <c>UI/UiBreakpointTests</c>, exactly like <see cref="AutoUiScale" />.
///     </para>
/// </summary>
public static class UiBreakpointController
{
    /// <summary>Below this many logical DIPs of width the shell is
    /// <see cref="UiBreakpoint.Compact" />. 1600 is the audit's number: a 1920
    /// window at 1.0 sits comfortably above it, so the ordinary 1080p user is
    /// on the DESIGN shape and nothing about their app moves.</summary>
    public const double CompactBelow = 1600.0;

    /// <summary>Above this many logical DIPs of width the shell is
    /// <see cref="UiBreakpoint.Wide" />. 2560 is the 1440p width: at 1.0 a 1440p
    /// monitor is the top of the design band and a 4K one is genuinely wide.
    /// The band is INCLUSIVE at both ends — exactly 1600 and exactly 2560 are
    /// <see cref="UiBreakpoint.Default" />.</summary>
    public const double WideAbove = 2560.0;

    /// <summary>
    ///     How far past a threshold the width must travel before the shape
    ///     changes.
    ///     <para>
    ///         Without it a window parked within a pixel of 2560 alternates on
    ///         every delivered size, and each alternation re-styles the whole
    ///         tree and re-resolves every <c>DynamicResource</c> in it. 64 DIPs
    ///         is roughly one drag-resize frame on a fast mouse, and small
    ///         enough that a deliberate snap/maximise still lands on the right
    ///         side. Same shape as <see cref="AutoUiScale.HysteresisMargin" />,
    ///         and for the same reason.
    ///     </para>
    /// </summary>
    public const double HysteresisMargin = 64.0;

    /// <summary>The style class a breakpoint wears on the window root.
    /// <see cref="UiBreakpoint.Default" /> wears NONE — the design shape is the
    /// unqualified selector, so nothing has to be written twice.</summary>
    public static string? ClassFor(UiBreakpoint breakpoint) => breakpoint switch
    {
        UiBreakpoint.Compact => "Compact",
        UiBreakpoint.Wide => "Wide",
        _ => null
    };

    /// <summary>Every class this type may put on a window root, so a caller can
    /// clear them without knowing the enum.</summary>
    public static IReadOnlyList<string> AllClasses { get; } = ["Compact", "Wide"];

    /// <summary>
    ///     The breakpoint for a logical width, with no hysteresis — the answer
    ///     for a FIRST layout, where there is no previous shape to be sticky
    ///     about. A non-finite or non-positive width answers
    ///     <see cref="UiBreakpoint.Default" /> rather than throwing: this runs
    ///     from a size-changed handler and a window mid-teardown reports zeroes,
    ///     and "the design shape" is the right thing to report when we do not
    ///     know.
    /// </summary>
    public static UiBreakpoint For(double logicalWidthDip)
    {
        if (!double.IsFinite(logicalWidthDip) || logicalWidthDip <= 0) return UiBreakpoint.Default;
        if (logicalWidthDip < CompactBelow) return UiBreakpoint.Compact;
        return logicalWidthDip > WideAbove ? UiBreakpoint.Wide : UiBreakpoint.Default;
    }

    /// <summary>
    ///     The breakpoint to adopt for <paramref name="logicalWidthDip" /> given
    ///     the one currently applied. Returns <paramref name="current" />
    ///     unchanged while the width is still inside the stickiness band around
    ///     it, so the caller's "did it change?" test is also its "should I
    ///     re-style?" test — which is the whole of the coalescing this mechanism
    ///     needs. A drag across a threshold costs ONE re-style; a wobble on the
    ///     threshold costs none.
    /// </summary>
    public static UiBreakpoint Next(double logicalWidthDip, UiBreakpoint current)
    {
        var target = For(logicalWidthDip);
        if (target == current) return current;
        if (!double.IsFinite(logicalWidthDip) || logicalWidthDip <= 0) return current;

        // Hold `current` until the width has left ITS band by the margin.
        // Testing the band of the value in force (rather than of the target) is
        // what makes this symmetric: the same 64 DIPs of slack applies whichever
        // way the edge is being dragged.
        var left = current switch
        {
            UiBreakpoint.Compact => logicalWidthDip >= CompactBelow + HysteresisMargin,
            UiBreakpoint.Wide => logicalWidthDip <= WideAbove - HysteresisMargin,
            _ => logicalWidthDip is < (CompactBelow - HysteresisMargin)
                 or > (WideAbove + HysteresisMargin)
        };

        return left ? target : current;
    }

    /// <summary>
    ///     The logical width a window's content is laid out in: the client width
    ///     divided by the global UI scale, because the shell lives inside
    ///     <c>RootScaler</c>. See the type remarks for why this and not
    ///     <c>ClientSize.Width</c>.
    /// </summary>
    public static double LogicalWidthOf(double clientWidthDip, double scale)
    {
        if (!double.IsFinite(clientWidthDip) || clientWidthDip <= 0) return 0;
        if (!double.IsFinite(scale) || scale <= 0) return clientWidthDip;
        return clientWidthDip / scale;
    }

    /// <summary>The wide layout-constant ladder, merged over
    /// <c>Ladders.axaml</c> while the shell is <see cref="UiBreakpoint.Wide" />.
    /// There is deliberately no Compact ladder — see the guide.</summary>
    private static readonly Uri WideLaddersUri =
        new("avares://DotCharter.Tone.UI/Styles/FireTokens/Ladders.Wide.axaml");

    /// <summary>
    ///     Applies <paramref name="breakpoint" /> to a window root and to the
    ///     application's resources, and answers whether anything actually
    ///     changed. Idempotent, and safe to call before the window has a size.
    ///     <para>
    ///         Separate from <see cref="Attach" /> so a test can drive the
    ///         mechanism at a chosen width without a size-changed event, and so
    ///         the ladder swap has exactly one implementation.
    ///     </para>
    /// </summary>
    public static bool Apply(Window window, UiBreakpoint breakpoint)
    {
        ArgumentNullException.ThrowIfNull(window);

        var wanted = ClassFor(breakpoint);
        var changed = false;

        foreach (var name in AllClasses)
        {
            var shouldHave = string.Equals(name, wanted, StringComparison.Ordinal);
            if (window.Classes.Contains(name) == shouldHave) continue;
            if (shouldHave) window.Classes.Add(name);
            else window.Classes.Remove(name);
            changed = true;
        }

        return ApplyWideLadders(breakpoint == UiBreakpoint.Wide) || changed;
    }

    /// <summary>
    ///     Merges (or un-merges) the wide layout ladder at APPLICATION scope.
    ///     <para>
    ///         Application scope, not window scope, because the tokens have to
    ///         reach a popup: under the native-popup path every flyout, dropdown
    ///         and tooltip is its own <c>PopupRoot</c>, and
    ///         <c>FirePopoverMaxWidth</c> is one of the tokens that moves.
    ///         <c>Theming/ThemeService</c> already establishes that
    ///         <c>Application.Resources</c> wins over the theme's
    ///         <c>Styles</c> resources — that is how palette switching works —
    ///         so appending here overrides the base ladder without editing it.
    ///     </para>
    ///     <para>
    ///         <b>This does not touch a closed popover, and must not start to.</b>
    ///         Changing a merged dictionary raises <c>ResourcesChanged</c> down
    ///         the tree; a closed popover is either still detached (it picks the
    ///         new values up when it next attaches) or already re-parented into
    /// its target's scope by <c>PrewarmableFlyout.OnClosed</c>, where
    ///         it re-resolves like any other control. Both are safe. The rule this
    ///         is a hair's breadth from ("never realize a popover while it is
    ///         DETACHED" — <c>Perf/PopoverRestyleUiPerfSmokeTests</c>) is broken by
    ///         CALLING <c>ApplyTemplate</c>/<c>Measure</c> on a detached subtree,
    ///         which nothing here does.
    ///     </para>
    /// </summary>
    private static bool ApplyWideLadders(bool wanted)
    {
        if (Application.Current is not { } app) return false;

        var existing = app.Resources.MergedDictionaries
            .OfType<ResourceInclude>()
            .FirstOrDefault(r => r.Source == WideLaddersUri);

        if (wanted == (existing != null)) return false;

        if (wanted)
        {
            app.Resources.MergedDictionaries.Add(
            new ResourceInclude((Uri?)null) { Source = WideLaddersUri });
        }
        else
        {
            app.Resources.MergedDictionaries.Remove(existing!);
        }

        return true;
    }

    /// <summary>
    ///     Follows <paramref name="window" />'s width until it closes.
    ///     <para>
    ///         <b>Never per pixel and never mid-first-frame.</b> The evaluation
    ///         itself is a division and a comparison, but the ACT — a class flip
    ///         plus a merged-dictionary change — re-styles the whole tree, so it
    ///         happens only when <see cref="Next" /> says the shape actually
    ///         changed. The caller is expected to invoke this from the window's
    ///         constructor: at that point <c>ClientSize</c> is still zero, which
    ///         <see cref="For" /> answers as <see cref="UiBreakpoint.Default" />,
    ///         i.e. the shell's first style pass runs exactly as it does today
    ///         and the first REAL evaluation arrives with the first size
    ///         delivery, after the initial layout.
    ///     </para>
    /// </summary>
    public static void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        var current = UiBreakpoint.Default;
        Apply(window, current);

        window.SizeChanged += OnSizeChanged;
        UiScaleController.ScaleChanged += OnScaleChanged;
        // A static event roots its subscriber for the life of the process.
        window.Closed += OnClosed;
        return;

        void Reapply()
        {
            var logical = LogicalWidthOf(window.ClientSize.Width, UiScaleController.Scale);
            var next = Next(logical, current);
            if (next == current) return;
            current = next;
            Apply(window, next);
        }

        void OnSizeChanged(object? sender, SizeChangedEventArgs e) => Reapply();
        void OnScaleChanged(double scale) => Reapply();

        void OnClosed(object? sender, EventArgs e)
        {
            window.SizeChanged -= OnSizeChanged;
            UiScaleController.ScaleChanged -= OnScaleChanged;
            window.Closed -= OnClosed;
            // Leave the application ladder as we found it: this window is the
            // only thing that ever set it, and a shell teardown that left the
            // wide tokens merged would hand them to whatever opens next.
            ApplyWideLadders(false);
        }
    }
}
