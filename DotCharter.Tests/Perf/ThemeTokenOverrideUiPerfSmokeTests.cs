using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using DotCharter.UI.Theming;

namespace DotCharter.Tests.Perf;

/// <summary>
///     <b>Runtime theme switching still works now that the colour tokens are
///     MERGED rather than declared inline.</b>
///     <para>
///         <c>ThemeService</c> writes <c>Application.Current.Resources[key]</c>.
///         An entry set on a <c>ResourceDictionary</c> itself is looked up
///         BEFORE its merged dictionaries, so the write shadows the merged
///         default and every <c>DynamicResource</c> consumer re-evaluates — but
///         that is a property of Avalonia's lookup order, not of this repo, and
///         nothing else in the suite would notice if it changed. So it is
///         asserted end to end: the raw token, and the BRUSH over it, which
///         lives in another dictionary (<c>Styles/Resources/ThemeSurfaces.axaml</c>)
///         and reaches the colour across the dictionary boundary.
///     </para>
///     <para>Needs the real Avalonia app session, hence <c>*UiPerfSmokeTests</c>.</para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class ThemeTokenOverrideUiPerfSmokeTests
{
    /// <summary>
    ///     The app palette, and — since the Fire palette was tokenised on
    ///     2026-09-20 — the Fire layer too. Fire used to be literal brushes, so
    ///     a theme write stopped at every Play Mode surface; the rewrite needs a
    ///     design proposal to be able to BE a theme
    ///     (<c>docs/agents/ui/target-architecture.md</c>).
    /// </summary>
    [Theory]
    [InlineData("AccentColorValue", "AccentColor")]
    [InlineData("FireGroundColor", "FireGround")]
    public async Task SetColor_OnAMergedToken_IsSeenByADynamicResourceConsumer(string tokenKey, string brushKey)
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var app = Application.Current!;
            Assert.True(app.TryFindResource(tokenKey, out var found) && found is Color,
                $"{tokenKey} does not resolve at all — the merged ColorTokens dictionary is not reachable");
            var original = (Color)found!;
            var hadOwnEntry = app.Resources.ContainsKey(tokenKey);

            var probe = new Border();
            var window = new Window { Width = 200, Height = 100, Content = probe };
            window.Show();
            try
            {
                // Tag is an ordinary styled property, so binding it to the
                // resource observable is exactly what `{DynamicResource}` does.
                probe.Bind(Control.TagProperty, probe.GetResourceObservable(tokenKey));
                window.UpdateLayout();
                Assert.Equal(original, (Color)probe.Tag!);

                var wanted = Color.FromRgb(0x11, 0x22, 0x33);
                Assert.NotEqual(original, wanted);

                new ThemeService().SetColor(tokenKey, wanted);
                window.UpdateLayout();

                Assert.Equal(wanted, (Color)probe.Tag!);

                Assert.True(probe.TryFindResource(brushKey, out var brush) && brush is SolidColorBrush,
                    $"{brushKey} does not resolve to a SolidColorBrush");
                Assert.Equal(wanted, ((SolidColorBrush)brush!).Color);
            }
            finally
            {
                // Put the shared session's palette back. Removing the OWN entry
                // (rather than writing the old value into it) restores the
                // merged default, which is the state every other case expects.
                if (hadOwnEntry) app.Resources[tokenKey] = original;
                else app.Resources.Remove(tokenKey);
                window.Close();
            }
        }, TestContext.Current.CancellationToken);
    }
}
