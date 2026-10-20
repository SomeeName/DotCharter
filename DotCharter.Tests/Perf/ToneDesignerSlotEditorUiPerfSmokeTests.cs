using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.UI.Controls;
using DotCharter.UI.Views.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     #173: opening the Tone designer built five <c>AutoCompleteBox</c>es per
///     arrangement card on every open, because the dialog's view model is new each
///     time. The slots are now <see cref="DeferredAutoCompleteBox" />es — a resting
///     field that builds the real box on first focus. These cases pin the
///     STRUCTURE (what an open builds) and that
///     the deferred field still is the field: named, in the tab order, editing the
///     row, offering completion and committing on close. The open time itself is
///     logged by <see cref="SurfaceShowRewarmUiPerfSmokeTests" />, never asserted.
/// </summary>
[Collection("HeadlessUI")]
public sealed class ToneDesignerSlotEditorUiPerfSmokeTests
{
    [Fact]
    public async Task AnOpen_BuildsNoAutoCompleteBox_UntilASlotIsFocused()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var project = SurfaceShowRewarmUiPerfSmokeTests.ToneProject(2, 5);
            using var provider = Provider(project);
            SurfaceShowRewarmUiPerfSmokeTests.WithDialogHost(provider, (host, window) =>
            {
                for (var open = 1; open <= 2; open++)
                {
                    var view = Open(host, window);

                    var slots = Slots(view);
                    Assert.Equal(2 * 5, slots.Count);
                    var built = view.GetVisualDescendants().OfType<AutoCompleteBox>().Count();
                    Assert.True(built == 0,
                        $"open {open} built {built} AutoCompleteBox(es) before any slot was focused — each one is a " +
                        "templated TextBox and a popup, rebuilt on every open (#173)");
                    foreach (var slot in slots)
                    {
                        Assert.Null(slot.Editor);
                        Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(slot)),
                            "a resting slot field has no accessible name");
                        Assert.True(slot.Bounds.Width > 1 && slot.Bounds.Height >= 32,
                            $"a resting slot field laid out {slot.Bounds} — it measured nothing");
                    }


                    host.CloseTop();
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                }
            });
        }, CancellationToken.None);
    }

    [Fact]
    public async Task AFocusedSlot_IsTheRealBox_NamedInTheTabOrder_AndEditsTheRow()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var project = SurfaceShowRewarmUiPerfSmokeTests.ToneProject(2, 5);
            using var provider = Provider(project);
            SurfaceShowRewarmUiPerfSmokeTests.WithDialogHost(provider, (host, window) =>
            {
                var view = Open(host, window);
                var vm = Assert.IsType<ToneDesignerViewModel>(view.DataContext);
                var row = vm.Arrangements[0];
                // The first card's five fields, in document order: base, A, B, C, D.
                var slots = Slots(view).Take(5).ToList();
                var (toneBase, toneA, toneB) = (slots[0], slots[1], slots[2]);

                // ── at rest: an Edit with a value, under its own name ───────────
                var peer = ControlAutomationPeer.CreatePeerForElement(toneB);
                Assert.Equal(AutomationControlType.Edit, peer.GetAutomationControlType());
                Assert.Equal(AutomationProperties.GetName(toneB), peer.GetName());
                Assert.Equal(row.ToneB, Assert.IsAssignableFrom<IValueProvider>(peer).Value);

                // ── a click makes it the real box and puts the caret in it ───────
                var height = toneBase.Bounds.Height;
                var point = toneBase.TranslatePoint(new Point(20, toneBase.Bounds.Height / 2), window)!.Value;
                window.MouseDown(point, MouseButton.Left);
                window.MouseUp(point, MouseButton.Left);
                Settle(window);
                var baseBox = Assert.IsType<AutoCompleteBox>(toneBase.Editor);
                Assert.Same(baseBox, FocusedBoxIn(window));
                Assert.Equal(AutomationProperties.GetName(toneBase), AutomationProperties.GetName(baseBox));
                Assert.Equal(row.ToneBase, baseBox.Text);
                Assert.Equal(height, toneBase.Bounds.Height, 0.5);
                Assert.False(toneBase.Focusable, "a realized slot field is still a tab stop of its own");
                Assert.Single(view.GetVisualDescendants().OfType<AutoCompleteBox>());

                // ── Tab goes on to the next slot, which realizes; Shift+Tab back ─
                window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
                Settle(window);
                Assert.NotNull(toneA.Editor);
                Assert.Same(toneA.Editor, FocusedBoxIn(window));
                window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.Shift);
                Settle(window);
                Assert.Same(baseBox, FocusedBoxIn(window));
                window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
                Settle(window);
                Assert.Same(toneA.Editor, FocusedBoxIn(window));

                // ── typing edits the row and offers the authored keys ───────────
                var boxA = toneA.Editor!;
                boxA.Text = "";
                Settle(window);
                window.KeyTextInput("tone3");
                Settle(window);
                Assert.Equal("tone3", row.ToneA);
                Assert.True(boxA.IsDropDownOpen, "typing in a realized slot offered no completion");
                Assert.Contains(boxA.ItemsSource!.Cast<object>(), k => (string)k == "tone3");

                // ── the row can still drive both a resting and a realized field ─
                row.ToneD = "tone4";
                row.ToneA = "tone2";
                Settle(window);
                Assert.Equal("tone4", slots[4].Text);
                Assert.Equal("tone2", boxA.Text);

                // ── and Done commits it to the arrangement ──────────────────────
                vm.CloseCommand.Execute(null);
                Settle(window);
                Assert.Equal("tone2", project.Arrangements[0].ToneA);
                Assert.Equal("tone4", project.Arrangements[0].ToneD);
            });
        }, CancellationToken.None);
    }

    private static ServiceProvider Provider(ChartProject project)
    {
        var services = new ServiceCollection();
        services.AddSingleton<DialogsSlot>();
        services.AddScoped<IToneDesignerScreen>(_ => SurfaceShowRewarmUiPerfSmokeTests.ToneDesigner(project));
        return services.BuildServiceProvider();
    }

    private static Control Open(DialogHost host, Window window)
    {
        _ = host.ShowAsync<IToneDesignerScreen, bool>();
        Settle(window);
        return host.OpenViews[^1];
    }

    private static List<DeferredAutoCompleteBox> Slots(Control view) =>
        view.GetVisualDescendants().OfType<DeferredAutoCompleteBox>().Where(s => s.IsEffectivelyVisible).ToList();

    /// <summary>The <see cref="AutoCompleteBox" /> whose TextBox holds the keyboard, or null.</summary>
    private static AutoCompleteBox? FocusedBoxIn(Window window) =>
        window.FocusManager?.GetFocusedElement() is TextBox input
            ? input.GetVisualAncestors().OfType<AutoCompleteBox>().FirstOrDefault()
            : null;

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }
}
