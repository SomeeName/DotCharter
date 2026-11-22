using System.Text.RegularExpressions;

namespace DotCharter.Tests.UI;

/// <summary>
///     Keyboard entry (#367, #368). The runtime proofs live in
///     <c>Perf/DialogConstructionUiPerfSmokeTests.KeyboardInput</c>; these lints
///     cover what a runtime case cannot reach (an item-template box) and guard
///     every dialog at once.
/// </summary>
public sealed class KeyboardEntryLintTests
{
    /// <summary>Dialogs whose only inputs are the per-row boxes of an item template: there is no single first input to name.</summary>
    private static readonly HashSet<string> RowInputOnly = new(StringComparer.Ordinal) { "LinkGroupsDialogView.axaml" };

    private static readonly Regex Input = new(@"<(TextBox|NumericUpDown)\b[^>]*>", RegexOptions.Singleline);

    /// <summary>
    ///     A dialog whose body edits text or a number must name its first input
    ///     with <c>FocusOnOpen.IsTarget</c>; otherwise it opens with focus nowhere
    ///     and the first keystroke goes to the editor underneath. Read-only boxes
    ///     are exempt.
    /// </summary>
    [Fact]
    public void EveryDialogWithAnEditableInput_NamesAFocusTarget()
    {
        var dir = UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Dialogs"));
        var offenders = new List<string>();
        var checkedDialogs = 0;

        foreach (var file in UiSourceTree.EnumerateFiles(dir, "*DialogView.axaml", SearchOption.AllDirectories))
        {
            var markup = UiSourceTree.ReadAllText(file);
            var editable = Input.Matches(markup).Any(m => !m.Value.Contains("IsReadOnly=\"True\"", StringComparison.Ordinal));
            if (!editable || RowInputOnly.Contains(Path.GetFileName(file))) continue;

            checkedDialogs++;
            if (!markup.Contains("FocusOnOpen.IsTarget=\"True\"", StringComparison.Ordinal))
                offenders.Add(Path.GetFileName(file));
        }

        Assert.True(checkedDialogs >= 15, $"only {checkedDialogs} dialogs with inputs were seen — the lint is vacuous");
        Assert.True(offenders.Count == 0,
            "Dialogs with an editable input and no FocusOnOpen.IsTarget:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>The chat-title rename box commits on Enter and cancels on Escape; it sits in an item template, so it is checked in the markup.</summary>
    [Fact]
    public void AiChatTitleBox_CommitsOnEnterAndCancelsOnEscape()
    {
        var path = UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Editor", "AiChatPaneView.axaml"));
        var markup = UiSourceTree.ReadAllText(path);
        var box = Regex.Match(markup, @"<TextBox\b[^>]*Text=""\{Binding TitleDraft\}""[^>]*>.*?</TextBox>", RegexOptions.Singleline);

        Assert.True(box.Success, "the TitleDraft box (with KeyBindings as a child) is gone");
        Assert.Matches(@"Gesture=""Enter""\s+Command=""\{Binding CommitRenameCommand\}""", box.Value);
        Assert.Matches(@"Gesture=""Escape""\s+Command=""\{Binding CancelRenameCommand\}""", box.Value);
    }

    /// <summary>The setlist name box (LostFocus-bound) is marked to commit on Enter.</summary>
    [Fact]
    public void SetlistNameBox_CommitsOnEnter()
    {
        var path = UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views", "PlayMode", "Screens", "SetlistPanelView.axaml"));
        var markup = UiSourceTree.ReadAllText(path);
        var box = Regex.Match(markup, @"<TextBox\b[^>]*x:Name=""SetlistNameBox""[^>]*>", RegexOptions.Singleline);

        Assert.True(box.Success);
        Assert.Contains("CommitOnEnter.IsEnabled=\"True\"", box.Value);
    }
}
