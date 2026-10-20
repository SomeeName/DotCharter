// The dialogs on DialogHost, DERIVED from the screen catalog: the harness that opens one the way the app does (a real DialogHost inside the real DialogContainerView, bound to a fake variant), and what only a host can prove — Enter and Esc answering, the stacking against the legacy dialogs, and the shell seeing a hosted dialog as open.

using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Presentation.Fakes;
using DotCharter.UI;
using DotCharter.UI.ViewModels.Shell;
using DotCharter.UI.Views.Dialogs;
using DotCharter.UI.Views.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

public sealed partial class DialogConstructionUiPerfSmokeTests
{
    /// <summary>Every catalog dialog drawn in the dialog grammar (the message prompt wears the Fire card instead).</summary>
    private static readonly IReadOnlyList<ScreenEntry> HostedDialogs =
    [
        .. ScreenCatalog.Default.Entries.Where(e =>
            e.Kind == ScreenKind.Dialog && e.ViewType.Namespace == typeof(SetBpmDialogView).Namespace)
    ];

    /// <summary>The grammar checks run over both halves: the legacy dialogs and the hosted ones.</summary>
    private static void ForEachConvertedDialog(Action<string, Control> assert)
    {
        foreach (var name in Converted)
        {
            var dialog = CreateDialog(name);
            WithLaidOutDialog(dialog, () => assert(name, dialog));
        }

        Assert.True(HostedDialogs.Count >= 29, $"only {HostedDialogs.Count} hosted dialogs in the catalog");
        foreach (var entry in HostedDialogs)
            WithHostedDialog(entry, FakeVariantNames.Default, (view, _) => assert(entry.ViewType.Name, view));
    }

    private static void AssertMeasured(string name, Control view) =>
        Assert.True(view.Bounds.Width > 1 && view.Bounds.Height > 1, $"{name} laid out as {view.Bounds} — it rendered nothing");

    /// <summary>
    ///     Opens <paramref name="entry" /> on a real <see cref="DialogHost" />, bound
    ///     to its fake's <paramref name="variant" />, inside a real
    ///     <see cref="DialogContainerView" /> (where <c>DialogStyles.axaml</c> is
    ///     included) in a shown window, lays it out, and hands over the open view.
    /// </summary>
    private static void WithHostedDialog(ScreenEntry entry, string variant, Action<Control, DialogHost> assert)
    {
        var fake = FakeScreens.VariantsOf(entry.Contract).Single(v => v.Name == variant);
        var services = new ServiceCollection();
        services.AddSingleton<DialogsSlot>();
        services.AddScoped(entry.Contract, _ => fake.Create());
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var host = new DialogHost(provider.GetRequiredService<IServiceScopeFactory>(), ScreenCatalog.Default,
            provider.GetRequiredService<DialogsSlot>());

        var container = new DialogContainerView();
        container.DialogPanel.Children.Clear();
        container.DialogPanel.Children.Add(host);
        var window = new Window { Width = HostWidth, Height = HostHeight, Content = container };
        window.Show();
        try
        {
            _ = Open(host, entry);
            Dispatcher.UIThread.RunJobs();
            container.Measure(new Size(HostWidth, HostHeight));
            container.Arrange(new Rect(0, 0, HostWidth, HostHeight));
            assert(Assert.Single(host.OpenViews), host);
            host.CloseTop();
            Dispatcher.UIThread.RunJobs();
        }
        finally
        {
            window.Close();
            host.Dispose();
        }
    }

    /// <summary><c>ShowAsync&lt;TContract, TResult&gt;</c> for a catalog entry, whose result type the contract names.</summary>
    private static Task Open(IDialogs host, ScreenEntry entry)
    {
        var result = entry.Contract.GetInterfaces().Append(entry.Contract)
            .First(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDialogScreen<>))
            .GetGenericArguments()[0];
        var show = typeof(IDialogs).GetMethod(nameof(IDialogs.ShowAsync))!.MakeGenericMethod(entry.Contract, result);
        return (Task)show.Invoke(host, [null])!;
    }

    private static object? AnswerOf(Task answer) => answer.GetType().GetProperty("Result")!.GetValue(answer);

    private static object? DefaultAnswer(ScreenEntry entry)
    {
        var type = entry.Contract.GetInterfaces().Append(entry.Contract)
            .First(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDialogScreen<>))
            .GetGenericArguments()[0];
        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }

    private static void PumpUntil(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!done() && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }

        Assert.True(done(), "the dialog never answered — its continuation was not delivered in 60 s");
    }

    private static void Press(Control target, Key key) =>
        target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Source = target, Key = key });

    /// <summary>
    ///     The keyboard contract every dialog had on the legacy path, now on the
    ///     host: Enter runs the <c>IsDefault</c> button (Save / Apply / Generate /
    ///     Download — a non-default answer), Esc closes the top dialog with the
    ///     default answer (false, or Cancel for the unsaved-changes gate), and the
    ///     host is empty again afterwards.
    /// </summary>
    [Fact]
    public async Task EveryHostedDialog_AnswersEnterWithItsDefaultButton_AndEscWithTheDefaultResult()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            foreach (var entry in HostedDialogs)
            {
                foreach (var key in new[] { Key.Enter, Key.Escape })
                {
                    var services = new ServiceCollection();
                    services.AddSingleton<DialogsSlot>();
                    services.AddScoped(entry.Contract,
                        _ => FakeScreens.VariantsOf(entry.Contract).Single(v => v.Name == FakeVariantNames.Default).Create());
                    using var provider = services.BuildServiceProvider();
                    var host = new DialogHost(provider.GetRequiredService<IServiceScopeFactory>(), ScreenCatalog.Default,
                        provider.GetRequiredService<DialogsSlot>());
                    var container = new DialogContainerView();
                    container.DialogPanel.Children.Clear();
                    container.DialogPanel.Children.Add(host);
                    var window = new Window { Width = HostWidth, Height = HostHeight, Content = container };
                    window.Show();
                    try
                    {
                        var answer = Open(host, entry);
                        Dispatcher.UIThread.RunJobs();
                        window.UpdateLayout();
                        Press(Assert.Single(host.OpenViews), key);
                        PumpUntil(() => answer.IsCompleted);
                        Dispatcher.UIThread.RunJobs();

                        var name = $"{entry.ViewType.Name} on {key}";
                        Assert.True(answer.IsCompletedSuccessfully, $"{name}: the dialog faulted");
                        if (key == Key.Enter)
                        {
                            Assert.False(Equals(AnswerOf(answer), DefaultAnswer(entry)),
                            $"{name}: Enter answered the default — the IsDefault button did not run");
                        }
                        else
                        {
                            Assert.Equal(DefaultAnswer(entry), AnswerOf(answer));
                        }

                        Assert.False(host.HasOpenDialog, $"{name}: the host still has a dialog open");
                        Assert.False(host.Scrim.IsVisible, $"{name}: the shared scrim stayed up");
                    }
                    finally
                    {
                        window.Close();
                        host.Dispose();
                    }
                }
            }
        }, CancellationToken.None);
    }

    /// <summary>
    ///     The stacking the legacy ZIndex tiers used to claim, as the container
    ///     really builds it: every legacy dialog below the host, the host below the
    ///     app-wide prompt — so a confirmation raised from any dialog is on top of
    ///     it, and a hosted dialog is never drawn under a legacy one.
    /// </summary>
    [Fact]
    public async Task TheDialogHost_SitsAboveEveryLegacyDialog_AndUnderTheAppWidePrompt()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var container = new DialogContainerView
            {
                DataContext = App.Services?.GetRequiredService<MainWindowViewModel>()
            };
            var host = container.Dialogs;
            Assert.True(host is not null, "the container built no dialog host — nothing can open on IDialogs");

            container.HiddenSurfaces();
            var children = container.DialogPanel.Children;
            var hostAt = children.IndexOf(host!);
            var promptAt = children.IndexOf(children.OfType<MessageDialogView>().Single());
            Assert.True(hostAt < promptAt, $"the dialog host (child {hostAt}) is above the app-wide prompt ({promptAt})");

            var legacy = children.OfType<ContentControl>().Where(c => c is not MessageDialogView).ToList();
            // 1 legacy dialog is left after the Songsterr import moved; the floor follows them down.
            Assert.True(legacy.Count >= 1, $"only {legacy.Count} legacy dialogs were materialized — the check is vacuous");
            var above = legacy.Where(c => children.IndexOf(c) > hostAt).Select(c => c.GetType().Name).ToList();
            Assert.True(above.Count == 0,
                "These legacy dialogs are drawn over the dialog host:\n  " + string.Join("\n  ", above));
        }, CancellationToken.None);
    }

    /// <summary>
    ///     Keyboard shortcuts: a chord is no louder than the command beside it. It
    ///     wore the song-key readout's display type (a size up, display weight) and
    ///     out-shouted every label in the list (live review, 2026-09-24).
    /// </summary>
    [Fact]
    public async Task KeyboardShortcuts_AChordIsNoLouderThanItsCommand()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var entry = HostedDialogs.Single(e => e.Contract == typeof(IKeyboardShortcutsScreen));
            WithHostedDialog(entry, FakeVariantNames.Default, (view, _) =>
            {
                var rows = Descendants<Grid>(view)
                    .Select(g => (Label: g.Children.OfType<TextBlock>().FirstOrDefault(t => t.Classes.Contains("DlgLabel")),
                                  Chord: g.Children.OfType<TextBlock>().FirstOrDefault(t => Grid.GetColumn(t) == 1)))
                    .Where(r => r.Label is not null && r.Chord is not null)
                    .ToList();
                Assert.True(rows.Count >= 3, $"only {rows.Count} shortcut rows laid out — the check is vacuous");
                foreach (var (label, chord) in rows)
                {
                    Assert.True(chord!.FontSize <= label!.FontSize,
                        $"'{chord.Text}' is {chord.FontSize} px beside '{label.Text}' at {label.FontSize} px");
                    Assert.True(chord.FontWeight <= label.FontWeight,
                        $"'{chord.Text}' is heavier ({chord.FontWeight}) than '{label.Text}' ({label.FontWeight})");
                }
            });
        }, CancellationToken.None);
    }

    /// <summary>
    ///     A hosted dialog is an OPEN DIALOG to the shell: <c>HasOpenDialog</c> is
    ///     what hides the native 3D pane, gates the global chords and the keybind
    ///     registry, and it must rise (with a change notification) while one is up
    ///     and fall when it closes — through the app's real registrations.
    /// </summary>
    [Fact]
    public async Task A_hosted_dialog_is_an_open_dialog_to_the_shell()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var services = App.Services;
            Assert.NotNull(services);
            var shellDialogs = services!.GetRequiredService<IAppDialogService>();
            var container = new DialogContainerView { DataContext = services.GetRequiredService<MainWindowViewModel>() };
            var window = new Window { Width = HostWidth, Height = HostHeight, Content = container };
            var raised = 0;
            PropertyChangedEventHandler count = (_, e) =>
            {
                if (e.PropertyName == nameof(IAppDialogService.HasOpenDialog)) raised++;
            };
            window.Show();
            shellDialogs.PropertyChanged += count;
            try
            {
                var answer = services.GetRequiredService<IDialogs>()
                    .ShowAsync<IUnsavedChangesScreen, UnsavedChangesChoice>();
                Dispatcher.UIThread.RunJobs();

                Assert.True(shellDialogs.HasOpenDialog, "a hosted dialog is up but the shell reports no open dialog");
                Assert.True(raised > 0, "HasOpenDialog rose without a change notification — the 3D pane would not hear it");

                Assert.NotNull(shellDialogs.CloseTopDialog());
                PumpUntil(() => answer.IsCompleted);
                Dispatcher.UIThread.RunJobs();

                Assert.Equal(UnsavedChangesChoice.Cancel, answer.Result);
                Assert.False(shellDialogs.HasOpenDialog);
            }
            finally
            {
                shellDialogs.PropertyChanged -= count;
                window.Close();
            }
        }, CancellationToken.None);
    }
}
