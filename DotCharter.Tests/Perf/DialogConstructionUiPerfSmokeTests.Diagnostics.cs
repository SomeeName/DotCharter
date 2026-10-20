// The Views/Diagnostics surfaces held to the same dialog grammar: Session Health's status pills and stacked startup bar, and the About box's build lockup, fact cards and licence halves.

using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using DotCharter.UI;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

public sealed partial class DialogConstructionUiPerfSmokeTests
{
    /// <summary>
    ///     Session Health, with the real registered view model behind it: four
    ///     status pills that resolved a tint, the startup bar's SEVEN segments
    ///     laid out to their phase weights, and the footer naming one thing.
    ///     <para>
    ///         The bar is the part a bounds check cannot take on trust.
    ///         <c>Grid.ColumnDefinitions</c> is a plain CLR property, so the
    ///         obvious <c>{Binding}</c> onto it compiles and does nothing —
    ///         seven equal segments, every startup looking identical, no warning
    ///         anywhere. Measuring that the widest segment is the longest PHASE
    ///         is the only thing that tells the two apart.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task SessionHealth_ShowsStatusPills_AStackedStartupBar_AndOneFinding()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            // Phases the app itself would publish at window-open. The window
            // ctor is deliberately the LONGEST, so the bar has a clear winner.
            DotCharter.UI.Diagnostics.StartupDiagnostics.SetPhases(
                107, 311, 693, 718, 1605, 1903, 2210, 4108);

            var vm = App.Services!.GetRequiredService<
                DotCharter.UI.ViewModels.Diagnostics.DiagnosticsViewModel>();
            var window = new DotCharter.UI.Views.Diagnostics.DiagnosticsWindow(vm);
            window.Show();
            try
            {
                var content = Assert.IsAssignableFrom<Control>(window.Content);
                content.Measure(new Size(1060, 780));
                content.Arrange(new Rect(0, 0, 1060, 780));

                // Four cards, each with a pill that resolved a tint.
                foreach (var card in new[] { "AudioCard", "RenderCard", "RuntimeCard", "StartupCard" })
                {
                    var border = Descendants<Border>(content).FirstOrDefault(b => b.Name == card);
                    Assert.True(border is not null, $"Session Health has no {card}");
                    Assert.True(border!.Bounds.Width > 1 && border.Bounds.Height > 1,
                        $"{card} laid out {border.Bounds} — it rendered nothing");

                    var pill = Descendants<Border>(border)
                        .FirstOrDefault(b => b.Classes.Contains("FireStatusPill"));
                    Assert.True(pill is not null, $"{card} carries no status pill");
                    Assert.True(pill!.Background is not null && pill.BorderBrush is not null,
                        $"{card}'s status pill resolved no tint");
                    Assert.True(pill.Bounds.Width > 1, $"{card}'s status pill laid out {pill.Bounds}");
                    Assert.False(string.IsNullOrWhiteSpace(
                            Descendants<TextBlock>(pill).FirstOrDefault()?.Text),
                        $"{card}'s status pill says nothing");
                }

                // The stacked bar: seven segments, sized by their PHASE.
                var bar = Descendants<Grid>(content).FirstOrDefault(g => g.Name == "StartupBar");
                Assert.True(bar is not null, "Session Health has no StartupBar");
                Assert.Equal(7, bar!.ColumnDefinitions.Count);
                var segments = bar.Children.OfType<Border>()
                    .Where(b => b.Classes.Contains("DiagPhase")).ToList();
                Assert.Equal(7, segments.Count);
                Assert.All(segments, s => Assert.True(s.Bounds.Height > 1,
                    $"a startup segment laid out {s.Bounds} — the bar has no height"));

                // Window ctor (1605-718 = 887 ms) is the longest named phase, so
                // its segment must be the widest of the six. Equal widths mean
                // the weights never reached the grid.
                var named = segments.Take(6).Select(s => s.Bounds.Width).ToList();
                Assert.True(named.Distinct().Count() > 1,
                    "every startup segment is the same width — the phase weights never reached the bar");
                Assert.Equal(named.Max(), named[4], 1.0);

                // …and the footer says exactly one thing.
                var finding = Descendants<TextBlock>(content).FirstOrDefault(t => t.Name == "HeadlineFinding");
                Assert.True(finding is not null, "Session Health has no footer finding");
                Assert.False(string.IsNullOrWhiteSpace(finding!.Text),
                    "the footer finding is blank — the dashboard names nothing to look at");
                Assert.True(finding.Bounds.Height > 1, $"the footer finding laid out {finding.Bounds}");
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    /// <summary>
    ///     At the size it opens at, nothing in Session Health is drawn under its
    ///     footer, and each path is one line. The Paths card sat in the unscrolled
    ///     right column, wrapped each path over three lines and ran under
    ///     "Uptime · Read-only" (seen live, 2026-09-24).
    /// </summary>
    [Fact]
    public async Task SessionHealth_AtItsOpeningSize_NothingRunsUnderTheFooter_AndEachPathIsOneLine()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var vm = App.Services!.GetRequiredService<
                DotCharter.UI.ViewModels.Diagnostics.DiagnosticsViewModel>();
            var window = new DotCharter.UI.Views.Diagnostics.DiagnosticsWindow(vm);
            window.Show();
            try
            {
                var content = Assert.IsAssignableFrom<Control>(window.Content);
                var size = new Size(window.Width, window.Height);
                content.Measure(size);
                content.Arrange(new Rect(size));

                var footer = Descendants<Border>(content).Single(b => b.Classes.Contains("DialogFooter"));
                var footerTop = footer.TranslatePoint(default, content)!.Value.Y;
                var unscrolled = Descendants<Border>(content)
                    .Where(b => b.Classes.Contains("DlgSection") && !Ancestors(b).OfType<ScrollViewer>().Any())
                    .ToList();
                Assert.True(unscrolled.Count >= 2, $"only {unscrolled.Count} cards outside a scroller — the check is vacuous");
                foreach (var card in unscrolled)
                {
                    var bottom = card.TranslatePoint(new Point(0, card.Bounds.Height), content)!.Value.Y;
                    Assert.True(bottom <= footerTop + 0.5,
                        $"{card.Name} ends at {bottom:0} px, under the footer at {footerTop:0} px");
                }

                var shown = new[] { vm.ConfigPath, vm.LogPath, vm.ActiveLogFile };
                var paths = Descendants<SelectableTextBlock>(content).Where(t => shown.Contains(t.Text)).ToList();
                Assert.True(paths.Count >= 3, $"only {paths.Count} of the three paths are on screen");
                foreach (var path in paths)
                {
                    Assert.False(string.IsNullOrEmpty(path.Text), "a path row is empty");
                    Assert.True(path.Bounds.Height < path.FontSize * 2,
                        $"'{path.Text}' laid out {path.Bounds.Height:0} px tall at {path.FontSize:0} px type — it wrapped");
                }
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    /// <summary>A log message gets the log's width, not what four side-by-side columns
    /// leave of it: in the right column that was about 70 px, a word a line.</summary>
    [Fact]
    public async Task SessionHealth_ALogMessage_GetsTheLogsWidth()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var vm = App.Services!.GetRequiredService<
                DotCharter.UI.ViewModels.Diagnostics.DiagnosticsViewModel>();
            var window = new DotCharter.UI.Views.Diagnostics.DiagnosticsWindow(vm);
            window.Show();
            try
            {
                var content = Assert.IsAssignableFrom<Control>(window.Content);
                var size = new Size(window.Width, window.Height);
                content.Measure(size);
                content.Arrange(new Rect(size));

                // The headless app writes no log file, so the panel's own row
                // template is built for one entry, in a slot as wide as the well.
                var well = Descendants<Border>(content).Single(b => b.Classes.Contains("DiagLogWell"));
                Assert.True(well.Bounds.Width > 100, $"the log well laid out {well.Bounds}");
                var template = Descendants<ItemsControl>(well).Single().ItemTemplate!;
                var entry = new DotCharter.UI.ViewModels.Diagnostics.DiagnosticsLogEntry(
                    "05:21:28.145", DotCharter.UI.ViewModels.Diagnostics.LogSeverity.Info, "Import",
                    "PSARC library scan completed. Root='C:\\Songs'");
                var row = template.Build(entry)!;
                row.DataContext = entry;
                var slot = new Border { Width = well.Bounds.Width - well.Padding.Left - well.Padding.Right, Child = row };
                well.Child = slot;
                slot.Measure(new Size(slot.Width, double.PositiveInfinity));
                slot.Arrange(new Rect(slot.DesiredSize));

                var message = Descendants<TextBlock>(row).Single(t => t.Text == entry.Message);
                Assert.True(message.Bounds.Width >= slot.Width * 0.9,
                    $"the log message has {message.Bounds.Width:0} of the log's {slot.Width:0} px");
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    /// <summary>Help ▸ Session health asked twice is one window, brought forward
    /// (three clicks used to leave three), and a closed one opens fresh.</summary>
    [Fact]
    public async Task SessionHealth_AskedForTwice_IsOneWindow()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var service = new DotCharter.UI.Services.DiagnosticsDialogService(() =>
                App.Services!.GetRequiredService<DotCharter.UI.ViewModels.Diagnostics.DiagnosticsViewModel>());
            service.ShowDiagnostics();
            var first = service.Open;
            Assert.NotNull(first);
            try
            {
                service.ShowDiagnostics();
                Assert.Same(first, service.Open);
            }
            finally
            {
                first!.Close();
            }

            Assert.Null(service.Open);
        }, CancellationToken.None);
    }

    private static IEnumerable<Visual> Ancestors(Visual visual)
    {
        for (var v = visual.GetVisualParent(); v is not null; v = v.GetVisualParent()) yield return v;
    }

    /// <summary>
    ///     About: the build LOCKUP and the four fact cards, plus the one thing
    ///     on that surface that can be WRONG rather than merely ugly.
    ///     <para>
    ///         The licence statement has two halves and both are load-bearing:
    ///         DotCharter is GPL-3.0-or-later (BSD 3-Clause until v0.50.0), and the shipped audio
    ///         engine statically links JUCE + Rubber Band + the VST 3 SDK, so the
    ///         BINARY carries their GPLv3 / commercial dual terms. Dropping
    ///         either half for brevity produces a licence claim that is false.
    ///         The tokens are proper nouns, so the check survives translation —
    ///         which is the point: a German build must not be able to say less.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task About_ShowsTheBuildLockup_FourFactCards_AndBothHalvesOfTheLicence()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var entry = HostedDialogs.Single(e =>
                e.Contract == typeof(DotCharter.Presentation.Contracts.Dialogs.IAboutScreen));
            WithHostedDialog(entry, DotCharter.Presentation.Fakes.FakeVariantNames.Default, (content, _) =>
            {
                var lockup = Descendants<Grid>(content).FirstOrDefault(g => g.Name == "BuildLockup");
                Assert.True(lockup is not null, "About has no build lockup");
                Assert.True(lockup!.Bounds.Width > 1 && lockup.Bounds.Height > 1,
                    $"the build lockup laid out {lockup.Bounds} — it rendered nothing");
                var mark = Descendants<Image>(lockup).FirstOrDefault();
                Assert.True(mark?.Source is not null, "the product mark resolved no image");
                var version = Descendants<TextBlock>(lockup).FirstOrDefault(t => t.Name == "VersionText");
                Assert.False(string.IsNullOrWhiteSpace(version?.Text),
                    "the lockup prints no version — a bug report cannot name this build");

                var cards = Descendants<Grid>(content).FirstOrDefault(g => g.Name == "AboutCards");
                Assert.True(cards is not null, "About has no fact cards");
                var sections = Descendants<Border>(cards!)
                    .Where(b => b.Classes.Contains("DlgSection")).ToList();
                Assert.Equal(4, sections.Count);
                foreach (var section in sections)
                {
                    Assert.True(section.Bounds.Width > 1 && section.Bounds.Height > 1,
                        $"an About card laid out {section.Bounds} — it rendered nothing");
                    Assert.True(section.Background is not null && section.BorderBrush is not null,
                        "an About card resolved no brushes — DlgSection did not apply");
                    Assert.True(Descendants<TextBlock>(section)
                            .Any(t => t.Classes.Contains("DlgSectionLabel") && !string.IsNullOrWhiteSpace(t.Text)),
                        "an About card has no eyebrow — it does not say what it is about");
                }

                var licence = string.Concat(Descendants<TextBlock>(cards!)
                    .Where(t => t.Classes.Contains("DlgBody"))
                    .Select(t => t.Text));
                // Both halves: the project's own licence (GPL v3 or later since
                // v0.50.0) and the engine's linked libraries under the same terms.
                Assert.Contains("GPL v3", licence, StringComparison.Ordinal);
                Assert.Contains("JUCE", licence, StringComparison.Ordinal);
                // …and the libraries this app has not shipped since 0.10 must not
                // come back. The mockup still names them.
                Assert.DoesNotContain("BASS", licence, StringComparison.Ordinal);
                Assert.DoesNotContain("OwnAudio", licence, StringComparison.Ordinal);
            });
        }, CancellationToken.None);
    }
}
