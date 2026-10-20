using System.Xml.Linq;

namespace DotCharter.Tests.UI;

/// <summary>
///     Enter commits the dialog's PRIMARY, never a body button. Both Songsterr
///     dialogs shipped <c>IsDefault</c> on the body's Search button while the
///     footer's Import/Apply primary had none — so filling in the import
///     options and pressing Enter re-ran the SEARCH and threw the selection
///     away. The rule this encodes: <c>IsDefault="True"</c> may only sit on a
///     button that (a) lives in a footer action slot
///     (<c>StackPanel.DlgActions</c>) and (b) wears the <c>Primary</c> class.
///     A search box gets a KeyDown handler instead (the PhraseCatalogView
///     pattern — see the Songsterr code-behinds).
/// </summary>
public sealed class DialogDefaultActionTests
{
    [Fact]
    public void IsDefault_OnlyEverSitsOnAFooterPrimary()
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
                     .Where(e => e.Name.LocalName is "Button" or "ToggleButton"
                                 && string.Equals(e.Attribute("IsDefault")?.Value, "True",
                                     StringComparison.OrdinalIgnoreCase)))
            {
                found++;
                var classes = (button.Attribute("Classes")?.Value ?? string.Empty)
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var inActions = button.Ancestors().Any(a =>
                    a.Name.LocalName == "StackPanel"
                    && (a.Attribute("Classes")?.Value ?? string.Empty)
                        .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        .Contains("DlgActions", StringComparer.Ordinal));

                if (!classes.Contains("Primary", StringComparer.Ordinal))
                {
                    offenders.Add($"{Path.GetFileName(file)}: IsDefault on a non-Primary button " +
                              $"(Classes=\"{string.Join(' ', classes)}\") — Enter would commit the wrong action");
                }
                else if (!inActions)
                {
                    offenders.Add($"{Path.GetFileName(file)}: IsDefault on a Primary outside the footer's " +
                              "StackPanel.DlgActions — the default action belongs in the footer");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "IsDefault must mark the footer primary and nothing else:\n  " + string.Join("\n  ", offenders));

        // The scan saw the catalogue (30+ dialogs carry a default action) — it
        // cannot pass by parsing nothing.
        Assert.True(found >= 25, $"only {found} IsDefault buttons were found — the scan is broken");
    }

    /// <summary>
    ///     The CLOSE TIER (2026-08-11). Two honest shapes for a footer Close:
    ///     a read-only dialog whose ONLY footer-right action is dismissal gets
    ///     it as the Primary (with IsCancel + IsDefault — Enter and Esc both
    ///     leave); a Close standing beside a committing Primary is Subtle, so
    ///     the lit chamfer stays on the action that changes something. Batch
    ///     Lint's Subtle Close beside Run and History's Primary Close are BOTH
    ///     correct — what this pins is that the shape follows the footer's
    ///     contents, not the author's taste.
    /// </summary>
    [Fact]
    public void AFooterClose_SitsOnTheTierItsFooterDictates()
    {
        var dialogDirs = new[]
        {
            UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Dialogs")),
            UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Diagnostics"))
        };

        var offenders = new List<string>();
        var closes = 0;

        foreach (var file in dialogDirs.Where(Directory.Exists)
                     .SelectMany(d => UiSourceTree.EnumerateFiles(d, "*.axaml", SearchOption.AllDirectories)))
        {
            foreach (var actions in XDocument.Load(file).Descendants()
                     .Where(e => e.Name.LocalName == "StackPanel"
                                 && (e.Attribute("Classes")?.Value ?? string.Empty)
                                     .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                                     .Contains("DlgActions", StringComparer.Ordinal)))
            {
                var buttons = actions.Elements().Where(e => e.Name.LocalName == "Button").ToList();
                var close = buttons.FirstOrDefault(b =>
                    (b.Attribute("Content")?.Value ?? string.Empty)
                    .Contains("Loc Action_Close", StringComparison.Ordinal));
                if (close is null) continue;

                closes++;
                var closeClasses = (close.Attribute("Classes")?.Value ?? string.Empty)
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries);

                if (buttons.Count == 1)
                {
                    if (!closeClasses.Contains("Primary", StringComparer.Ordinal))
                    {
                        offenders.Add($"{Path.GetFileName(file)}: a sole-dismissal Close is " +
                                  $"\"{string.Join(' ', closeClasses)}\" — it is this dialog's one way out " +
                                  "and must be the Primary");
                    }
                    else if (!string.Equals(close.Attribute("IsDefault")?.Value, "True", StringComparison.OrdinalIgnoreCase))
                    {
                        offenders.Add($"{Path.GetFileName(file)}: a sole-dismissal Primary Close without " +
                                  "IsDefault — Enter should leave a read-only dialog");
                    }
                }
                else if (buttons.Any(b => !ReferenceEquals(b, close)
                                          && (b.Attribute("Classes")?.Value ?? string.Empty)
                                              .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                                              .Contains("Primary", StringComparer.Ordinal))
                         && closeClasses.Contains("Primary", StringComparer.Ordinal))
                {
                    offenders.Add($"{Path.GetFileName(file)}: a Close wearing Primary beside another " +
                                  "Primary — the committing action owns the chamfer, Close goes Subtle");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "Footer Close button(s) on the wrong tier (the close-tier rule, docs/agents/ui/dialogs.md):\n  " +
            string.Join("\n  ", offenders));
        Assert.True(closes >= 5, $"only {closes} footer Close buttons were found — the scan is broken");
    }
}
