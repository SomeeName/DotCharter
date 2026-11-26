using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Presentation.Fakes;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Linking;
using DotCharter.Tests.Presentation.Dialogs;
using DotCharter.Tests.Services.State.Linking;
using DotCharter.UI.Views.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The linked-measures dialogs (the import's summary and Tools ▸ Linked measures), built the way
///     the app builds them: a real <see cref="DialogHost" /> inside the real
///     <see cref="DialogContainerView" />, in a shown window, laid out. Every named part must MEASURE —
///     a view whose styles failed still arranges, with no size — on the real view models over a
///     linked chart and on every fake variant.
/// </summary>
[Collection("HeadlessUI")]
public sealed class LinkDialogsUiPerfSmokeTests
{
    private const double HostWidth = 1280;
    private const double HostHeight = 720;
    private static readonly int[] PatternRepeats = [0, 1, 2, 3, 1, 5, 6, 1, 8];

    [Fact]
    public async Task TheRealManager_ListsItsGroupsAndSuggestions_AndLaysOut()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var (edit, link) = Edit();
            link.Link((link.Lead, 1), (link.Lead, 4), (link.Rhythm, 1));
            var suggestions = LinkSuggestionFinder.Find(link.Project, [link.Lead, link.Rhythm], PatternRepeats, link.Rule);
            Assert.NotEmpty(suggestions);

            WithOpenDialog<ILinkGroupsScreen, bool>(() =>
            {
                var vm = new LinkGroupsViewModel(edit);
                vm.Configure(new LinkGroupsRequest(suggestions));
                return vm;
            }, view =>
            {
                AssertLaidOut("real", view, "LinkGroupList", "LinkSuggestionsSection", "LinkSelectedButton");
                var buttons = view.GetVisualDescendants().OfType<Button>().ToList();
                Assert.Contains(buttons, b => Equals(b.Content, "Lead · M5") && b.Bounds.Width > 1);
                Assert.Contains(view.GetVisualDescendants().OfType<CheckBox>(), c => c.IsChecked == true && c.Bounds.Width > 1);
            });
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TheManagerOpensWhileDetectionRunsOnAWorker()
    {
        using var entered = new ManualResetEventSlim();
        var ranOnUi = false;
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellation = TestContext.Current.CancellationToken;
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var (edit, _) = Edit();
            var vm = new LinkGroupsViewModel(edit, (_, _, token, _) =>
            {
                ranOnUi = Dispatcher.UIThread.CheckAccess();
                entered.Set();
                if (!ranOnUi) WaitHandle.WaitAny([token.WaitHandle, cancellation.WaitHandle]);
                stopped.TrySetResult();
                return [];
            });

            WithOpenDialog<ILinkGroupsScreen, bool>(() =>
            {
                vm.Configure(new LinkGroupsRequest([]));
                return vm;
            }, view =>
            {
                entered.Wait(cancellation);
                Assert.False(ranOnUi);
                AssertLaidOut("detecting", view, "LinkDetectionProgress", "LinkDetectionCancel");
                Assert.False(stopped.Task.IsCompleted);
                var cancelButton = Assert.Single(view.GetVisualDescendants().OfType<Button>(), b => b.Name == "LinkDetectionCancel");
                Assert.Same(vm.CancelDetectionCommand, cancelButton.Command);
                cancelButton.Command!.Execute(cancelButton.CommandParameter);
                Assert.False(vm.Result.IsCompleted);
            });
        }, cancellation);

        await stopped.Task.WaitAsync(cancellation);
    }

    [Fact]
    public async Task TheRealSummary_ShowsItsLines_AndLaysOut()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var (edit, link) = Edit();
            var suggestions = LinkSuggestionFinder.Find(link.Project, [link.Lead, link.Rhythm], PatternRepeats, link.Rule);

            WithOpenDialog<ILinkSuggestionsScreen, LinkSuggestionsChoice>(() =>
            {
                var vm = new LinkSuggestionsViewModel(edit);
                vm.Configure(new LinkSuggestionsRequest(suggestions));
                return vm;
            }, view =>
            {
                AssertLaidOut("real", view, "LinkSuggestBody", "LinkSuggestHeadline");
                Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(),
                    t => t.Text?.StartsWith("Lead and Rhythm share", StringComparison.Ordinal) == true && t.Bounds.Width > 1);
            });
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task EveryFakeVariant_LaysOut()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var managers = FakeScreens.VariantsOf(typeof(ILinkGroupsScreen));
            var summaries = FakeScreens.VariantsOf(typeof(ILinkSuggestionsScreen));
            Assert.True(managers.Count >= 3 && summaries.Count >= 3);
            foreach (var variant in managers)
            {
                WithOpenDialog<ILinkGroupsScreen, bool>(() => (ILinkGroupsScreen)variant.Create(), view =>
                {
                    if (variant.Name == FakeVariantNames.Error)
                        AssertLaidOut(variant.Name, view, "LinkDetectionStatus", "LinkDetectionRetry");
                    else AssertLaidOut(variant.Name, view);
                });
            }

            foreach (var variant in summaries)
            {
                WithOpenDialog<ILinkSuggestionsScreen, LinkSuggestionsChoice>(() => (ILinkSuggestionsScreen)variant.Create(),
                    view => AssertLaidOut(variant.Name, view, "LinkSuggestBody", "LinkSuggestHeadline"));
            }
        }, TestContext.Current.CancellationToken);
    }

    /// <summary>
    ///     #964: the two dialogs' lists with German and Japanese text, long group names and many
    ///     groups, in the 1280 px window and in a 640 px one (the card clamps to its window, so the
    ///     smaller host stands for the narrowest card). Nothing may overhang the card, overlap its
    ///     neighbour in a row, or be cut off without a tooltip.
    /// </summary>
    [Theory]
    [InlineData(1280)]
    [InlineData(640)]
    public async Task TheListRows_StayInsideTheCard_WithLongAndLocalizedText(int hostWidth) =>
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var offenders = new List<string>();
            foreach (var variant in FakeScreens.VariantsOf(typeof(ILinkGroupsScreen))
                         .Where(v => v.Name is FakeVariantNames.Default or FakeVariantNames.LongText or FakeVariantNames.Full))
            {
                WithOpenDialog<ILinkGroupsScreen, bool>(() => (ILinkGroupsScreen)variant.Create(),
                    view => AuditCard($"groups/{variant.Name}@{hostWidth}", view, offenders), hostWidth,
                    window => NarrowWidthLayout.Capture(window, $"link-groups-{variant.Name}-{hostWidth}"));
            }

            foreach (var variant in FakeScreens.VariantsOf(typeof(ILinkSuggestionsScreen)))
            {
                WithOpenDialog<ILinkSuggestionsScreen, LinkSuggestionsChoice>(() => (ILinkSuggestionsScreen)variant.Create(),
                    view => AuditCard($"suggestions/{variant.Name}@{hostWidth}", view, offenders), hostWidth,
                    window => NarrowWidthLayout.Capture(window, $"link-suggestions-{variant.Name}-{hostWidth}"));
            }

            Assert.True(offenders.Count == 0, $"({HeadlessSessionFixture.SweptCulture}) {string.Join("; ", offenders)} (#964).");
        }, TestContext.Current.CancellationToken);

    private static void AuditCard(string what, Control view, List<string> offenders)
    {
        var card = view.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("DialogCard"));
        var (cardLeft, cardRight) = NarrowWidthLayout.Span(card, view);
        NarrowWidthLayout.Report($"[narrow-width] {what}: card {cardLeft:0.#}..{cardRight:0.#}");
        foreach (var control in view.GetVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible && c.Bounds.Width > 1))
        {
            if (control is not (Button or TextBox or CheckBox or TextBlock)) continue;
            if (control is TextBlock && control.GetVisualAncestors().Any(a => a is Button or TextBox or CheckBox)) continue;
            var (left, right) = NarrowWidthLayout.Span(control, view);
            var name = control is TextBlock t ? $"text '{Shorten(t.Text)}'" : $"{control.GetType().Name} '{Shorten(ContentOf(control))}'";
            if (left < cardLeft - 0.5 || right > cardRight + 0.5)
            {
                offenders.Add($"{what}: {name} spans {left:0.#}..{right:0.#}, outside the card {cardLeft:0.#}..{cardRight:0.#}");
            }

            if (control is TextBlock trimmed && trimmed.TextTrimming != TextTrimming.None && !string.IsNullOrEmpty(trimmed.Text)
                && NarrowWidthLayout.NaturalWidth(trimmed) > trimmed.Bounds.Width + 1
                && ToolTip.GetTip(trimmed) is null)
            {
                offenders.Add($"{what}: {name} is trimmed ({NarrowWidthLayout.NaturalWidth(trimmed):0.#} > {trimmed.Bounds.Width:0.#} px) with no tooltip");
            }

            if (control is TextBox { Text.Length: > 0 } box
                && NarrowWidthLayout.NaturalWidth(box.Text!, box) > box.Bounds.Width - box.Padding.Left - box.Padding.Right + 1
                && ToolTip.GetTip(box) is null)
            {
                offenders.Add($"{what}: {name} is cut ({NarrowWidthLayout.NaturalWidth(box.Text!, box):0.#} > {box.Bounds.Width:0.#} px box) with no tooltip");
            }

            if (control is Button { Content: string label } button && label.Length > 0
                && NarrowWidthLayout.NaturalWidth(label, button) > button.Bounds.Width - button.Padding.Left - button.Padding.Right + 1
                && ToolTip.GetTip(button) is null)
            {
                offenders.Add($"{what}: {name} is cut ({NarrowWidthLayout.NaturalWidth(label, button):0.#} > {button.Bounds.Width:0.#} px button) with no tooltip");
            }
        }

        // a row of controls in one grid row must not overlap sideways (the group row: letter, name, Rename, Unlink)
        foreach (var row in view.GetVisualDescendants().OfType<Grid>().Where(g => g.ColumnDefinitions.Count >= 3 && g.IsEffectivelyVisible))
        {
            var parts = row.Children.OfType<Control>().Where(c => c.IsVisible && c.Bounds.Width > 1)
                .Select(c => (c, span: NarrowWidthLayout.Span(c, view))).OrderBy(p => p.span.Left).ToList();
            for (var i = 1; i < parts.Count; i++)
            {
                if (parts[i].span.Left < parts[i - 1].span.Right - 0.5)
                {
                    offenders.Add($"{what}: {parts[i - 1].c.GetType().Name} ({parts[i - 1].span.Left:0.#}..{parts[i - 1].span.Right:0.#}) " +
                                  $"overlaps {parts[i].c.GetType().Name} ({parts[i].span.Left:0.#}..{parts[i].span.Right:0.#})");
                }
            }
        }
    }

    private static string ContentOf(Control c) => c switch { ContentControl { Content: string s } => s, TextBox tb => tb.Text ?? "", _ => "" };

    private static string Shorten(string? text) => text is { Length: > 24 } ? text[..24] + "…" : text ?? "";

    private static (ChartEditDialogServices Edit, LinkHarness Link) Edit()
    {
        var link = LinkHarness.WithRhythm();
        var edit = new ChartEditDialogServices(RecordingProxy.Create<IChartEditorSurface>().Proxy, link.History, link.State,
            new FakeConfigurationService { AppConfig = link.Config }, FakeLocalization.English(), new FakeAppLogger());
        return (edit, link);
    }

    private static void AssertLaidOut(string variant, Control view, params string[] parts)
    {
        var card = view.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("DialogCard"));
        Assert.True(card is { Bounds.Width: > 1, Bounds.Height: > 1 }, $"{variant}: the card laid out {card?.Bounds}");
        Assert.True(card!.Bounds.Width <= HostWidth && card.Bounds.Height <= HostHeight,
            $"{variant}: the card ({card.Bounds}) does not fit a {HostWidth}x{HostHeight} window");
        foreach (var name in parts)
        {
            var part = view.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == name);
            Assert.True(part is { Bounds.Width: > 1, Bounds.Height: > 1 }, $"{variant}: {name} laid out {part?.Bounds}");
        }
    }

    private static void WithOpenDialog<TContract, TResult>(Func<TContract> create, Action<Control> assert,
        double hostWidth = HostWidth, Action<Window>? capture = null)
        where TContract : class, IDialogScreen<TResult>
    {
        var services = new ServiceCollection();
        services.AddSingleton<DialogsSlot>();
        services.AddScoped(_ => create());
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var host = new DialogHost(provider.GetRequiredService<IServiceScopeFactory>(), ScreenCatalog.Default,
            provider.GetRequiredService<DialogsSlot>());
        var container = new DialogContainerView();
        container.DialogPanel.Children.Clear();
        container.DialogPanel.Children.Add(host);
        var window = new Window { Width = hostWidth, Height = HostHeight, Content = container };
        window.Show();
        try
        {
            _ = ((IDialogs)host).ShowAsync<TContract, TResult>();
            Dispatcher.UIThread.RunJobs();
            container.Measure(new Size(hostWidth, HostHeight));
            container.Arrange(new Rect(0, 0, hostWidth, HostHeight));
            NarrowWidthLayout.Settle(window);
            capture?.Invoke(window);
            assert(Assert.Single(host.OpenViews));
            host.CloseTop();
            Dispatcher.UIThread.RunJobs();
        }
        finally
        {
            window.Close();
            host.Dispose();
        }
    }
}
