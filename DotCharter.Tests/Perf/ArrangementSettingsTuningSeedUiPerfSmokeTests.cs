using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Tests.UI.ViewModels.Dialogs;
using DotCharter.UI.Views.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     #905: the arrangement dialog, bound to the REAL view model inside the real
///     <see cref="DialogHost" />, must show the stored offsets and write them back unchanged
///     on OK, on the first open and on a reopen (the host reuses the idle view).
/// </summary>
[Collection("HeadlessUI")]
public sealed class ArrangementSettingsTuningSeedUiPerfSmokeTests
{
    private const double HostWidth = 1280;
    private const double HostHeight = 900;

    /// <summary>Each instrument the dialog edits with a uniform down-tuning (B standard), a deep one, a
    ///     drop, a mixed row and a positive row; the full matrix runs in the default gate
    ///     (<see cref="ArrangementSettingsTuningRoundTripTests" />).</summary>
    public static TheoryData<ArrangementType, int[]> Tunings
    {
        get
        {
            var data = new TheoryData<ArrangementType, int[]>();
            foreach (var (type, strings) in new[]
                     {
                         (ArrangementType.Bass, 4), (ArrangementType.Bass, 5), (ArrangementType.Bass, 6),
                         (ArrangementType.Lead, 6), (ArrangementType.Rhythm, 7), (ArrangementType.Combo, 8)
                     })
            {
                var drop = new int[strings];
                drop[0] = -2;
                var mixed = Enumerable.Repeat(-5, strings).ToArray();
                mixed[^1] = -4;
                data.Add(type, Enumerable.Repeat(-5, strings).ToArray());
                data.Add(type, Enumerable.Repeat(-12, strings).ToArray());
                data.Add(type, drop);
                data.Add(type, mixed);
                data.Add(type, Enumerable.Repeat(2, strings).ToArray());
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Tunings))]
    public async Task OpenOkReopen_ShowsAndKeepsTheStoredOffsets(ArrangementType type, int[] steps)
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var harness = new ArrangementSettingsTuningRoundTripTests.Harness(
                ArrangementSettingsTuningRoundTripTests.ArrangementWith(type, "CUSTOM", steps));
            WithHost(harness, host =>
            {
                // Three opens: the first builds the view, the later ones re-bind the idle one.
                for (var open = 1; open <= 3; open++)
                {
                    var (screen, view) = Show(host);
                    AssertShows(view, screen, steps, $"open {open}");
                    screen.SaveCommand.Execute(null);
                    Dispatcher.UIThread.RunJobs();
                    Assert.Empty(host.OpenViews);
                    Assert.Equal(steps, harness.Arrangement.TuningPitches.Select(t => t.Step).ToArray());
                }

                // Typing a new negative value into the lowest box applies it.
                var typed = steps.ToArray();
                typed[0] = -9;
                var (typing, typingView) = Show(host);
                TuningBoxes(typingView)[0].Text = "-9";
                Dispatcher.UIThread.RunJobs();
                typing.SaveCommand.Execute(null);
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(typed, harness.Arrangement.TuningPitches.Select(t => t.Step).ToArray());

                var (reopened, reopenedView) = Show(host);
                AssertShows(reopenedView, reopened, typed, "after typing");
                reopened.CancelCommand.Execute(null);
                Dispatcher.UIThread.RunJobs();
            });
        }, TestContext.Current.CancellationToken);
    }

    private static TextBox[] TuningBoxes(Control view) =>
        [.. view.GetVisualDescendants().OfType<TextBox>()
            .Where(b => b.FindAncestorOfType<ItemsControl>()?.Name == "TuningStepsRow")];

    private static void AssertShows(Control view, IArrangementSettingsScreen screen, int[] steps, string when)
    {
        var boxes = TuningBoxes(view);
        Assert.True(boxes.Length == steps.Length && boxes.All(b => b.Bounds.Width > 1),
            $"{when}: {boxes.Length} laid-out tuning boxes");
        Assert.Equal(steps, screen.TuningOffsets.Select(t => t.Step).ToArray());
        Assert.Equal(steps.Select(s => s.ToString(System.Globalization.CultureInfo.CurrentCulture)),
            boxes.Select(b => b.Text));
        var picker = view.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "TuningPresetPicker");
        Assert.Equal(screen.TuningPreset, picker.SelectedValue as string);
    }

    private static (IArrangementSettingsScreen Screen, Control View) Show(DialogHost host)
    {
        IArrangementSettingsScreen? shown = null;
        _ = ((IDialogs)host).ShowAsync<IArrangementSettingsScreen, bool>(s =>
        {
            ((ArrangementSettingsViewModel)s).Configure(new ArrangementSettingsRequest());
            shown = s;
        });
        Dispatcher.UIThread.RunJobs();
        host.UpdateLayout();
        return (shown!, Assert.Single(host.OpenViews));
    }

    private static void WithHost(ArrangementSettingsTuningRoundTripTests.Harness harness, Action<DialogHost> act)
    {
        var services = new ServiceCollection();
        services.AddSingleton<DialogsSlot>();
        services.AddScoped<IArrangementSettingsScreen>(_ => harness.Create());
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
            container.Measure(new Size(HostWidth, HostHeight));
            container.Arrange(new Rect(0, 0, HostWidth, HostHeight));
            act(host);
        }
        finally
        {
            window.Close();
            host.Dispose();
        }
    }
}
