using System.Text.RegularExpressions;

namespace DotCharter.Tests.UI;

/// <summary>
///     A style class that matches no selector is a SILENT no-op.
///     <para>
///         Avalonia does not warn, the build is clean and the control simply
///         renders in its default look — so a button meant to be the prominent
///         action on its panel quietly becomes an ordinary grey one, and nobody
///         notices until they look at that exact screen. Two shipped this way
///         (<c>Classes="Accent"</c> on Play Mode's guest-start button and on the
///         pedalboard's save-lyrics button; the real class is
///         <c>Primary</c>), and both were found by a reviewer reading markup,
///         which does not scale.
///     </para>
///     <para>
///         This is a source lint rather than a runtime check because the failure
///         has no runtime signal at all — there is nothing to assert on a live
///         control that distinguishes "styled by a matching selector" from
///         "styled by the default".
///     </para>
/// </summary>
public sealed class ButtonStyleClassTests
{
    /// <summary>
    ///     Deliberately EMPTY. Every entry here would be a hole in the lint, and
    ///     the codebase currently needs none — each of the 27 button classes in
    ///     markup has a real selector, including the ones declared as nested
    ///     <c>^.Class</c> inside a ControlTheme. If a genuine case appears (a
    ///     class only ever attached from code-behind), add it WITH the reason.
    /// </summary>
    private static readonly HashSet<string> Exempt = new(StringComparer.Ordinal);

    [Fact]
    public void EveryButtonStyleClassInMarkup_HasASelectorThatCanMatchIt()
    {
        var uiRoot = Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI");
        var axaml = UiSourceTree.EnumerateFiles(uiRoot, "*.axaml", SearchOption.AllDirectories).ToList();
        Assert.NotEmpty(axaml);

        // Everything a selector could bind a class to. Both spellings matter:
        // a top-level `Selector="Button.Primary"` and the nested `^.CloseBtn`
        // form used inside a ControlTheme (which is how the window chrome's
        // close button is styled — a lint that only knew the first spelling
        // would report it as undefined).
        // TYPE-AWARE: a class only counts for a Button when the selector step
        // that names it could match a Button — `Button.X`, a typeless `.X`,
        // `^.X` inside a ControlTheme, or `:is(Button).X`. A type-blind set let
        // `ComboBox.FireCombo.Bare` stand in for Home's `Button Classes="Bare"`,
        // which matched nothing and drew a second frame round every card.
        var defined = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in axaml)
        {
            var markup = UiSourceTree.ReadAllText(file);
            foreach (Match m in Regex.Matches(markup, @"Selector=""(?<s>[^""]*)"""))
            {
                foreach (Match step in Regex.Matches(m.Groups["s"].Value,
                         @"(?<type>:is\([A-Za-z]+\)|\^|[A-Za-z_][A-Za-z0-9_|]*)?(?<classes>(?:\.[A-Za-z_][A-Za-z0-9_]*)+)"))
                {
                    var type = step.Groups["type"].Value;
                    var couldBeButton = type is "" or "^" or "Button" or ":is(Button)";
                    if (!couldBeButton) continue;
                    foreach (Match c in Regex.Matches(step.Groups["classes"].Value, @"\.([A-Za-z_][A-Za-z0-9_]*)"))
                        defined.Add(c.Groups[1].Value);
                }
            }
        }

        Assert.NotEmpty(defined);

        var offenders = new List<string>();
        foreach (var file in axaml)
        {
            var markup = UiSourceTree.ReadAllText(file);
            // `<Button ... Classes="A B">`, literal only — a bound
            // `Classes.Foo="{Binding}"` is a different construct and is not the
            // failure this guards.
            foreach (Match m in Regex.Matches(
                         markup, @"<Button\b[^>]*?\bClasses=""(?<classes>[^""{]+)""", RegexOptions.Singleline))
            {
                foreach (var cls in m.Groups["classes"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (defined.Contains(cls) || Exempt.Contains(cls)) continue;
                    offenders.Add($"{Path.GetFileName(file)}: Button Classes=\"{cls}\" matches no selector");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "Style class(es) that silently do nothing — the button renders in its default look:\n  " +
            string.Join("\n  ", offenders.Distinct()));
    }
}
