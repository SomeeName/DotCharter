using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.UI.Controls;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     #6: the Tone designer's gear slots hold the whole catalogue, and finding one by scrolling a
///     <c>ComboBox</c> was slow. The selected row's picker is now a <see cref="DeferredChoiceBox" />:
///     a resting field that builds an <c>AutoCompleteBox</c> on first focus. These cases pin that an
///     open still builds no box, that typing narrows the list by name AND by gear key (any case),
///     that Down/Enter commit a match into the slot, and that Escape (or leaving the field) keeps
///     the old choice.
/// </summary>
[Collection("HeadlessUI")]
public sealed class ToneDesignerGearSearchUiPerfSmokeTests
{
    [Fact]
    public async Task TheGearPicker_RestsAsAField_AndBuildsNoBoxOnOpen()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            WithDesigner((view, window, vm) =>
            {
                var picker = OpenPicker(view);
                Assert.Null(picker.Editor);
                Assert.Empty(view.GetVisualDescendants().OfType<AutoCompleteBox>());
                Assert.True(picker.Bounds.Width > 1 && picker.Bounds.Height >= 32,
                    $"the gear picker laid out {picker.Bounds} — it measured nothing");
                Assert.Equal(vm.Editor.SelectedGearSlot!.Label, AutomationProperties.GetName(picker));
                Assert.True(picker.ItemsSource!.Cast<object>().Count() > 20, "the picker is not over the whole gear family");
            });
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Typing_NarrowsTheList_ByNameAndByKey_IgnoringCase()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            WithDesigner((view, window, vm) =>
            {
                var slot = PickedSlot(vm);
                var options = slot.Options;
                var picker = OpenPicker(view);
                var box = Focus(window, picker);

                // A fresh click shows the whole family, then typing replaces the picked name.
                Assert.Equal(options.Count, Shown(box).Count);

                // Narrowing by display name, in the other case.
                var word = options.Select(o => o.DisplayName).First(n => n.Length > 4 && n.Any(char.IsLetter) && n.Any(char.IsLower));
                var needle = word.Substring(1, 3).ToUpperInvariant();
                Type(window, box, needle);
                var expected = options.Where(o => o.DisplayName.Contains(needle, StringComparison.OrdinalIgnoreCase)
                                                  || o.SearchKey.Contains(needle, StringComparison.OrdinalIgnoreCase)).ToList();
                var shown = Shown(box);
                Assert.True(shown.Count > 0 && shown.Count < options.Count,
                    $"typing '{needle}' left {shown.Count} of {options.Count} entries — the list did not narrow");
                Assert.Equal(expected.Select(o => o.DisplayName).Order(), shown.Select(o => o.DisplayName).Order());

                // Narrowing by the gear key alone: text that is in keys but in no display name.
                var keyOnly = options.Where(o => o.SearchKey.Length > 0).Select(o => o.SearchKey)
                    .SelectMany(k => Enumerable.Range(0, Math.Max(0, k.Length - 4)).Select(i => k.Substring(i, 5)))
                    .First(s => options.Any(o => o.SearchKey.Contains(s, StringComparison.OrdinalIgnoreCase))
                                && !options.Any(o => o.DisplayName.Contains(s, StringComparison.OrdinalIgnoreCase)));
                Type(window, box, keyOnly);
                shown = Shown(box);
                Assert.True(shown.Count > 0, $"typing the key fragment '{keyOnly}' found nothing");
                Assert.All(shown, o => Assert.Contains(keyOnly, o.SearchKey, StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain(shown, o => o.DisplayName.Contains(keyOnly, StringComparison.OrdinalIgnoreCase));
            });
        }, CancellationToken.None);
    }

    [Fact]
    public async Task DownThenEnter_CommitsTheMatch_IntoTheSlot()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            WithDesigner((view, window, vm) =>
            {
                var slot = PickedSlot(vm);
                var before = slot.SelectedOption;
                var target = slot.Options.First(o => o.Gear is not null && !ReferenceEquals(o, before));
                var picker = OpenPicker(view);
                var box = Focus(window, picker);

                Type(window, box, target.DisplayName);
                window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
                Settle(window);
                window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                Settle(window);

                Assert.Equal(target.DisplayName, slot.SelectedOption.DisplayName);
                Assert.Equal(target.DisplayName, box.Text);
                Assert.Equal(target.DisplayName, picker.SelectedItem?.DisplayName);
                Assert.True(slot.HasGear);
                Assert.False(box.IsDropDownOpen);
            });
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Enter_WithoutAHighlight_TakesTheFirstMatch()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            WithDesigner((view, window, vm) =>
            {
                var slot = PickedSlot(vm);
                var target = slot.Options.First(o => o.Gear is not null && !ReferenceEquals(o, slot.SelectedOption));
                var picker = OpenPicker(view);
                var box = Focus(window, picker);

                Type(window, box, target.DisplayName.ToLowerInvariant());
                window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                Settle(window);

                Assert.Equal(target.DisplayName, slot.SelectedOption.DisplayName);
                Assert.Equal(target.DisplayName, box.Text);
            });
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Escape_KeepsTheOldChoice_AndPutsItsNameBack()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            WithDesigner((view, window, vm) =>
            {
                var slot = PickedSlot(vm);
                var before = slot.SelectedOption;
                var other = slot.Options.First(o => o.Gear is not null && !ReferenceEquals(o, before));
                var picker = OpenPicker(view);
                var box = Focus(window, picker);

                Type(window, box, other.DisplayName);
                window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                Settle(window);

                Assert.Same(before, slot.SelectedOption);
                Assert.Equal(before.DisplayName, box.Text);
                Assert.False(box.IsDropDownOpen);

                // …and so does leaving the field with text that was never committed.
                Type(window, box, other.DisplayName);
                var elsewhere = view.GetVisualDescendants().OfType<Button>().First(b => b.Focusable && b.IsEffectivelyVisible && b.IsEnabled);
                Assert.True(elsewhere.Focus(), "could not move the focus off the gear box");
                Settle(window);
                Assert.Same(before, slot.SelectedOption);
                Assert.Equal(before.DisplayName, box.Text);
            });
        }, CancellationToken.None);
    }

    private static ToneGearSlot PickedSlot(ToneDesignerViewModel vm) =>
        Assert.IsType<ToneGearSlot>(vm.Editor.SelectedGearSlot);

    private static DeferredChoiceBox OpenPicker(Control view) =>
        view.GetVisualDescendants().OfType<DeferredChoiceBox>().Single(c => c.IsEffectivelyVisible);

    /// <summary>A real click on the resting field: it becomes the box and the caret moves in.</summary>
    private static AutoCompleteBox Focus(Window window, DeferredChoiceBox picker)
    {
        var point = picker.TranslatePoint(new Point(20, picker.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Settle(window);
        var box = Assert.IsType<AutoCompleteBox>(picker.Editor);
        Assert.True(window.FocusManager?.GetFocusedElement() is TextBox input && input.GetVisualAncestors().Contains(box),
            "the click did not put the caret in the gear box");
        return box;
    }

    /// <summary>Replaces what the box holds with <paramref name="text" />, the way a user does: select all, type.</summary>
    private static void Type(Window window, AutoCompleteBox box, string text)
    {
        box.GetVisualDescendants().OfType<TextBox>().First().SelectAll();
        window.KeyTextInput(text);
        Settle(window);
    }

    /// <summary>The entries the drop-down is listing right now.</summary>
    private static List<ToneGearOption> Shown(AutoCompleteBox box)
    {
        var list = box.GetVisualDescendants().OfType<Popup>().Single().Child!.GetSelfAndVisualDescendants().OfType<SelectingItemsControl>().Single();
        return list.ItemsSource is { } items ? items.OfType<ToneGearOption>().ToList() : [];
    }

    private static void WithDesigner(Action<Control, Window, ToneDesignerViewModel> act)
    {
        var project = SurfaceShowRewarmUiPerfSmokeTests.ToneProject(1, 2);
        var services = new ServiceCollection();
        services.AddSingleton<DialogsSlot>();
        services.AddScoped<IToneDesignerScreen>(_ => SurfaceShowRewarmUiPerfSmokeTests.ToneDesigner(project));
        using var provider = services.BuildServiceProvider();
        SurfaceShowRewarmUiPerfSmokeTests.WithDialogHost(provider, (host, window) =>
        {
            _ = host.ShowAsync<IToneDesignerScreen, bool>();
            Settle(window);
            var view = host.OpenViews[^1];
            act(view, window, Assert.IsType<ToneDesignerViewModel>(view.DataContext));
            host.CloseTop();
            Settle(window);
        });
    }

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }
}
