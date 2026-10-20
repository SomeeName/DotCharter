using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.VisualTree;
using DotCharter.Localization;
using DotCharter.Tone.Audio.Equalizer;
using DotCharter.UI;
using DotCharter.UI.Controls;
using DotCharter.UI.ViewModels.Editor.Inspector;
using DotCharter.UI.Views.Editor.Inspector;

namespace DotCharter.Tests.Perf;

/// <summary>
///     Double-click a slider, get its default back — proved by SYNTHESISING the
///     pointer input, on the real styled control, in a real window.
/// </summary>
/// <remarks>
///     <para>
///         The source lint in <c>UI/EqModuleReachabilityTests</c> proves every
///         slider DECLARES a default. It cannot prove the gesture arrives, and
///         that is the half worth doubting: Avalonia's <c>Thumb</c> marks the
///         press handled, and a slider's own <c>Track</c> sits between the
///         pointer and anything the view attached. Nothing short of real input
///         answers whether the tap survives that.
///     </para>
///     <para>
///         Named <c>*UiPerfSmokeTests</c> because it needs the real Avalonia app
///         session — which cannot share a testhost with the gate and is excluded from
///         the default gate.
///     </para>
/// </remarks>
[Collection("HeadlessUI")]
public sealed class ResetOnDoubleClickUiPerfSmokeTests
{
    [Fact]
    public Task ADoubleClick_PutsAStyledSliderBackToItsDeclaredDefault() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var slider = new Slider { Minimum = -18, Maximum = 18, Value = -11, Width = 240, Height = 30 };
            slider.Classes.Add("MixerSlider");
            ResetOnDoubleClick.SetDefaultValue(slider, 0);

            WithStyledWindow(slider, window =>
            {
                DoubleClick(window, Centre(window, slider));
                Assert.Equal(0, slider.Value, 6);
            });
        }, CancellationToken.None);

    [Fact]
    public Task ADoubleClick_RunsTheCommandSinkWithTheControlsOwnName() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            // The mixer's thirteen defaults live in ONE switch on the transport
            // view model, keyed by the slider's name. That is the sink this
            // covers — a value written here instead would be a fourteenth copy.
            var seen = new List<object?>();
            var slider = new Slider { Minimum = 0, Maximum = 1, Value = 0.3, Width = 240, Height = 30, Name = "SpeedSlider" };
            slider.Classes.Add("MixerSlider");
            ResetOnDoubleClick.SetCommand(slider, new RelayProbeCommand(seen.Add));

            WithStyledWindow(slider, window =>
            {
                DoubleClick(window, Centre(window, slider));
                Assert.Equal(["SpeedSlider"], seen);
            });
        }, CancellationToken.None);

    [Fact]
    public Task ASingleClick_ResetsNothing() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var slider = new Slider { Minimum = -18, Maximum = 18, Value = -11, Width = 240, Height = 30 };
            slider.Classes.Add("MixerSlider");
            ResetOnDoubleClick.SetDefaultValue(slider, 0);

            WithStyledWindow(slider, window =>
            {
                var point = Centre(window, slider);
                window.MouseDown(point, MouseButton.Left);
                window.MouseUp(point, MouseButton.Left);

                // The press itself moves the value (that is what a slider does);
                // what must NOT have happened is the reset.
                Assert.NotEqual(0, slider.Value);
            });
        }, CancellationToken.None);

    [Fact]
    public Task ASliderWithNoDeclaredDefault_IsNotArmedAtAll() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var slider = new Slider { Minimum = -18, Maximum = 18, Value = -11, Width = 240, Height = 30 };
            slider.Classes.Add("MixerSlider");

            WithStyledWindow(slider, window =>
            {
                Assert.False(ResetOnDoubleClick.IsArmed(slider));
                DoubleClick(window, Centre(window, slider));
                Assert.NotEqual(0, slider.Value);
            });
        }, CancellationToken.None);

    /// <summary>
    ///     The whole EQ module, driven end to end: the Q slider double-clicked
    ///     back to Butterworth THROUGH the view model, the same number the RESET
    ///     button writes.
    /// </summary>
    [Fact]
    public Task InTheRealModule_DoubleClickingTheQSlider_PutsTheBandBackToButterworth() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            _ = App.Services ?? throw new InvalidOperationException("the headless app session did not boot.");

            var vm = new EqModuleViewModel(new FakeAudioPlaybackService(), LocalizationService.Instance)
            {
                SelectedPresetKey = "FindTheGuitar",
                SelectedIndex = 2,
                SelectedQNormalized = 0.85
            };
            Assert.NotEqual(EqLimits.DefaultQ, vm.Section.Bands[2].Q, 3);

            var view = new EqModuleView { DataContext = vm, Width = 420 };
            WithStyledWindow(view, window =>
            {
                // Freq, Gain, Q — the band card's three, in markup order.
                var q = view.GetVisualDescendants().OfType<Slider>().ElementAt(2);
                DoubleClick(window, Centre(window, q));

                Assert.Equal(EqLimits.DefaultQ, vm.Section.Bands[2].Q, 3);
            });
        }, CancellationToken.None);

    // ── harness ─────────────────────────────────────────────────────────────

    /// <summary>
    ///     A window carrying the inspector grammar, because the styled control is
    ///     the one that has to work: <c>MixerSlider</c> retemplates the track and
    ///     the thumb, and a bare <c>Slider</c> would prove nothing about the
    ///     control the user actually clicks.
    /// </summary>
    private static void WithStyledWindow(Control content, Action<Window> body)
    {
        var window = new Window { Width = 520, Height = 520, Content = content };
        window.Styles.Add(new StyleInclude(new Uri("avares://DotCharter/"))
        {
            Source = new Uri("avares://DotCharter/Views/Editor/InspectorStyles.axaml")
        });

        window.Show();
        try
        {
            window.UpdateLayout();
            body(window);
        }
        finally
        {
            window.Close();
        }
    }

    private static Point Centre(Window window, Visual control) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)
        ?? new Point(0, 0);

    private static void DoubleClick(Window window, Point point)
    {
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
    }

    /// <summary>The smallest thing that records an execute. A Mvvm-Toolkit
    /// command would need a view model; this needs a lambda.</summary>
    private sealed class RelayProbeCommand(Action<object?> onExecute) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => onExecute(parameter);
    }
}
