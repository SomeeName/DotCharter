using System.Text.RegularExpressions;

namespace DotCharter.Tests.UI;

/// <summary>
///     The scrim rule (2026-08-11): clicking the scrim may only ever dismiss a
///     READ-ONLY dialog. An EDITING dialog ignores the scrim — six of them
///     (Event, Paste Special, Pattern Builder, Showlights and the since-deleted
///     Selection Filter / Selection Query) closed on scrim-click with no dirty
///     check, so a mis-click one pixel past the card threw away everything
///     typed into it.
///     Esc still cancels (every dialog carries an <c>IsCancel</c> button —
///     <c>UI/ModalEscapeHatchTests</c> pins that), which is fine: Esc is a
///     deliberate gesture, a click beside a 480 px card on a 2560 px screen is
///     not.
/// </summary>
public sealed class DialogScrimRuleTests
{
    /// <summary>
    ///     Dialogs allowed to dismiss on scrim-click. An entry must be a
    ///     READ-ONLY dialog: nothing in it edits state, so dismissal cannot
    ///     discard work. Deliberately EMPTY — none of the current 33 wires it,
    ///     and a new one is a claim to argue here, with the reason.
    /// </summary>
    private static readonly HashSet<string> ReadOnlyScrimDismiss = new(StringComparer.Ordinal);

    [Fact]
    public void NoEditingDialog_DismissesOnScrimClick()
    {
        var dialogDirs = new[]
        {
            UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Dialogs")),
            UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Diagnostics"))
        };

        var offenders = new List<string>();
        var scrims = 0;

        foreach (var file in dialogDirs.Where(Directory.Exists)
                     .SelectMany(d => UiSourceTree.EnumerateFiles(d, "*.axaml", SearchOption.AllDirectories)))
        {
            var markup = UiSourceTree.ReadAllText(file);
            // A hosted dialog's root is Grid.DialogFrame; its backdrop is
            // DialogHost's one shared scrim, checked below.
            scrims += Regex.Matches(markup, "Classes=\"Dialog(Scrim|Frame)\"").Count;
            if (ReadOnlyScrimDismiss.Contains(Path.GetFileName(file))) continue;

            // The wiring, not the word: a comment may legitimately explain the
            // rule by naming the handler.
            if (Regex.IsMatch(markup, "Classes=\"Dialog(Scrim|Frame)\"[^>]*PointerPressed=", RegexOptions.Singleline))
            {
                offenders.Add($"{Path.GetFileName(file)}: the scrim wires PointerPressed — " +
                              "a click one pixel past the card would discard the dialog's work");
            }

            var codeBehind = file + ".cs";
            if (File.Exists(codeBehind)
                && Regex.IsMatch(UiSourceTree.ReadAllText(codeBehind), @"\bOnScrimPressed\b"))
            {
                offenders.Add($"{Path.GetFileName(codeBehind)}: carries an OnScrimPressed handler");
            }
        }

        Assert.True(offenders.Count == 0,
            "Scrim-click dismissal on an editing dialog (the scrim rule, docs/agents/ui/dialogs.md):\n  " +
            string.Join("\n  ", offenders));

        // The dialog host's shared scrim is the backdrop of every hosted
        // dialog, so it answers to the same rule: no pointer handler at all.
        var host = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Shell", "DialogHost.cs")));
        Assert.False(Regex.IsMatch(host, @"\bPointer(Pressed|Released)\b"),
            "DialogHost wires a pointer handler — a click beside a hosted dialog's card must not dismiss it");

        // The scan really walked the catalogue — 30+ dialogs declare a scrim or a hosted frame.
        Assert.True(scrims >= 30, $"only {scrims} DialogScrim / DialogFrame declarations were found — the scan is broken");
    }
}
