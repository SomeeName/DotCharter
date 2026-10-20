using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DotCharter.Tests.UI;

/// <summary>
///     The dialog grammar's DANGER tier: a destructive action (Delete, Discard,
///     Don't Save) must be visually distinct from the non-destructive buttons
///     beside it. Before the tier existed, "Don't Save" and "Cancel" rendered
///     pixel-identical in the close gate — the only thing separating "lose my
///     work" from "keep my work" was reading order.
///     <para>
///         Source lints, because the failure has no runtime signal: a button
///         missing the <c>Danger</c> class simply renders in the ghost tier's
///         look, which is a perfectly valid look for every OTHER button.
///     </para>
/// </summary>
public sealed class DialogDangerTierTests
{
    /// <summary>Loc keys that NAME a destructive verb without being one —
    /// deliberately empty. An entry here is a hole in the lint; add one only
    /// with the reason ("…_DeleteHint is a description row, not a button").</summary>
    private static readonly HashSet<string> ExemptKeys = new(StringComparer.Ordinal);

    private static readonly Regex DestructiveKey = new(
        "Delete|Discard|DontSave|RemoveAll", RegexOptions.Compiled);

    /// <summary>
    ///     The grammar declares the tier, in the Fire danger palette, and
    ///     declares it LAST — the tier is colour-only and composes over the
    ///     chip/link silhouettes, which only works because a later style wins.
    ///     Reads through <see cref="StyleFileText.ReadWithIncludes" />: the
    ///     grammar file is a container of includes and reading it alone would
    ///     pass vacuously.
    /// </summary>
    [Fact]
    public void TheGrammar_DeclaresTheDangerTier_AfterEveryOtherTier()
    {
        var grammar = StyleFileText.ReadWithIncludes(UiSourceTree.Resolve(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Dialogs", "DialogStyles.axaml")));

        var danger = grammar.IndexOf("Border.DialogCard Button.Danger", StringComparison.Ordinal);
        Assert.True(danger >= 0,
            "The dialog grammar no longer declares the danger tier " +
            "(`Border.DialogCard Button.Danger`) — every destructive action just " +
            "renders in the ghost tier and reads as safe.");

        var dangerBlock = grammar[danger..];
        Assert.Contains("FireDanger", dangerBlock, StringComparison.Ordinal);
        Assert.Contains("FireDangerTint", dangerBlock, StringComparison.Ordinal);
        Assert.Contains("FireDangerBorder", dangerBlock, StringComparison.Ordinal);

        // Colour-only + declared last is the tier's whole mechanism: it must
        // out-declare the chip and link tiers or `Classes="DlgChip Danger"`
        // keeps the chip's muted caption.
        foreach (var tier in new[] { "Button.DlgChip /template/", "Button.DlgLink /template/" })
        {
            Assert.True(grammar.IndexOf(tier, StringComparison.Ordinal) < danger,
                $"The danger tier is declared before `{tier}` — the {tier} colours would " +
                "win over it and a destructive chip/link would not read as destructive.");
        }
    }

    /// <summary>
    ///     Every dialog button whose caption is a destructive verb carries the
    ///     <c>Danger</c> class. This is what stops a NEW dialog shipping a
    ///     Delete that renders identical to its Cancel.
    /// </summary>
    [Fact]
    public void EveryDestructiveDialogAction_WearsTheDangerTier()
    {
        var dialogDirs = new[]
        {
            UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Dialogs")),
            UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Diagnostics"))
        };

        var offenders = new List<string>();
        var found = 0;

        foreach (var file in dialogDirs.Where(Directory.Exists)
                     .SelectMany(d => UiSourceTree.EnumerateFiles(d, "*.axaml", SearchOption.AllDirectories)))
        {
            foreach (var button in XDocument.Load(file).Descendants()
                     .Where(e => e.Name.LocalName is "Button" or "ToggleButton"))
            {
                // The caption is usually the Content attribute, but a button may
                // carry its text as a child TextBlock (Showlights does) — collect
                // Loc keys from the element AND its subtree.
                var keys = button.DescendantsAndSelf()
                    .SelectMany(e => e.Attributes())
                    .Select(a => Regex.Match(a.Value, @"localization:Loc\s+([A-Za-z0-9_]+)"))
                    .Where(m => m.Success)
                    .Select(m => m.Groups[1].Value)
                    .Where(k => DestructiveKey.IsMatch(k) && !ExemptKeys.Contains(k))
                    .ToList();
                if (keys.Count == 0) continue;

                found++;
                var classes = (button.Attribute("Classes")?.Value ?? string.Empty)
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (!classes.Contains("Danger", StringComparer.Ordinal))
                {
                    offenders.Add($"{Path.GetFileName(file)}: a button captioned " +
                              $"'{keys[0]}' has Classes=\"{string.Join(' ', classes)}\" — no Danger");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "Destructive dialog action(s) render identical to the safe buttons beside them — " +
            "add `Danger` to their Classes:\n  " + string.Join("\n  ", offenders));

        // …and the scan itself saw the known destructive set, so it cannot pass
        // by matching nothing (seven exist since the export presets went).
        Assert.True(found >= 7, $"only {found} destructive dialog buttons were found — the caption scan is broken");
    }
}
