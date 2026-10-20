using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The shared controls' STATE colours, read off the element that actually
///     paints. Fluent's control themes set a button's hover / pressed / disabled /
///     checked colours on the template's <c>PART_ContentPresenter</c> through
///     triggered setters, and the template gives a checkbox box, a switch track
///     and a thumb their colours as template values — both outrank an untriggered
///     style on the control. So a style that sets <c>Background</c> on the Button
///     itself, or an untriggered one on a template part, renders in Fluent's
///     colours and nothing says so: the Primary label turned white on light blue
///     on hover, and pressing it went grey. Each case forces the pseudoclass and
///     compares against the token the style means.
/// </summary>
[Collection("HeadlessUI")]
public sealed class ControlStateColourUiPerfSmokeTests
{
    [Theory]
    [InlineData("Primary", ":pointerover", "TextOnAccent")]
    [InlineData("Primary", ":pressed", "TextOnAccent")]
    [InlineData("Danger", ":pointerover", "DangerColor")]
    [InlineData("Danger", ":pressed", "DangerColor")]
    public async Task AButtonsLabel_KeepsItsColour_OnHoverAndPress(string cls, string state, string token)
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var button = new Button { Classes = { cls }, Content = "Save" }.Themed();
            WithLaidOut(button, () =>
            {
                var presenter = PresenterOf(button);
                Force(button, state);
                Assert.True(Colour(presenter.Foreground) == Token(button, token),
                    $"Button.{cls}{state}: the label is {Colour(presenter.Foreground)}, not {token} " +
                    $"({Token(button, token)}) — Fluent's state foreground is showing through");
                Assert.True(Colour(presenter.Background) != Token(button, "SurfaceActive"),
                    $"Button.{cls}{state}: the fill is Fluent's grey pressed background");
            });
        }, CancellationToken.None);
    }

    /// <summary>
    ///     #348: the Fire CTA and primary templates draw their fill on <c>Border#Cut</c>, and
    ///     their presenter is only the padded content strip inside it. A copied state rule
    ///     that paints the presenter (the whole button in Fluent's template) drew a box
    ///     behind the label on hover and press. In these templates the sheen is the hover:
    ///     the strip stays clear and the fill keeps its resting colour.
    /// </summary>
    [Theory]
    [InlineData("FireCtaButton", "FireCta Compact Danger", ":pointerover")]
    [InlineData("FireCtaButton", "FireCta Compact Danger", ":pressed")]
    [InlineData("FirePrimaryButton", "FirePrimary Primary", ":pointerover")]
    [InlineData("FirePrimaryButton", "FirePrimary Primary", ":pressed")]
    public async Task AFireTemplateButton_ShowsNoBoxBehindItsLabel_OnHoverAndPress(string theme, string classes, string state)
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var button = new Button
            {
                Content = "Delete",
                Theme = (Avalonia.Styling.ControlTheme)Application.Current!.FindResource(theme)!
            };
            button.Classes.AddRange(classes.Split(' '));
            WithLaidOut(button, () =>
            {
                var presenter = PresenterOf(button);
                var fill = Part<Border>(button, "Cut");
                var resting = Colour(fill.Background);
                Force(button, state);
                Assert.True(Colour(presenter.Background) is null or { A: 0 },
                    $"{classes}{state}: the content strip is painted {Colour(presenter.Background)}");
                Assert.Equal(resting, Colour(fill.Background));
            });
        }, CancellationToken.None);
    }

    /// <summary>A disabled button is its resting self, dimmed — not Fluent's disabled fill.</summary>
    [Theory]
    [InlineData("Secondary")]
    [InlineData("Subtle")]
    [InlineData("Danger")]
    [InlineData("Subtle Danger")]
    public async Task ADisabledButton_KeepsItsRestingColours(string cls)
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var button = new Button { Content = "Delete" };
            button.Classes.AddRange(cls.Split(' '));
            button.Themed();
            WithLaidOut(button, () =>
            {
                var presenter = PresenterOf(button);
                var (fill, edge, label) =
                    (Colour(presenter.Background), Colour(presenter.BorderBrush), Colour(presenter.Foreground));
                button.IsEnabled = false;
                button.UpdateLayout();
                Assert.Equal((fill, edge, label),
                    (Colour(presenter.Background), Colour(presenter.BorderBrush), Colour(presenter.Foreground)));
            });
        }, CancellationToken.None);
    }

    /// <summary>Graphite: an ON toggle is a tint with an accent label, never a solid accent block.</summary>
    [Theory]
    [InlineData("Chip")]
    [InlineData("Seg")]
    [InlineData("RibbonChip")]
    public async Task ACheckedToggle_IsATint(string cls)
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var toggle = new ToggleButton { Classes = { cls }, Content = "PM", IsChecked = true }.Themed();
            WithLaidOut(toggle, () =>
            {
                var presenter = PresenterOf(toggle);
                Assert.Equal(Token(toggle, "AccentSubtle"), Colour(presenter.Background));
                Assert.Equal(Token(toggle, "AccentColor"), Colour(presenter.Foreground));
            });
        }, CancellationToken.None);
    }

    /// <summary>The inspector restates Chip and Seg in its own grammar; its checked tint must reach the presenter too.</summary>
    [Theory]
    [InlineData("Chip")]
    [InlineData("Seg")]
    public async Task InTheInspector_ACheckedToggle_WearsTheInspectorsTint(string cls)
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var toggle = new ToggleButton { Classes = { cls, "Mini" }, Content = "PM", IsChecked = true };
            var scope = new UserControl { Content = toggle };
            scope.Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://DotCharter/"))
            {
                Source = new Uri("avares://DotCharter/Views/Editor/InspectorStyles.axaml")
            });
            WithLaidOut(scope, () =>
            {
                var presenter = PresenterOf(toggle);
                Assert.Equal(Token(toggle, "FireAccentTintStrong"), Colour(presenter.Background));
                Assert.Equal(Token(toggle, "FireTextBright"), Colour(presenter.Foreground));
            });
        }, CancellationToken.None);
    }

    /// <summary>
    ///     A disabled dialog primary is NEUTRAL, one surface: control-theme styles
    ///     rank below app styles, so <c>Button.Primary</c>'s accent beat the Fire
    ///     theme's own disabled fill, and the plain Primary's disabled presenter rule
    ///     painted a grey box inside the Fire template.
    /// </summary>
    [Fact]
    public async Task ADisabledDialogPrimary_IsOneNeutralSurface()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var primary = new Button { Classes = { "DlgAction", "Primary" }, Content = "Restore selected", IsEnabled = false };
            var footer = new Border { Classes = { "DialogFooter" }, Child = primary };
            var container = new DotCharter.UI.Views.Shell.DialogContainerView();
            container.DialogPanel.Children.Clear();
            container.DialogPanel.Children.Add(footer);
            WithLaidOut(container, () =>
            {
                var cut = Part<Border>(primary, "Cut");
                Assert.Equal(Token(primary, "FireSurfaceChip"), Colour(cut.Background));
                var presenter = PresenterOf(primary);
                Assert.True(presenter.Background is null || Colour(presenter.Background)?.A == 0,
                    $"the disabled primary paints a second box behind its label: {Colour(presenter.Background)}");
                Assert.Equal(Token(primary, "FireTextDim"), Colour(presenter.Foreground));
            });
        }, CancellationToken.None);
    }

    /// <summary>The mixed-selection chip warns that a click will stomp data; it must not read as "off".</summary>
    [Fact]
    public async Task AnIndeterminateChip_ShowsTheWarningEdge()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var chip = new ToggleButton { Classes = { "Chip" }, Content = "PM", IsThreeState = true, IsChecked = null }.Themed();
            WithLaidOut(chip, () =>
            {
                var presenter = PresenterOf(chip);
                Assert.Equal(Token(chip, "WarningColor"), Colour(presenter.BorderBrush));
                Assert.Equal(Token(chip, "WarningColor"), Colour(presenter.Foreground));
            });
        }, CancellationToken.None);
    }

    [Fact]
    public async Task AnOpenComboBox_AndAFocusedNumberBox_WearTheAccentEdge()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var combo = new ComboBox { ItemsSource = new[] { "One", "Two" }, Width = 200 };
            var number = new NumericUpDown { Width = 200 };
            var panel = new StackPanel { Children = { combo, number } };
            WithLaidOut(panel, () =>
            {
                Force(combo, ":dropdownopen");
                Assert.Equal(Token(combo, "AccentColor"), Colour(combo.BorderBrush));
                Force(number, ":focus-within");
                Assert.Equal(Token(number, "AccentColor"), Colour(number.BorderBrush));
            });
        }, CancellationToken.None);
    }

    [Fact]
    public async Task AnUncheckedCheckBox_AndAnOffSwitch_WearTheAppsColours()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var check = new CheckBox { Content = "Snap" };
            var toggle = new ToggleSwitch();
            var panel = new StackPanel { Children = { check, toggle } };
            WithLaidOut(panel, () =>
            {
                var box = Part<Border>(check, "NormalRectangle");
                Assert.Equal(Token(check, "AppBackground"), Colour(box.Background));
                Assert.Equal(Token(check, "BorderDefault"), Colour(box.BorderBrush));
                Assert.Equal((CornerRadius)check.FindResource("RadiusXs")!, box.CornerRadius);

                var track = Part<Border>(toggle, "OuterBorder");
                Assert.Equal(Token(toggle, "SurfaceHover"), Colour(track.Background));
                Assert.Equal(Token(toggle, "BorderDefault"), Colour(track.BorderBrush));
            });
        }, CancellationToken.None);
    }

    /// <summary>A checked toolbar toggle still answers the pointer.</summary>
    [Fact]
    public async Task ACheckedToolbarToggle_ChangesOnHover()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var toggle = new ToggleButton
            {
                Content = "FX", IsChecked = true, Theme = (Avalonia.Styling.ControlTheme)Application.Current!.FindResource("ToolbarToggleBtn")!
            };
            WithLaidOut(toggle, () =>
            {
                var resting = Colour(toggle.Background);
                Force(toggle, ":pointerover");
                Assert.NotEqual(resting, Colour(toggle.Background));
            });
        }, CancellationToken.None);
    }

    private static void Force(Control control, string pseudoClass)
    {
        ((IPseudoClasses)control.Classes).Set(pseudoClass, true);
        control.UpdateLayout();
    }

    private static ContentPresenter PresenterOf(TemplatedControl control) =>
        Part<ContentPresenter>(control, "PART_ContentPresenter");

    private static T Part<T>(Control control, string name) where T : Control
    {
        var part = control.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name);
        Assert.True(part != null, $"{control.GetType().Name} has no {typeof(T).Name} '{name}' — the template changed");
        return part!;
    }

    private static Color Token(Control scope, string key)
    {
        Assert.True(scope.TryFindResource(key, out var value), $"{key} did not resolve");
        return Colour(value as IBrush) ?? (value is Color c ? c : throw new InvalidOperationException(key));
    }

    /// <summary>A solid brush's colour, or a gradient's first stop; null for no brush.</summary>
    private static Color? Colour(IBrush? brush) => brush switch
    {
        ISolidColorBrush solid => solid.Color,
        IGradientBrush { GradientStops.Count: > 0 } gradient => gradient.GradientStops[0].Color,
        _ => null
    };

    /// <summary>Lays the control out in a shown window with every transition off, so a read is the settled value.</summary>
    private static void WithLaidOut(Control view, Action assert)
    {
        var window = new Window { Width = 480, Height = 320, Content = view };
        window.Show();
        try
        {
            view.UpdateLayout();
            foreach (var animated in view.GetSelfAndVisualDescendants().OfType<Avalonia.Animation.Animatable>())
                animated.Transitions = null;
            assert();
        }
        finally
        {
            window.Content = null;
            window.Close();
        }
    }
}
