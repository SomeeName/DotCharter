using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The MEASURED half of the Fire search field. Its source-lint sibling is
///     <c>UI/FireFieldInputPairingTests</c>, which pins that every
///     <c>TextBox.FireFieldInput</c> is inside a <c>Border.FireField</c>; this
///     one attaches the pair to a real window, lays it out, and asserts the
///     geometry the owner actually looks at.
///     <para>
///         <b>Why a bounds assertion and not "it laid out".</b> Every fault in
///         this family arranges perfectly. v0.44's field measured 320 × 36 for
///         the frame and 294 × <b>18</b> for the input inside it: the class
///         clears <c>Height</c> and <c>MinHeight</c>, and the app-wide
///         <c>Style Selector="TextBox, ComboBox, NumericUpDown"</c>
///         (Styles/Inputs.axaml) then supplied <c>VerticalAlignment=Center</c>,
///         so the input collapsed to one text line floating in the middle of the
///         frame. Nothing is zero-sized, nothing throws, and the only visible
///         symptom is that the focus ring and the selection band cover a strip
///         through the middle of the box instead of the box — which is exactly
///         what was reported. A smoke test that asserted "measured non-zero"
///         passed throughout.
///     </para>
///     <para>
///         The focus half has its own trap: Fluent's TextBox
///         <c>ControlTheme</c> paints hover and focus by setting
///         <c>Background</c> / <c>BorderThickness</c> on
///         <c>PART_BorderElement</c> from <c>/template/</c> setters, which beat
///         the TemplateBinding that would have carried the class's
///         <c>BorderThickness=0</c>. So the input drew a 2 px accent rectangle
///         of its own inside the frame no matter what the class said about
///         borders. The frame carries the state now
///         (<c>Border.FireField:focus-within</c>) and the input's is suppressed.
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class FireFieldUiPerfSmokeTests
{
    private const double W = 700;
    private const double H = 420;

    /// <summary>Vertical slack allowed between the input and the frame's
    /// interior. Zero in practice; the tolerance is for layout rounding, not
    /// for a design decision.</summary>
    private const double Slack = 0.51;

    private static (Border Frame, TextBox Box) Field(bool multiline = false)
    {
        var box = new TextBox
        {
            Classes = { "FireFieldInput" },
            Text = "Kryptonite",
            PlaceholderText = "Search songs"
        };
        // The frame wears its ControlTheme the way every view's does (#240): the field
        // rules are gated on the class INSIDE the theme, so a bare classed Border is unstyled.
        var frame = new Border
        {
            Theme = (Avalonia.Styling.ControlTheme)Application.Current!.FindResource("FireFieldBorder")!,
            Classes = { "FireField" },
            Width = 320,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Child = box
        };

        if (!multiline) return (frame, box);

        box.Classes.Add("Multiline");
        box.AcceptsReturn = true;
        box.TextWrapping = TextWrapping.Wrap;
        box.MinHeight = 72;
        frame.Classes.Add("Tall");
        return (frame, box);
    }

    /// <summary>The frame's interior — what the input has to fill.</summary>
    private static (double Top, double Bottom, double Height) Interior(Border frame) =>
        (frame.BorderThickness.Top + frame.Padding.Top,
            frame.BorderThickness.Bottom + frame.Padding.Bottom,
            frame.Bounds.Height
            - frame.BorderThickness.Top - frame.BorderThickness.Bottom
            - frame.Padding.Top - frame.Padding.Bottom);

    // ── 1. the input fills the frame it is drawn inside ─────────────────────

    /// <summary>
    ///     THE contract. The input carries no chrome of its own, so whatever the
    ///     frame encloses is what the user reads as "the box" — and every state
    ///     the input still draws for itself (selection, caret, and before this
    ///     fix a focus rectangle) is clipped to the input's own bounds. An input
    ///     shorter than its frame therefore highlights a band, not a field.
    /// </summary>
    [Fact]
    public async Task TheInput_FillsTheFramesInterior()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var (frame, box) = Field();
            WithLaidOut(frame, () =>
            {
                AssertMeasured(frame, "Border.FireField");
                AssertMeasured(box, "TextBox.FireFieldInput");

                var interior = Interior(frame);
                Assert.True(interior.Height > 0.5,
                    $"the frame has no interior at all ({frame.Bounds}) — nothing below can mean anything");

                Assert.True(Math.Abs(box.Bounds.Height - interior.Height) <= Slack,
                    $"the input is {box.Bounds.Height} tall inside a frame whose interior is {interior.Height} " +
                    $"(frame {frame.Bounds}, border {frame.BorderThickness}, padding {frame.Padding}). It has to " +
                    "FILL the frame: its selection band, its caret and its focus state are all clipped to its own " +
                    "bounds, so a short input highlights a strip through the middle of the field. The setter that " +
                    "does this is VerticalAlignment=Stretch on TextBox.FireFieldInput — without it the app-wide " +
                    "TextBox style supplies Center and the box collapses to one text line.");

                var top = box.TranslatePoint(new Point(0, 0), frame)!.Value.Y;
                Assert.True(Math.Abs(top - interior.Top) <= Slack,
                    $"the input starts {top} px into a frame whose interior starts at {interior.Top}");
            });
        }, CancellationToken.None);
    }

    /// <summary>
    ///     …and the text stays vertically centred once it stretches. The two
    ///     setters are a pair: stretching alone would leave the app-wide default
    ///     as the only thing keeping the text off the frame's top edge, which is
    ///     the "text sits too high" half of the same report.
    /// </summary>
    [Fact]
    public async Task TheTextStaysCentred_OnceTheInputStretches()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var (frame, box) = Field();
            WithLaidOut(frame, () =>
            {
                Assert.Equal(VerticalAlignment.Stretch, box.VerticalAlignment);
                Assert.Equal(VerticalAlignment.Center, box.VerticalContentAlignment);

                var presenter = Part(box, "PART_TextPresenter");
                var interior = Interior(frame);
                var top = presenter.TranslatePoint(new Point(0, 0), frame)!.Value.Y - interior.Top;
                var bottom = interior.Height - (top + presenter.Bounds.Height);

                Assert.True(presenter.Bounds.Height > 0.5, "the text presenter has no height");
                Assert.True(Math.Abs(top - bottom) <= 1.01,
                    $"the text sits {top} px from the top of the field's interior and {bottom} px from the " +
                    "bottom — it is not centred, which is exactly what 'the text sits too high' looks like");
            });
        }, CancellationToken.None);
    }

    // ── 2. the horizontal inset the input gave up really is supplied ────────

    /// <summary>
    ///     The class sets <c>Padding=FirePadNone</c>, so the ONLY thing keeping
    ///     the caret off the left edge is the frame's own padding. A token that
    ///     resolved to nothing and a token that resolves to zero look identical
    ///     from the outside, so this asserts the realized gap, not the setter.
    /// </summary>
    [Fact]
    public async Task TheFrame_SuppliesTheHorizontalInsetTheInputGaveUp()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var (frame, box) = Field();
            WithLaidOut(frame, () =>
            {
                Assert.Equal(new Thickness(0), box.Padding);

                var left = box.TranslatePoint(new Point(0, 0), frame)!.Value.X;
                Assert.True(left >= 8,
                    $"the input starts {left} px from the frame's left edge. The input's own Padding is 0 by " +
                    "design, so the frame's FirePadField is the only thing between the caret and the border — " +
                    "and the caret hard against the edge with no padding is the reported defect.");

                var right = frame.Bounds.Width - (left + box.Bounds.Width);
                Assert.True(right >= 8, $"the input runs to {right} px of the frame's right edge");
            });
        }, CancellationToken.None);
    }

    // ── 3. focus lights the FRAME, and nothing draws a second box inside ────

    /// <summary>
    ///     Fluent's <c>^:focus /template/ Border#PART_BorderElement</c> setters
    ///     are not reachable from the control's own <c>BorderThickness</c>, so
    ///     the input drew its own accent rectangle inside the frame however the
    ///     class was written. Both halves are asserted: the frame changes, and
    ///     the input still does not.
    /// </summary>
    [Fact]
    public async Task Focus_LightsTheWholeFrame_AndDrawsNoSecondBoxInsideIt()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var (frame, box) = Field();
            WithLaidOut(frame, () =>
            {
                var rest = Colour(frame.BorderBrush);
                Assert.True(rest != null, "the frame has no border brush at rest");

                box.Focus();
                frame.UpdateLayout();

                Assert.True(box.IsFocused, "the input did not take focus — the rest of this proves nothing");
                Assert.Contains(":focus-within", frame.Classes);

                Assert.NotEqual(rest, Colour(frame.BorderBrush));

                var inner = Part(box, "PART_BorderElement");
                Assert.Equal(new Thickness(0), ((Border)inner).BorderThickness);
                Assert.True(Transparent(((Border)inner).Background),
                    "the focused input paints a fill of its own inside the frame. Fluent sets " +
                    "TextControlBackgroundFocused on PART_BorderElement from a /template/ setter, which the " +
                    "control's own Background cannot override — it needs its own /template/ rule.");
                Assert.True(box.FocusAdorner == null,
                    "the input still declares a focus adorner. It would be drawn around the INPUT, inset from " +
                    "the frame on every side, and read as two nested controls.");
            });
        }, CancellationToken.None);
    }

    // ── 4. the wrapping spelling ────────────────────────────────────────────

    /// <summary>
    ///     A wrapping editor is top-anchored and needs a vertical inset — the
    ///     one-line field's <c>FirePadField</c> has none, and the app-wide
    ///     <c>VerticalContentAlignment=Center</c> put the rig's lyrics editor's
    ///     first line halfway down a 72 px box (measured: presenter at y = 27).
    /// </summary>
    [Fact]
    public async Task TheWrappingField_AnchorsItsFirstLineToTheTop_WithAirAboveIt()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var (frame, box) = Field(multiline: true);
            WithLaidOut(frame, () =>
            {
                AssertMeasured(frame, "Border.FireField.Tall");
                Assert.Equal(VerticalAlignment.Top, box.VerticalContentAlignment);

                Assert.True(frame.Padding.Top >= 6,
                    $"the tall frame's vertical inset is {frame.Padding.Top} — a top-anchored editor with no " +
                    "inset puts its first line hard against the border");

                var presenter = Part(box, "PART_TextPresenter");
                var interior = Interior(frame);
                var top = presenter.TranslatePoint(new Point(0, 0), frame)!.Value.Y - interior.Top;
                Assert.True(top <= 1.01,
                    $"the first line starts {top} px into the interior of a {frame.Bounds.Height} px editor — " +
                    "it is being centred, not anchored");
            });
        }, CancellationToken.None);
    }

    // ── 5. …and the real screens, not just the composition ──────────────────

    /// <summary>
    ///     The same two assertions against every field the shipped views
    ///     actually build. The composition tests above would stay green while a
    ///     view drifted, which is how four unframed inputs shipped.
    /// </summary>
    [Theory]
    [InlineData("LibraryView")]
    public async Task EveryFireFieldInputInARealView_IsFramedAndFillsIt(string viewName)
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            Control view = viewName switch
            {
                "LibraryView" => new DotCharter.UI.Views.Library.LibraryView(),
                _ => throw new ArgumentOutOfRangeException(nameof(viewName), viewName, "unknown view")
            };

            WithLaidOut(view, () =>
            {
                AssertMeasured(view, viewName);

                var inputs = view.GetSelfAndVisualDescendants().OfType<TextBox>()
                    .Where(t => t.Classes.Contains("FireFieldInput"))
                    .ToList();

                Assert.True(inputs.Count > 0,
                    $"{viewName} realized no TextBox.FireFieldInput at all — this case is looking at nothing");

                foreach (var box in inputs)
                {
                    var frame = box.GetVisualAncestors().OfType<Border>()
                        .FirstOrDefault(b => b.Classes.Contains("FireField"));
                    Assert.True(frame != null,
                        $"{viewName}: a TextBox.FireFieldInput has no Border.FireField around it — it renders " +
                        "as bare text with no box, no padding and nothing to focus into");

                    var interior = Interior(frame!);
                    Assert.True(Math.Abs(box.Bounds.Height - interior.Height) <= Slack,
                        $"{viewName}: the input measured {box.Bounds.Height} inside a frame interior of " +
                        $"{interior.Height} ({frame!.Bounds}) — its focus and selection cover a band, not the field");
                }
            });
        }, CancellationToken.None);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static Control Part(Control root, string name)
    {
        var part = root.GetSelfAndVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == name);
        Assert.True(part != null, $"the TextBox template has no {name} — the control never templated");
        return part!;
    }

    private static Color? Colour(IBrush? brush) => (brush as ISolidColorBrush)?.Color;

    private static bool Transparent(IBrush? brush) => brush is null || Colour(brush)?.A == 0;

    private static void WithLaidOut(Control view, Action assert)
    {
        var window = new Window { Width = W, Height = H, Content = view };
        window.Show();
        try
        {
            view.Measure(new Size(W, H));
            view.Arrange(new Rect(0, 0, W, H));
            view.UpdateLayout();
            assert();
        }
        finally
        {
            window.Content = null;
            window.Close();
        }
    }

    private static void AssertMeasured(Control control, string what) =>
        Assert.True(control.Bounds.Width > 0.5 && control.Bounds.Height > 0.5,
            $"{what} laid out as {control.Bounds} — a control whose styles failed still arranges, it just has " +
            "no size, and that is the shape every silent XAML fault in this repo takes");
}
