// DialogConstructionUiPerfSmokeTests root: the class contract, the legacy dialog catalogues (Converted / AllOverlayDialogs) and CreateDialog factory, the four grammar assertions (AssertShell / AssertRows / AssertFooter / AssertTints) and the WithLaidOutDialog / Descendants / RepoRoot plumbing every partial uses.

using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using DotCharter.UI.Views.Dialogs;
using DotCharter.UI.Views.Shell;

namespace DotCharter.Tests.Perf;

/// <summary>
///     Builds every overlay dialog for real — inside the PRODUCTION host
///     (<see cref="DialogContainerView" />, which is where the shared
///     <c>Views/Dialogs/DialogStyles.axaml</c> is included) — attaches it to a
///     window and runs a layout pass, then asserts the dialog grammar actually
///     produced a head, rows on one right edge and a footer.
///     <para>
///         A restyled dialog is the exact shape of change that builds clean and
///         fails at runtime. This repo has shipped four separate silent XAML
///         faults: an unresolvable <c>StaticResource</c>, a <c>StringFormat</c>
///         handed a <c>Binding</c>, a <c>RelayCommand&lt;int&gt;</c> fed a
///         literal <c>CommandParameter</c> (throws during layout, from
///         <c>CanExecute</c>, once the tree is ROOTED) and a view that failed to
///         construct and rendered as a "Not Found:" placeholder. Construction
///         catches the last one; attaching + measuring catches the rest. A fifth
///         was found by this file: a <c>Setter</c> handing an <c>x:Double</c>
///         token to a <c>Thickness</c> property, which throws out of
///         <c>OnAttachedToLogicalTree</c> — clean build, dead dialog.
///     </para>
///     <para>
///         Dialogs are laid out with NO DataContext on purpose. Every
///         <c>{Binding}</c> then produces no value and each property keeps its
///         default — the harshest layout case (empty labels, everything
///         visible), and it needs no service graph. <c>{localization:Loc}</c> is
///         a markup extension, not a binding, so real localized text is still
///         present. What is under test is the LAYOUT, not the data.
///     </para>
///     <para>
///         Each session-backed case is ONE <c>[Fact]</c> that loops over the
///         dialogs inside a SINGLE <c>Dispatch</c>, deliberately not a
///         <c>[Theory]</c> per dialog: every dispatch can re-enter
///         <c>HeadlessUnitTestSession.EnsureIsolatedApplication</c> on a
///         different pool thread, and past ~60 of them the second thread to
///         touch the compositor throws "a different thread owns it". Every
///         assertion message names the dialog, so granularity is not lost.
///     </para>
///     <para>
///         Named <c>*UiPerfSmokeTests</c> because it needs the real Avalonia app
///         session, which cannot share a testhost with the gate and is excluded from
///         the default gate. Run it by hand after touching any dialog view.
///     </para>
///     <para>
///         In the <c>"HeadlessUI"</c> collection since 2026-08-23. It used to be
///         deliberately OUT of it, because <c>HeadlessSessionLifecycle</c>'s
///         disposal "tears the shared session down and hangs a filtered run" —
///         true of the synchronous <c>Dispose()</c>, dead since <c>303b2682</c>
///         made it <c>IAsyncDisposable</c>. Staying out was the worse bug: a
///         class outside the collection runs in PARALLEL with it and poisons
///         the process-global dispatcher for the whole run.
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed partial class DialogConstructionUiPerfSmokeTests
{
    private const double HostWidth = 1280;
    private const double HostHeight = 900;

    /// <summary>Half the point of the rework: inside one dialog, every row's
    /// control ends on ONE right edge. Measured rather than reviewed by eye,
    /// because a single row that forgets the shared column set is invisible in
    /// a diff and obvious in a screenshot.</summary>
    private const double EdgeTolerance = 1.0;

    /// <summary>
    ///     The dialogs converted onto <c>DialogStyles.axaml</c>. Everything in
    ///     this list must satisfy the full grammar (scrim → card → head → rows →
    ///     footer); the rest are only proven to build, attach and measure.
    ///     <para>
    ///         Move a name here in the same change that converts it — that is
    ///         what makes the grammar checks apply to it.
    ///     </para>
    /// </summary>
    private static readonly string[] Converted =
    [
        nameof(MessageDialogView),
        // The first-run wizard: eight pages, ONE shared footer whose primary
        // changes face (all pages show at once without a DataContext).
        nameof(OnboardingWizardView)
    ];

    /// <summary>Every LEGACY dialog view in Views/Dialogs that lives in the overlay
    /// host. The standalone <c>Window</c> (Diagnostics) is covered
    /// separately — a Window cannot be parented into a Panel — and the dialogs
    /// on <c>DialogHost</c> are DERIVED from the screen catalog
    /// (<see cref="HostedDialogs" />, the <c>.Hosted.cs</c> partial).</summary>
    private static readonly string[] AllOverlayDialogs =
    [
        nameof(MessageDialogView),
        nameof(OnboardingWizardView)
    ];

    private static Control CreateDialog(string name) => name switch
    {
        nameof(MessageDialogView) => new MessageDialogView(),
        nameof(OnboardingWizardView) => new OnboardingWizardView(),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "unknown dialog view")
    };

    // ── assertions ────────────────────────────────────────────────────────

    private static void AssertShell(string name, Control dialog)
    {
        if (Descendants<Grid>(dialog).FirstOrDefault(g => g.Classes.Contains("DialogFrame")) is { } frame)
        {
            // A hosted dialog: no backdrop of its own — the host's one shared
            // scrim sits under it — and the frame centres the card.
            Assert.True(frame.Background is null, $"{name}'s DialogFrame paints a backdrop; the host owns the scrim");
            var hostScrim = dialog.GetLogicalParent() is DialogHost host ? host.Scrim : null;
            Assert.True(hostScrim is { IsVisible: true, Background: not null },
                $"{name} opened without the host's shared scrim under it");
        }
        else
        {
            var scrim = Descendants<Grid>(dialog).FirstOrDefault(g => g.Classes.Contains("DialogScrim"));
            Assert.True(scrim is not null, $"{name} has no Grid.DialogScrim root");
            Assert.True(scrim!.Background is not null, $"{name}'s scrim resolved no OverlayScrim brush");
        }

        var card = Descendants<Border>(dialog).FirstOrDefault(b => b.Classes.Contains("DialogCard"));
        Assert.True(card is not null, $"{name} has no Border.DialogCard");
        Assert.True(card!.Bounds.Width > 1 && card.Bounds.Height > 1,
            $"{name}'s card laid out {card.Bounds} — its content measured nothing");

        var head = Descendants<Grid>(dialog).FirstOrDefault(g => g.Classes.Contains("DialogHead"));
        Assert.True(head is not null, $"{name} has no Grid.DialogHead");
        Assert.True(head!.Bounds.Height > 1,
            $"{name}'s head laid out {head.Bounds} — the title measured nothing");

        var badges = Descendants<Border>(dialog).Where(b => b.Classes.Contains("DialogIconBox")).ToList();
        Assert.True(badges.Count > 0, $"{name} has no Border.DialogIconBox in its head");
        foreach (var badge in badges)
        {
            Assert.True(badge.Background is not null,
                $"a DialogIconBox on {name} resolved no background brush");
            var icon = Descendants<PathIcon>(badge).FirstOrDefault(p => p.Classes.Contains("DialogIcon"));
            Assert.True(icon is not null, $"a DialogIconBox on {name} holds no PathIcon.DialogIcon");
            Assert.True(icon!.Data is not null,
                $"a head icon on {name} resolved no geometry — its DynamicResource key is wrong");
        }

        Assert.True(Descendants<TextBlock>(dialog).Any(t => t.Classes.Contains("DialogTitle")),
            $"{name} has no TextBlock.DialogTitle");
    }

    private static void AssertRows(string name, Control dialog)
    {
        var rows = Descendants<Grid>(dialog)
            .Where(g => g.Classes.Contains("DlgRow") && g.IsEffectivelyVisible)
            .ToList();

        foreach (var row in rows)
        {
            Assert.True(row.Bounds.Height >= 30,
                $"a row on {name} laid out {row.Bounds.Height:0.#} px tall — below the shared DlgRow rhythm");

            var control = row.Children.FirstOrDefault(c => Grid.GetColumn(c) == 1 && c.IsEffectivelyVisible);
            if (control == null) continue; // full-width row

            var gap = row.Bounds.Width - (control.Bounds.X + control.Bounds.Width);
            Assert.True(Math.Abs(gap) <= EdgeTolerance,
                $"a row on {name} left {gap:0.#} px between its control and the row's right edge — " +
                "the dialog's controls no longer share one right edge");
        }

        // Right-alignment alone would pass the check above even from a row that
        // kept an old, narrower split — so measure the column itself.
        var columnWidths = rows
            .Where(r => r.ColumnDefinitions.Count == 2)
            .Select(r => Math.Round(r.ColumnDefinitions[1].ActualWidth))
            .Distinct()
            .ToList();
        Assert.True(columnWidths.Count <= 1,
            $"{name} lays its rows out over {columnWidths.Count} different control-column widths " +
            $"({string.Join(", ", columnWidths)}) — they must all share one");
    }

    private static void AssertFooter(string name, Control dialog)
    {
        // A wizard carries one footer per step; the one to check is the step on screen.
        var footer = Descendants<Border>(dialog).Where(b => b.Classes.Contains("DialogFooter"))
            .OrderByDescending(b => b.IsEffectivelyVisible).FirstOrDefault();
        Assert.True(footer is not null, $"{name} has no Border.DialogFooter");
        Assert.True(footer!.BorderBrush is not null, $"{name}'s footer resolved no hairline brush");
        Assert.True(footer.Bounds.Height > 1,
            $"{name}'s footer laid out {footer.Bounds} — its actions measured nothing");

        var buttons = Descendants<Button>(footer).Where(b => b.IsEffectivelyVisible).ToList();
        Assert.True(buttons.Count > 0, $"{name}'s footer holds no visible button");
        foreach (var button in buttons)
        {
            Assert.True(button.Bounds.Width > 1 && button.Bounds.Height > 1,
                $"a footer button on {name} laid out {button.Bounds} — it rendered nothing");
        }

        var primaries = buttons.Count(b => b.Classes.Contains("Primary"));
        Assert.True(primaries <= 1,
            $"{name}'s footer shows {primaries} Button.Primary — a dialog has exactly one primary action");

        // The primary is LAST in the bar: the eye lands on the rightmost button,
        // so anything to its right reads as the default.
        // Positions are translated into the FOOTER's own space — a footer with a
        // left slot and a right slot has two StackPanels, and `Bounds.X` is
        // relative to each one, so comparing raw Bounds across them made the
        // first button of the left cluster tie with (and beat) the primary.
        if (primaries != 1) return;
        var primary = buttons.Single(b => b.Classes.Contains("Primary"));
        var rightmost = buttons.OrderByDescending(RightEdgeIn).First();
        Assert.True(ReferenceEquals(primary, rightmost),
            $"{name}'s primary action is not the rightmost footer button");

        double RightEdgeIn(Button b) =>
            (b.TranslatePoint(new Point(b.Bounds.Width, 0), footer!) ?? default).X;
    }

    private static void AssertTints(string name, Control dialog)
    {
        foreach (var strip in Descendants<Border>(dialog).Where(b => b.Classes.Contains("DlgStrip")))
        {
            Assert.True(strip.Background is not null, $"a DlgStrip on {name} resolved no fill brush");
            Assert.True(strip.BorderBrush is not null, $"a DlgStrip on {name} resolved no bar brush");
        }

        foreach (var frame in Descendants<Border>(dialog).Where(b => b.Classes.Contains("DlgListFrame")))
        {
            Assert.True(frame.Background is not null && frame.BorderBrush is not null,
                $"a DlgListFrame on {name} resolved no brushes");
        }

        foreach (var section in Descendants<Border>(dialog).Where(b => b.Classes.Contains("DlgSection")))
        {
            Assert.True(section.Background is not null && section.BorderBrush is not null,
                $"a DlgSection on {name} resolved no brushes");
        }

        // Any PathIcon anywhere in a converted dialog: a null Data means its
        // geometry key does not exist in Icons.axaml.
        foreach (var icon in Descendants<PathIcon>(dialog))
        {
            Assert.True(icon.Data is not null,
                $"a PathIcon on {name} resolved no geometry — its DynamicResource key is wrong");
        }
    }

    // ── plumbing ──────────────────────────────────────────────────────────

    /// <summary>Every view held to the dialog grammar. <c>Views/Diagnostics</c>
    /// joins <c>Views/Dialogs</c> because Session Health carries the grammar's
    /// own StyleInclude — a folder the lint could not see would be outside the
    /// "who may re-include it" rule entirely, which is how a fourth includer
    /// arrives unnoticed.</summary>
    private static IEnumerable<string> DialogAxamlFiles() =>
        new[] { "Dialogs", "Diagnostics" }
            .SelectMany(folder => UiSourceTree.EnumerateFiles(
                UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views", folder)), "*.axaml"))
            .Where(f => !string.Equals(Path.GetFileName(f), "DialogStyles.axaml", StringComparison.Ordinal));

    private static IEnumerable<T> Descendants<T>(Control root) where T : Control =>
        root.GetLogicalDescendants().OfType<T>();

    /// <summary>
    ///     Hosts the dialog where it actually lives: inside a real
    ///     <see cref="DialogContainerView" />, which is the single place
    ///     <c>DialogStyles.axaml</c> is included. Building the style include by
    ///     hand here would prove the STYLE FILE and nothing about the wiring —
    ///     and the wiring is what a future refactor breaks.
    ///     <para>
    ///         The host's eager <see cref="MessageDialogView" /> is removed
    ///         first so exactly one dialog is under test.
    ///     </para>
    /// </summary>
    private static void WithLaidOutDialog(Control dialog, Action assert)
    {
        var host = new DialogContainerView();
        host.DialogPanel.Children.Clear();
        host.DialogPanel.Children.Add(dialog);

        var window = new Window { Width = HostWidth, Height = HostHeight, Content = host };
        // Show() is what roots the tree and raises OnAttachedToLogicalTree. The
        // layout pass is driven through the VIEW, not the window: measuring a
        // headless Window directly reports "Invalid size returned for Measure".
        window.Show();
        try
        {
            host.Measure(new Size(HostWidth, HostHeight));
            host.Arrange(new Rect(0, 0, HostWidth, HostHeight));
            assert();
        }
        finally
        {
            window.Close();
        }
    }
}
