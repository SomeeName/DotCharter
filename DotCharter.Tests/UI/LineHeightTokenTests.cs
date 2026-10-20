using System.Globalization;
using System.Text.RegularExpressions;

namespace DotCharter.Tests.UI;

/// <summary>
///     <b>Tokenising <c>LineHeight</c> had to be a visual no-op, and this is the
///     proof.</b>
///     <para>
///         The 2026-09-12 resolution audit found 33 <c>LineHeight</c> setters
///         across 16 <c>.axaml</c> files carrying a bare number while the
///         <c>FontSize</c> beside each one went through a token. That is
///         invisible today — <c>MainWindow</c>'s <c>RootScaler</c>
///         <c>LayoutTransformControl</c> scales the whole shell uniformly, so a
///         literal line box grows with the glyphs — and it becomes an app-wide
///         clip the moment anything ramps the SIZE tokens instead of (or as well
///         as) the transform: <c>FontSizeHero</c> 28 -> 42 against a frozen
///         <c>LineHeight="34"</c> cuts every descender.
///     </para>
///     <para>
///         So each setter was repointed at a token whose value is TODAY'S value.
///         "Repointed with the same number" is exactly the claim a reader stops
///         checking, and it is one typo away from being false, so this test
///         RESOLVES every setter through the two ladder files and compares the
///         resulting multiset of numbers against the one measured before the
///         change. A token whose value moves, a setter pointed at the wrong
///         token, a token renamed out from under a setter and a literal
///         reintroduced all fail here.
///     </para>
///     <para>
///         Source-level and multiset-based on purpose. Per-file/per-line
///         expectations rot on the first insertion above them; the multiset
///         answers the only question that matters — "does the theme still
///         produce the same 33 line boxes?" — and is stable under any edit that
///         is not a behaviour change.
///     </para>
/// </summary>
public sealed class LineHeightTokenTests
{
    /// <summary>
    ///     The 33 line-box values the theme produced on 2026-09-12, immediately
    ///     before the tokenisation, keyed value -> count. Measured, not copied:
    ///     <c>grep -rn 'Property="LineHeight" Value="[0-9]'</c> over
    ///     <c>DotCharter.UI/**/*.axaml</c>.
    ///     <para>
    ///         <b>Changing an entry here is a VISUAL change.</b> Do it
    ///         deliberately and say so in the commit message; do not "fix" a red
    ///         build by editing this table to match what the theme now does.
    ///     </para>
    /// </summary>
    private static readonly IReadOnlyDictionary<double, int> BeforeTokenisation =
        new Dictionary<double, int>
        {
            // 2026-09-21, Graphite settings: 14 px 2 -> 1, a deliberate VISUAL
            // change. The settings rail's two-line tab description
            // (`SideTabDesc`, FireLineHeightXsTight) is gone; the description
            // is the rail row's tooltip now.
            [14] = 1, [15] = 1,
            // 2026-09-16, W4: 8 -> 9. ONE new 16 px line box, and it is not a
            // visual change at all — `TextBlock.RigRoleBody` is the rig's BODY
            // type role, declared once in `Styles/Fire/FireRigRolesAndTones.axaml` over the
            // same `FireSizeMeta` 11.5 / `FireLineHeightXs` 16 pair
            // `TextBlock.FireRowDesc.Wide` already used. The role exists so the
            // rig's tone surfaces and module editor carry a JOB at the call site
            // instead of one of seven faces; the box it produces is the box
            // those call sites already had.
            [16] = 9, [18] = 2,
            // 2026-10-08, #428: 19 px 1 -> 2, and NOT a visual change. The
            // dialog grammar's `DlgBody` rule is stated twice since its
            // TextBlock half became the `DlgBodyText` ControlTheme: once there
            // and once in the `SelectableTextBlock.DlgBody` style the message
            // dialog's selectable body still wears. Same box, two declarations.
            [19] = 2,
            // 2026-09-19: 20 px 6 -> 4, the couch co-op and LAN multiplayer HUD
            // strips were removed; 21 px 1 -> 0 with the LAN board's grammar
            // (`Views/Multiplayer/BoardStyles.axaml`), which also took one of
            // the three 34 px boxes.
            // 2026-09-13, lane F: 4 -> 6. TWO new 20 px line boxes, and they are
            // a deliberate VISUAL change at the WIDE breakpoint only. The
            // gameplay HUD strip (`Views/Multiplayer/MultiplayerHudStrip.axaml`)
            // steps its cells
            // FontSizeSm 12 -> FontSizeBase 14 above 2560 inner DIPs, and those
            // cells wear `Classes="Mono …"` — `TextBlock.Mono` pins a 16 px line
            // box, and 14 px of Space Grotesk in one renders as a clipped middle
            // band. The size and the box move together or neither moves.
            [20] = 4,
            // 2026-09-22: the three Fire FIGURE boxes grew to ~1.25× their
            // size (22 -> 28, 28 -> 36 twice, 60 -> 72). A 1.0× box clipped the
            // comma under the baseline, so every score read "1.940.200".
            // 2026-10-10, #601: 36 px 2 -> 1, a deliberate VISUAL change. The
            // Sound tab's "needs work" tiles (`TextBlock.StatTileCount`) are gone:
            // nothing selected leads with the next task now.
            [22] = 1, [24] = 1, [26] = 3, [28] = 1, [30] = 1, [34] = 2, [36] = 1,
            // 2026-09-13, resolution audit §4 P0.2: ONE new line box, and it is
            // a deliberate VISUAL change. `PlayModeView`'s results stat figures
            // step FontSizeHero 28 -> FontSizeHeroLg 40 at `Window.Wide`, and a
            // raised figure inside a frozen 34 px line box renders as a clipped
            // middle band — which is the fault `TextBlock.StatFigure`'s own
            // comment records. The two move together or neither moves.
            [48] = 1,
            [62] = 1, [72] = 1
        };

    /// <summary>The two files that may declare a line-height step. Anywhere else
    /// is a third ladder, which is how a screen ends up half converted.</summary>
    private static readonly string[] LadderFiles =
    [
        Path.Combine("Styles", "Typography.axaml"),
        Path.Combine("Styles", "FireTokens", "Ladders.axaml")
    ];

    [Fact]
    public void EveryLineHeightSetter_ResolvesToTheValueItHadBeforeTokenisation()
    {
        var tokens = LineHeightTokens();
        var actual = new Dictionary<double, int>();
        var unresolved = new List<string>();

        foreach (var file in AxamlFiles())
        {
            foreach (Match m in Regex.Matches(UiSourceTree.ReadAllText(file),
                     @"<Setter\s+Property=""LineHeight""\s+Value=""\{(?:Dynamic|Static)Resource\s+(?<key>[A-Za-z0-9_]+)\}""\s*/>"))
            {
                var key = m.Groups["key"].Value;
                if (!tokens.TryGetValue(key, out var value))
                {
                    unresolved.Add($"{Path.GetFileName(file)} -> {key}");
                    continue;
                }

                actual[value] = actual.GetValueOrDefault(value) + 1;
            }
        }

        Assert.True(unresolved.Count == 0,
            "these LineHeight setters name a token that neither ladder declares, so they resolve to " +
            "Avalonia's fallback (0) and the text collapses to a sliver:\n  " + string.Join("\n  ", unresolved));

        Assert.Equal(
            Describe(BeforeTokenisation),
            Describe(actual));
    }

    /// <summary>
    ///     …and no setter may go back to a literal. Without this the multiset
    ///     above could be satisfied by a file that never adopted a token at all.
    /// </summary>
    [Fact]
    public void NoLineHeightIsWrittenAsALiteral()
    {
        var offenders = new List<string>();
        foreach (var file in AxamlFiles())
        {
            // Comments are masked, not stripped, so the line numbers below stay
            // honest. Without this the ladder's own header — which quotes
            // `LineHeight="34"` to explain what it replaced — is an offender,
            // and a lint that reports the documentation of the rule is a lint
            // the next agent turns off. (It did, first run.)
            var text = MaskComments(UiSourceTree.ReadAllText(file));
            foreach (Match m in Regex.Matches(text,
                         @"(<Setter\s+Property=""LineHeight""\s+Value=""[0-9]|\bLineHeight=""[0-9])"))
            {
                offenders.Add($"{Path.GetFileName(file)}:{text.Take(m.Index).Count(c => c == '\n') + 1}");
            }
        }

        Assert.True(offenders.Count == 0,
            "a literal LineHeight is half a type step written outside the token table — pair it with the " +
            "FontSize token beside it and add a LineHeight*/FireLineHeight* entry instead:\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>
    ///     The count itself, so a wholesale deletion of the setters cannot pass
    ///     the multiset test by producing an empty one. 33 is the audit's number
    ///     and it is a FLOOR, not a pin: a new tokenised setter is fine, losing
    ///     one silently is not.
    /// </summary>
    [Fact]
    public void TheLadderIsActuallyReached()
    {
        var tokens = LineHeightTokens();
        Assert.True(tokens.Count >= 20,
            $"only {tokens.Count} LineHeight tokens declared — the ladder files have moved and this lint " +
            "is reading the wrong thing.");

        var setters = AxamlFiles().Sum(f => Regex.Matches(UiSourceTree.ReadAllText(f),
            @"<Setter\s+Property=""LineHeight""\s+Value=""\{(?:Dynamic|Static)Resource").Count);
        Assert.True(setters >= 32,
            $"only {setters} tokenised LineHeight setters — there were 33 on 2026-09-12 and ONE was removed " +
            "deliberately since (the settings rail's description, 2026-09-21).");
    }

    /// <summary>Every <c>LineHeight*</c> / <c>FireLineHeight*</c> key the two
    /// ladders declare, as a real number. Declared as anything but an
    /// <c>x:Double</c> the key would not match here at all, which is the other
    /// half of <see cref="ResourceTokenTypeTests" />' rule.</summary>
    internal static Dictionary<string, double> LineHeightTokens()
    {
        var tokens = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var relative in LadderFiles)
        {
            foreach (Match m in Regex.Matches(
                     UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", relative))),
                     @"<x:Double\s+x:Key=""(?<key>(?:Fire)?LineHeight[A-Za-z0-9_]*)"">(?<value>[0-9.]+)</x:Double>"))
            {
                tokens[m.Groups["key"].Value] =
                    double.Parse(m.Groups["value"].Value, CultureInfo.InvariantCulture);
            }
        }

        return tokens;
    }

    /// <summary>Replaces every XML comment's body with spaces, preserving both
    /// length and newlines so reported line numbers still point at the source.</summary>
    private static string MaskComments(string text) =>
        Regex.Replace(text, @"<!--.*?-->",
            m => new string([.. m.Value.Select(c => c is '\n' or '\r' ? c : ' ')]),
            RegexOptions.Singleline);

    private static string Describe(IReadOnlyDictionary<double, int> histogram) =>
        string.Join(", ", histogram.OrderBy(kv => kv.Key)
            .Select(kv => string.Create(CultureInfo.InvariantCulture, $"{kv.Key:0.##}x{kv.Value}")));

    private static IEnumerable<string> AxamlFiles()
        => UiSourceTree.EnumerateFiles(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI"), "*.axaml",
            SearchOption.AllDirectories);
}
