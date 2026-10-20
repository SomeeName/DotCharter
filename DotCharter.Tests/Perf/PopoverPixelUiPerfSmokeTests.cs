using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using DotCharter.Tone.UI.Controls;
using DotCharter.Tone.UI.ViewModels.Editor;
using DotCharter.UI;
using DotCharter.UI.ViewModels.Application;
using DotCharter.UI.ViewModels.Shell;
using DotCharter.UI.Views.Shell.Popovers;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     Renders every popover to a PNG, at the width the mockup draws it, on the
///     mockup's own page ground — so the result can be put beside
///     `Play Menus.dc.html` and COMPARED.
/// </summary>
/// <remarks>
///     <para>
///         <b>This exists because the first attempt at these panels was written
///         and never looked at.</b> It shipped a shell that measured, passed six
///         assertions, and did not resemble the drawing — which no bounds check
///         can catch, because "wrong colour", "wrong gradient", "wrong tracking"
///         and "a 26 px chip where a 34 px select belongs" all measure fine.
///     </para>
///     <para>
///         It reuses <see cref="UiSweepRunner" />'s machinery rather than
///         inventing a second one: the same <c>DOTCHARTER_UI_SWEEP_DUMP=1</c>
///         switch puts the shared headless session on Skia (the only mode in
///         which <c>CaptureRenderedFrame</c> returns anything), the same
///         <c>TestResults</c> root, and the same contact-sheet idea — one
///         <c>index.html</c> with every panel on it.
///     </para>
///     <para>
///         Without that variable the captures are skipped and the ASSERTIONS
///         still run: each surface must construct, attach, lay out and measure
///         non-zero at the mockup's width. That half is the regression test; the
///         pictures are the review tool.
///     </para>
///     <para>
///         Every popover is its own VIEW, which is what makes this possible at
///         all. While they were markup nested inside <c>ToolbarView</c> they were
///         not types anything could name, so they could not be constructed, could
///         not be hosted twice, and could not be rendered.
///     </para>
/// </remarks>
[Collection("HeadlessUI")]
public sealed class PopoverPixelUiPerfSmokeTests
{
    /// <summary>The mockup's page ground: <c>#08070e</c>, so a capture shows the
    /// card's own edge against what it actually sits on.</summary>
    private static readonly Color PageGround = Color.FromRgb(0x08, 0x07, 0x0E);

    /// <summary>Room around the card, so its 1 px edge is not the first
    /// pixel column.</summary>
    private const double Inset = 24;

    /// <summary>Where the captures land. A sibling of the UI sweep's own
    /// directory, under the same <c>TestResults</c> root.</summary>
    private static string ArtifactDirectory =>
        Path.Combine(AppContext.BaseDirectory, "TestResults", "popover-pixels");

    /// <summary>
    ///     Every surface the owner asked to see, at the width `Play Menus.dc.html`
    ///     draws it. The widths are ASSERTED, not decoration: a popover that
    ///     silently grew is the first thing that stops it being the drawing.
    /// </summary>
    public static TheoryData<string, double> Surfaces => new()
    {
        { "mix", 344 },
        { "tempo", 344 },
        // 420 (the FirePopoverMaxWidth cap), not the drawing's 400: at 400 the drop-tuning field read "0 semi…" (2026-09-24).
        { "tools", 420 },
        { "tracks", 376 },
        { "tracks-open", 376 },
        { "undo", 348 },
        { "redo", 348 },
        { "profile", 320 },
        // Not in `Play Menus.dc.html` (it postdates the mockup): the toolbar
        // overflow (P2-16), stated at its own drawn width like the rest.
        { "overflow", 320 }
    };

    [Theory]
    [MemberData(nameof(Surfaces))]
    public Task EveryPopover_laysOutAtTheMockupsWidth_andIsCaptured(string id, double width) =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var services = App.Services
                           ?? throw new InvalidOperationException("the headless app session did not boot.");

            // A popover drawn against an empty app is a popover of empty states,
            // and the mockup's specimens are all FULL — three arrangements in the
            // picker, three plugins in the chain. Seeding the same real
            // multi-arrangement project the UI sweep uses is what makes the
            // capture comparable to the drawing at all.
            Seed(services);

            var view = Build(id, services);

            // THROUGH A REAL FlyoutPresenter, which is what the app puts a
            // popover inside. The first version of this harness set
            // `Window.Content = popover` directly — a SURROGATE host — and that
            // is precisely why it rendered a clean thumb while the product
            // clipped one: the presenter and the popover card carry their own
            // ClipToBounds, and a plain Window carries neither. A capture from a
            // host the product does not use cannot prove anything about the
            // product.
            var presenter = new FlyoutPresenter { Content = view };
            var host = new Border
            {
                Background = new SolidColorBrush(PageGround),
                Padding = new Thickness(Inset),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
                Child = presenter
            };

            // Generous first, then trimmed to what the card actually needs, so
            // the capture is the panel and not a field of black.
            var window = new Window
            {
                Width = width + 2 * Inset,
                Height = 1000,
                Background = new SolidColorBrush(PageGround),
                Content = host
            };
            window.Show();
            try
            {
                host.Measure(new Size(width + 2 * Inset, double.PositiveInfinity));
                var desired = host.DesiredSize;

                window.Height = Math.Ceiling(desired.Height);
                window.UpdateLayout();
                host.Measure(new Size(width + 2 * Inset, window.Height));
                host.Arrange(new Rect(0, 0, window.Width, window.Height));
                window.UpdateLayout();

                // AFTER the layout, because attaching the Tools panel starts
                // tuner monitoring and that RESETS the reading — seeding it up
                // front is wiped before the frame is captured.
                SeedTunerReading(services);
                window.UpdateLayout();

                var popover = view.GetSelfAndVisualDescendants().OfType<FirePopover>().FirstOrDefault();
                Assert.True(popover != null,
                    $"'{id}' is not built on the shared FirePopover shell");

                Assert.True(popover!.Bounds is { Width: > 1, Height: > 1 },
                    $"'{id}' laid out {popover.Bounds} — it rendered nothing, which is what a failed " +
                    "DynamicResource, a missing ControlTheme and a Space* token in a Padding all look like");

                // The mockup's width, within a pixel. A popover that grew is no
                // longer the panel that was drawn.
                Assert.Equal(width, popover.Bounds.Width, 1);
                Assert.True(popover.Bounds.Width <= 420 + 1,
                    $"'{id}' is {popover.Bounds.Width:0.#} px wide — the SHELL RULES cap a popover at 420");

                // The head is the one part every popover shares, so it is the
                // one measurement that proves the shell resolved at all.
                var head = popover.GetVisualDescendants().OfType<Border>()
                    .FirstOrDefault(b => b.Name == "PART_Head");
                Assert.True(head is { Bounds.Height: > 1 },
                    $"'{id}' has no laid-out head — its ControlTheme did not resolve");
                Assert.Equal(40, head!.Bounds.Height, 1);

                Capture(window, id, popover.Bounds.Height);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);

    /// <summary>
    ///     A CLOSED popover holds no clock.
    ///     <para>
    ///         Two panels on this rail run something live while they are up, and
    ///         both gate it on ATTACH — deliberately, because
    ///         <c>MenuFlyoutPrewarmer</c> builds the popup at boot and then
    ///         CANCELS its own open, so the content is never parented and never
    ///         attached. <c>MixPopoverView</c> drives a 30 Hz input-meter poll of
    ///         the audio engine; <c>ToolsPopoverView</c> holds a ~15 Hz tuner
    ///         monitor on the live input. Left running for a shut menu they poll
    ///         the engine for the whole session.
    ///     </para>
    ///     <para>
    ///         Both views EXPOSE a seam for exactly this claim
    ///         (<c>IsMeterRunning</c>, <c>IsMonitoringTuner</c>) and nothing
    ///         asserted it, which is the shape of a guard that was never a guard.
    ///         Detach is the real close: the popup's presenter is torn out of the
    ///         tree, and that is what raises <c>DetachedFromVisualTree</c>.
    ///     </para>
    /// </summary>
    [Fact]
    public Task AClosedPopover_HoldsNoClock() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var services = App.Services
                           ?? throw new InvalidOperationException("the headless app session did not boot.");
            // The tuner refuses to monitor without an arrangement to tune
            // against, so the real project has to be in place first — otherwise
            // the "open" half of this case would pass for the wrong reason.
            Seed(services);
            var shell = services.GetRequiredService<MainWindowViewModel>();

            var mix = new MixPopoverView { DataContext = shell };
            var tools = new ToolsPopoverView { DataContext = shell };
            var host = new StackPanel();
            host.Children.Add(mix);
            host.Children.Add(tools);

            var window = new Window { Width = 420, Height = 900, Content = host };
            window.Show();
            try
            {
                host.Measure(new Size(420, 900));
                host.Arrange(new Rect(0, 0, 420, 900));
                window.UpdateLayout();

                Assert.True(mix.IsMeterRunning,
                    "the Mix panel is on screen and its input-level meter is not running");
                Assert.True(tools.IsMonitoringTuner,
                    "the Tools panel is on screen and it is holding no tuner monitor — the IN TUNE " +
                    "row would keep its em dash forever");

                // Closing a popover detaches its content; nothing else about the
                // view changes.
                host.Children.Clear();
                window.UpdateLayout();

                Assert.False(mix.IsMeterRunning,
                    "the Mix meter clock is still running for a SHUT panel — it polls the audio " +
                    "engine at 30 Hz for the rest of the session");
                Assert.False(tools.IsMonitoringTuner,
                    "the Tools panel is still holding a tuner monitor for a SHUT panel — the live " +
                    "input keeps being detected at ~15 Hz for the rest of the session");
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);

    // ── seeding ─────────────────────────────────────────────────────────────

    /// <summary>
    ///     Puts the sweep's real three-arrangement project into the shared
    ///     <c>ProjectState</c>, and three plugin slots into the guitar chain.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The slots go straight into the collection rather than through
    ///         <c>AddGuitarVstFromSelection</c>: that path persists to
    ///         <c>AppConfig</c> and pushes at the engine, and a screenshot
    ///         harness must not write the user's config. The rows it produces
    ///         are the real view models, which is all the markup sees.
    ///     </para>
    ///     <para>
    ///         Once per process. These are DI singletons, so a second seeding
    ///         would stack six plugins into one chain.
    ///     </para>
    /// </remarks>
    private static void Seed(IServiceProvider services)
    {
        // Through the SHELL, not the container: `TransportViewModel` is not a
        // registered service — `MainWindowViewModel` owns the one instance, and
        // that is the instance the popovers bind.
        var shell = services.GetService<MainWindowViewModel>();

        // Re-checked per surface rather than once, and that is not belt-and-
        // braces: the app's own startup finishes on a LATER dispatcher turn and
        // replaces the project (measured — the fixture survives exactly one
        // case), and the transport's fire-and-forget VST scan restores the
        // empty persisted chain over the top. Seeding once, up front, is
        // silently undone before the second capture.
        if (shell is null || shell.MultiTrackOverlay.AllEntries.Count == 0)
            UiSweepDataContexts.Build(services);

        var transport = shell?.Transport;
        if (transport is null || transport.GuitarVstChain.Count > 0) return;

        foreach (var (name, bypass) in new[]
                 {
                     ("TS-9 Overdrive", false), ("Neural Archetype", false), ("Valhalla Room", true)
                 })
        {
            var slot = new VstChainSlotViewModel(Guid.NewGuid(), name,
                $@"C:\Program Files\Common Files\VST3\{name}.vst3", bypass)
            {
                IsLoading = false,
                DisplayIndex = transport.GuitarVstChain.Count + 1
            };
            transport.GuitarVstChain.Add(slot);
        }
    }

    /// <summary>
    ///     Puts a real reading in front of the Tools panel's tuner row. A
    ///     headless run hears nothing, so the state that MATTERS — the mockup's
    ///     green <c>IN TUNE</c> — would otherwise never be rendered and its
    ///     styling would never be compared. The low string's exact target pitch
    ///     goes through the same entry the live detection feeds.
    /// </summary>
    private static void SeedTunerReading(IServiceProvider services)
    {
        if (services.GetService<DotCharter.Services.Audio.Tuning.LiveTuner>() is not { IsRunning: true } tuner) return;
        if (services.GetService<ProjectState>()?.ActiveArrangement is not { } arrangement) return;
        var targets = new DotCharter.Tone.Audio.Tuning.ArrangementTunerLogic();
        targets.Load(arrangement);
        tuner.Hear(targets.Targets[0].FrequencyHz, 66);
    }

    // ── building one surface ────────────────────────────────────────────────

    private static Control Build(string id, IServiceProvider services)
    {
        if (id == "profile")
        {
            var status = services.GetRequiredService<StatusBarViewModel>();
            return new ProfilePopoverView { DataContext = status };
        }

        var shell = services.GetRequiredService<MainWindowViewModel>();
        Control view = id switch
        {
            "mix" => new MixPopoverView(),
            "tempo" => new TempoPopoverView(),
            "tools" => new ToolsPopoverView(),
            "tracks" or "tracks-open" => new TracksPopoverView(),
            "undo" => new UndoPopoverView(),
            "redo" => new RedoPopoverView(),
            "overflow" => new OverflowPopoverView(),
            _ => throw new ArgumentOutOfRangeException(nameof(id), id, "unknown popover")
        };
        view.DataContext = shell;

        // The mockup draws Tracks TWICE — closed and with its arrangement list
        // open — because the open state is the one that proves the list is
        // INLINE (it pushes the rows below it down) rather than a popup.
        if (id == "tracks-open")
        {
            view.AttachedToVisualTree += (_, _) =>
            {
                if (view.FindControl<ToggleButton>("ArrangementSelect") is { } select)
                    select.IsChecked = true;
            };
        }

        return view;
    }

    // ── artifacts ───────────────────────────────────────────────────────────

    private static void Capture(Window window, string id, double cardHeight)
    {
        if (!UiSweepRunner.CaptureEnabled) return;

        Directory.CreateDirectory(ArtifactDirectory);
        try
        {
            var frame = HeadlessWindowExtensions.CaptureRenderedFrame(window);
            if (frame is null) return;

            frame.Save(Path.Combine(ArtifactDirectory, id + ".png"), new PngBitmapEncoderOptions());
            AppendToSheet(id, window.Width, cardHeight);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or IOException)
        {
            // A capture is a review aid, never the assertion. Losing one must
            // not turn a green layout into a red test.
        }
    }

    /// <summary>
    ///     The contact sheet, appended one row per surface. Each test method is
    ///     its own theory case and they share a process, so it accumulates rather
    ///     than being written once — the file is a review artifact, not an
    ///     assertion, so a partial run producing a partial sheet is correct.
    /// </summary>
    private static void AppendToSheet(string id, double width, double cardHeight)
    {
        var path = Path.Combine(ArtifactDirectory, "index.html");
        if (!File.Exists(path))
        {
            File.WriteAllText(path,
                "<!doctype html><meta charset=\"utf-8\"><title>Popovers vs Play Menus.dc.html</title>" +
                "<style>body{margin:0;padding:26px 30px;background:#08070e;color:#f4f0fb;" +
                "font-family:'Space Grotesk',system-ui,sans-serif}" +
                ".g{display:flex;flex-wrap:wrap;gap:22px;align-items:flex-start}" +
                ".c{display:flex;flex-direction:column;gap:9px}" +
                ".t{font-family:'IBM Plex Mono',monospace;font-size:9.5px;letter-spacing:.2em;color:#8b81a3}" +
                "img{display:block}</style>" +
                "<h1 style=\"font-size:20px;margin:0 0 18px\">Rendered popovers &mdash; compare against " +
                "<code>Play Menus.dc.html</code></h1><div class=\"g\">\n");
        }

        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture,
            $"<div class=\"c\"><div class=\"t\">{id.ToUpperInvariant()} &middot; {width:0} × {cardHeight:0}</div>");
        sb.Append(CultureInfo.InvariantCulture, $"<img src=\"{id}.png\" alt=\"{id}\"></div>\n");
        File.AppendAllText(path, sb.ToString());
    }
}
