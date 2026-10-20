using Avalonia.Controls;
using Avalonia.VisualTree;
using DotCharter.Tone.Diagnostics;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.Views.Editor.Inspector;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The Guitar roles switch (#260) built for real, attached to a window and laid
///     out, in the two states a user meets first: disabled with its reason (no
///     guitar stem), and on. Both must MEASURE — the caption that says this is an
///     approximation from the stereo image, and the reason line, are the honesty
///     half of the feature, and a style that failed to resolve would leave them
///     arranged at zero size. A host with no view model shows nothing.
/// </summary>
[Collection("HeadlessUI")]
public sealed class GuitarRolesConstructionUiPerfSmokeTests
{
    [Fact]
    public async Task Disabled_WithItsReason_BuildsAndLaysOut()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var vm = NewViewModel();
            vm.Update(new ChartProject(), hasStems: true, guitarPath: null, rolesActive: false);
            var view = new GuitarRolesView { DataContext = vm };
            AttachAndLayout(view, () =>
            {
                var toggle = Find<CheckBox>(view, "GuitarRolesToggle");
                Assert.False(toggle.IsEnabled, "The switch looks live with no guitar stem.");
                AssertMeasured(toggle);
                AssertMeasured(Find<TextBlock>(view, "GuitarRolesCaption"));
                var status = Find<TextBlock>(view, "GuitarRolesStatus");
                Assert.True(status.IsVisible, "The reason line is hidden.");
                Assert.False(string.IsNullOrWhiteSpace(status.Text));
                AssertMeasured(status);
            });
        }, CancellationToken.None);
    }

    [Fact]
    public async Task On_BuildsAndLaysOut()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var vm = NewViewModel();
            vm.Update(new ChartProject { GuitarRolesSplit = true }, true, @"C:\song\stems\guitar.wav", rolesActive: true);
            var view = new GuitarRolesView { DataContext = vm };
            AttachAndLayout(view, () =>
            {
                var toggle = Find<CheckBox>(view, "GuitarRolesToggle");
                Assert.True(toggle.IsEnabled);
                Assert.True(toggle.IsChecked);
                AssertMeasured(toggle);
            });
        }, CancellationToken.None);
    }

    [Fact]
    public async Task WithNoViewModel_ShowsNothing()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var view = new GuitarRolesView();
            AttachAndLayout(view, () =>
                Assert.False(Find<StackPanel>(view, "GuitarRolesRoot").IsVisible,
                    "The switch shows with no view model behind it (FallbackValue lost)."));
        }, CancellationToken.None);
    }

    /// <summary>
    ///     A dispatcher that never runs posted work: the background stereo check a
    ///     view model starts would otherwise write bound properties from a pool
    ///     thread while the view is attached.
    /// </summary>
    private static GuitarRolesViewModel NewViewModel() =>
        new(new DotCharter.Tests.UI.ViewModels.Editor.GuitarRolesViewModelTests.FakeGuitarRoleStems(), FakeLocalization.English(),
            new ParkedDispatcher(), () => { });

    private static T Find<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    private static void AssertMeasured(Control control) =>
        Assert.True(control.Bounds.Width > 1 && control.Bounds.Height > 1,
            $"{control.Name} laid out as {control.Bounds} — it rendered nothing.");

    private static void AttachAndLayout(Control view, Action assert)
    {
        var window = new Window { Width = 420, Height = 300, Content = view };
        window.Show();
        try
        {
            view.Measure(new Avalonia.Size(420, 300));
            view.Arrange(new Avalonia.Rect(0, 0, 420, 300));
            window.UpdateLayout();
            assert();
        }
        finally
        {
            window.Close();
        }
    }

    private sealed class ParkedDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;
        public void Post(Action action) { }
        public void PostNormal(Action action) { }
        public Task InvokeAsync(Action action) => Task.CompletedTask;
        public Task<T> InvokeAsync<T>(Func<T> func) => Task.FromResult(func());
        public Task<T> InvokeAsync<T>(Func<Task<T>> func) => func();
        public IDisposable SchedulePeriodic(TimeSpan period, Action tick) => new Inert();

        private sealed class Inert : IDisposable
        {
            public void Dispose() { }
        }
    }
}
