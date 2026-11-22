// Keyboard entry (#367, #368): the five dialogs that carry a text input take focus in it when they open, and the commit boxes take Enter.

using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Fakes;
using DotCharter.UI.Views.Shell;

namespace DotCharter.Tests.Perf;

public sealed partial class DialogConstructionUiPerfSmokeTests
{
    /// <summary>The dialog views whose first input must hold focus on open (#368), with the control type that input is.</summary>
    public static TheoryData<string, Type> FirstInputDialogs => new()
    {
        { "CheckpointsDialogView", typeof(TextBox) },
        { "KeyboardShortcutsDialogView", typeof(TextBox) },
        { "AlignToRecordingDialogView", typeof(NumericUpDown) },
        { "ShapeEditDialogView", typeof(TextBox) },
        { "RocksmithExportDialogView", typeof(TextBox) }
    };

    [Theory]
    [MemberData(nameof(FirstInputDialogs))]
    public Task TextInputDialog_FocusesItsFirstInputOnOpen(string viewName, Type inputType) =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var entry = HostedDialogs.Single(e => e.ViewType.Name == viewName);
            WithHostedDialog(entry, FakeVariantNames.Default, (view, _) =>
            {
                var target = Assert.Single(Descendants<Control>(view), DotCharter.UI.Views.Dialogs.FocusOnOpen.GetIsTarget);
                Assert.IsType(inputType, target);

                var focused = TopLevel.GetTopLevel(view)?.FocusManager?.GetFocusedElement();
                var landed = ReferenceEquals(focused, target)
                             || (focused is Avalonia.Visual v
                                 && Avalonia.VisualTree.VisualExtensions.GetVisualAncestors(v).Contains(target));
                Assert.True(landed, $"{viewName}: focus sits on {focused?.GetType().Name ?? "nothing"}, not its first input");
            });
        }, CancellationToken.None);

    /// <summary>Enter in the new-checkpoint box creates the checkpoint (the Close button is the dialog's default and used to eat the key).</summary>
    [Fact]
    public Task Checkpoints_EnterInTheNameBox_IsBoundToCreate() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var entry = HostedDialogs.Single(e => e.Contract == typeof(ICheckpointsScreen));
            WithHostedDialog(entry, FakeVariantNames.Default, (view, _) =>
            {
                var screen = (ICheckpointsScreen)view.DataContext!;
                var box = Descendants<TextBox>(view).First(DotCharter.UI.Views.Dialogs.FocusOnOpen.GetIsTarget);
                var enter = Assert.Single(box.KeyBindings, k => k.Gesture.Key == Key.Enter);
                Assert.Same(screen.CreateCheckpointCommand, enter.Command);
            });
        }, CancellationToken.None);

    /// <summary>A LostFocus-bound box commits on Enter through <see cref="CommitOnEnter" />.</summary>
    [Fact]
    public Task CommitOnEnter_PushesALostFocusBoundText() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var model = new NameModel();
            var box = new TextBox();
            box.Bind(TextBox.TextProperty, new Binding(nameof(NameModel.Name))
            {
                Source = model, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus
            });
            CommitOnEnter.SetIsEnabled(box, true);
            var window = new Window { Width = 300, Height = 100, Content = box };
            window.Show();
            try
            {
                box.Focus();
                box.Text = "Friday set";
                Assert.Equal("", model.Name);

                window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
                Dispatcher.UIThread.RunJobs();

                Assert.Equal("Friday set", model.Name);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);

    private sealed class NameModel
    {
        public string Name { get; set; } = "";
    }
}
