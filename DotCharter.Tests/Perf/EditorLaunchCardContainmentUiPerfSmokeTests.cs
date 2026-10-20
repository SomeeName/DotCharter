using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using DotCharter.Localization;
using DotCharter.UI.Views.Shell;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The chart editor's LAUNCH CARD (<see cref="EditorLaunchCardView" />) —
///     the empty-state door that offers New Project / Open Project / Attach
///     audio — must keep every one of those buttons INSIDE the card, in every
///     shipped language and at every shipped screen width.
///     <para>
///         It did not. The three verbs sat in a horizontal <c>StackPanel</c>
///         inside a card capped at 440 px, and a horizontal StackPanel arranges
///         each child at its own <c>DesiredSize</c> from x=0 and clips nothing
///         (docs/agents/ui/layout-traps.md § "Text that runs out of its
///         container"). So the panel reported a correct rectangle and DREW
///         outside it: in de-DE — <c>Neues Projekt</c> / <c>Projekt öffnen...</c>
///         / <c>Audio hinzufügen…</c> — the third button hung off the card's
///         right edge, which is exactly the owner's report ("the far right
///         button in it was half out of the dialog"). The card is
///         <c>MaxWidth</c>-capped and centred, so the overflow is
///         WIDTH-INDEPENDENT: 1920×1080 fails the same way 1366×768 does, and
///         only the LANGUAGE decides whether it shows — which is why it never
///         appeared in English.
///     </para>
///     <para>
///         The fix is a <see cref="WrapPanel" />: the cluster sizes to its
///         content and re-flows to a second line when the line cannot hold it,
///         the guidance for exactly this shape in
///         docs/agents/ui/layout-traps.md § Columns. This class is what would
///         fail if the row went back — restoring the StackPanel reproduces the
///         overflow in all three de-DE rows and all three ja-JP rows.
///     </para>
///     <para>
///         The card is laid out with NO DataContext, like the dialog smoke
///         tests: every <c>{Binding}</c> then produces no value and each
///         property keeps its default (the harshest case — the MRU section is
///         visible and empty), while <c>{localization:Loc}</c> is a markup
///         extension and still yields real localized text. What is under test is
///         the LAYOUT.
///     </para>
///     <para>
///         Named <c>*UiPerfSmokeTests</c> and in the <c>"HeadlessUI"</c>
///         collection because it boots the real Avalonia session — it needs its
///         OWN <c>dotnet test</c> invocation (root AGENTS.md § Validation).
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class EditorLaunchCardContainmentUiPerfSmokeTests
{
    /// <summary>The owner's brother's screen first, then two laptop classes.
    /// The card is MaxWidth-capped, so these differ only in how much room it is
    /// CENTRED in — a card whose row overflows does so at all three, and the
    /// ladder is here so a future width-dependent cap cannot hide in the gap
    /// between them.</summary>
    private static readonly (double W, double H)[] Screens =
        [(1920, 1080), (1600, 900), (1366, 768)];

    /// <summary>Every shipped resx. German is the longest and is the culture the
    /// report came from; Japanese is shorter in characters and wider per glyph.</summary>
    private static readonly string[] Cultures = ["en-US", "de-DE", "ja-JP"];

    /// <summary>A pixel of rounding is not an overflow.</summary>
    private const double Tolerance = 0.5;

    [Fact]
    public async Task TheLaunchCardVerbs_StayInsideTheCard_AtEveryShippedSizeAndCulture()
    {
        var offenders = new List<string>();
        var captions = new Dictionary<string, string>(StringComparer.Ordinal);
        var log = new List<string>();

        await HeadlessSessionFixture.Dispatch(() =>
        {
            var restore = LocalizationService.Instance.ConfiguredCulture;
            try
            {
                foreach (var culture in Cultures)
                {
                    LocalizationService.Instance.SetConfiguredCulture(culture);
                    foreach (var (w, h) in Screens)
                        MeasureOnce(culture, w, h, offenders, captions, log);
                }
            }
            finally
            {
                LocalizationService.Instance.SetConfiguredCulture(restore);
                CultureInfo.CurrentUICulture = LocalizationService.Instance.Culture;
            }
        }, CancellationToken.None);

        // Proof the sweep is not measuring English three times: a culture that
        // never moved would make this a one-entry set and the de-DE/ja-JP rows
        // above would assert nothing at all.
        Assert.True(captions.Values.Distinct(StringComparer.Ordinal).Count() == Cultures.Length,
            "the three cultures produced " +
            $"{captions.Values.Distinct(StringComparer.Ordinal).Count()} distinct verb rows — " +
            "SetConfiguredCulture did not reach the card's {localization:Loc} captions, so this sweep " +
            "graded one language three times: " +
            string.Join(" | ", captions.Select(kv => $"{kv.Key}: {kv.Value}")));

        // The measured table, for the next reader: card widths are 447 (en-US),
        // 499 (de-DE) and 508 (ja-JP) px against a 560 px cap on 2026-09-12, so
        // every language still reads as ONE line. Logged, not asserted — the
        // WrapPanel is the guarantee; this is how a string that outgrows the cap
        // is noticed rather than silently re-flowed.
        foreach (var line in log) Console.WriteLine(line);

        Assert.True(offenders.Count == 0,
            "The editor launch card draws outside itself (docs/agents/ui/layout-traps.md — a horizontal " +
            "StackPanel arranges from x=0 and clips nothing):\n  " + string.Join("\n  ", offenders));
    }

    private static void MeasureOnce(
        string culture, double width, double height,
        List<string> offenders, Dictionary<string, string> captions, List<string> log)
    {
        // Built on the dispatcher thread on purpose: a view constructed on the
        // xUnit thread cannot parse geometry and cannot be adopted by a window.
        var view = new EditorLaunchCardView();
        // The card is centre-aligned, so it is NOT the full window — the host
        // grid is what gives the window-containment check a screen-sized frame
        // to translate into.
        var host = new Grid();
        host.Children.Add(view);
        var window = new Window { Width = width, Height = height, Content = host };

        // Show() is what roots the tree and raises OnAttachedToLogicalTree,
        // where the buttons pick up their padding from Buttons.axaml — without
        // it every width measured here would be an unstyled one. Layout runs
        // through the VIEW; measuring a headless Window reports "Invalid size
        // returned for Measure".
        window.Show();
        try
        {
            host.Measure(new Size(width, height));
            host.Arrange(new Rect(0, 0, width, height));

            var card = view.GetLogicalDescendants().OfType<Border>()
                .FirstOrDefault(b => b.Name == "LaunchCardSurface");
            Assert.True(card is not null,
                "EditorLaunchCardView no longer holds a Border named LaunchCardSurface — this test is " +
                "scanning for a surface that moved and would pass vacuously.");

            Assert.True(card!.Bounds.Width > 1 && card.Bounds.Height > 1,
                $"the launch card laid out {card.Bounds} at {culture} {width:0}x{height:0} — it measured " +
                "nothing, so nothing below this line proves anything");

            var buttons = card.GetLogicalDescendants().OfType<Button>()
                .Where(b => b.IsEffectivelyVisible)
                .ToList();
            Assert.True(buttons.Count >= 3,
                $"the launch card shows {buttons.Count} buttons at {culture} — its three verbs are gone");

            captions[culture] = string.Join(" / ", buttons.Take(3).Select(Label));

            // LOGGED, never asserted. The cap is sized so all three languages
            // still read as one line (root AGENTS.md: no wall-clock or
            // pixel-target assertions in a layout gate) — the WrapPanel is the
            // GUARANTEE, this line is how a future string that no longer fits
            // is noticed. One line means every verb shares a top edge.
            var verbs = buttons.Take(3).Select(b => (b.TranslatePoint(default, card) ?? default).Y).ToList();
            log.Add(
                $"[launch card] {culture,-5} {width:0}x{height:0}: card {card.Bounds.Width:0}x{card.Bounds.Height:0} px, " +
                $"verbs on {verbs.Distinct().Count()} line(s) — {captions[culture]}");

            foreach (var button in buttons)
            {
                var origin = button.TranslatePoint(default, card) ?? default;
                var right = origin.X + button.Bounds.Width;
                var bottom = origin.Y + button.Bounds.Height;
                var label = Label(button);

                if (origin.X < -Tolerance || right > card.Bounds.Width + Tolerance)
                {
                    offenders.Add(
                        $"{culture} {width:0}x{height:0}: \"{label}\" spans x {origin.X:0.#}..{right:0.#} in a " +
                        $"card {card.Bounds.Width:0.#} px wide — " +
                        $"{Math.Max(-origin.X, right - card.Bounds.Width):0.#} px outside it");
                }

                if (origin.Y < -Tolerance || bottom > card.Bounds.Height + Tolerance)
                {
                    offenders.Add(
                        $"{culture} {width:0}x{height:0}: \"{label}\" spans y {origin.Y:0.#}..{bottom:0.#} in a " +
                        $"card {card.Bounds.Height:0.#} px tall — " +
                        $"{Math.Max(-origin.Y, bottom - card.Bounds.Height):0.#} px outside it");
                }

                // …and inside the WINDOW. The card is centred, so a row wider
                // than the screen would run off both sides of it as well.
                var inWindow = button.TranslatePoint(default, host) ?? default;
                if (inWindow.X < -Tolerance || inWindow.X + button.Bounds.Width > width + Tolerance)
                {
                    offenders.Add(
                        $"{culture} {width:0}x{height:0}: \"{label}\" spans x {inWindow.X:0.#}.." +
                        $"{inWindow.X + button.Bounds.Width:0.#} in a {width:0} px window");
                }
            }
        }
        finally
        {
            window.Close();
        }
    }

    private static string Label(Button button) => button.Content switch
    {
        string s => s,
        null => "<no content>",
        var other => other.ToString() ?? "<?>"
    };
}
