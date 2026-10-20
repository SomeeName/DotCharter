using System.Text.RegularExpressions;
using DotCharter.UI.ViewModels.Shell;

namespace DotCharter.Tests.UI.ViewModels.Shell;

/// <summary>
///     View ▸ Reset Layout (and the same verb in the command palette) throws away
///     the splitter positions, re-docks every floated pane, closes both pop-out
///     windows and clears the persisted <c>DockLayout</c> — a layout a user may
///     have arranged across several monitors, with no undo and nothing on disk to
///     reopen. It ran on ONE click. It is a MENU verb, so it takes the app-wide
///     modal prompt rather than the in-place two-click latch a row delete uses
///     (<c>docs/agents/ui/dialogs.md</c>).
///     <para>
///         The rule is a static over a delegate for the same reason
///         <c>BatchLintViewModel.ConfirmOverwriteAsync</c> is one:
///         <c>MainWindowViewModel</c> takes 26 required services, so nothing can
///         construct it in the default gate, and a confirm nobody can test is a
///         confirm nobody should trust.
///     </para>
/// </summary>
public sealed class LayoutResetConfirmTests
{
    [Fact]
    public async Task WithNoPromptWired_TheResetIsREFUSED()
    {
        var applied = 0;

        await MainWindowViewModel.ResetLayoutAsync(null, () => applied++);

        // "No way to ask" must mean "do not do it" for a destructive verb —
        // never "do it silently", which is what shipped.
        Assert.Equal(0, applied);
    }

    [Fact]
    public async Task WhenThePromptIsRefused_NothingIsReset()
    {
        var applied = 0;
        var asked = 0;

        await MainWindowViewModel.ResetLayoutAsync(
            () => { asked++; return Task.FromResult(false); },
            () => applied++);

        Assert.Equal(1, asked);
        Assert.Equal(0, applied);
    }

    [Fact]
    public async Task WhenThePromptIsAccepted_TheResetRunsExactlyOnce()
    {
        var applied = 0;

        await MainWindowViewModel.ResetLayoutAsync(() => Task.FromResult(true), () => applied++);

        Assert.Equal(1, applied);
    }

    /// <summary>
    ///     …and the command actually goes through the gate. Without this the two
    ///     tests above would pin a helper the shell had stopped calling — the
    ///     shape a file split makes silent in this repo.
    /// </summary>
    [Fact]
    public void TheResetLayoutCommand_GoesThroughTheGate_AndTheShellWiresTheModalPrompt()
    {
        var layout = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI",
            "ViewModels", "Shell", "MainWindowViewModel.Layout.cs")));
        var shell = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI",
            "ViewModels", "Shell", "MainWindowViewModel.cs")));

        Assert.Matches(new Regex(
                @"private\s+Task\s+ResetLayout\(\)\s*=>\s*ResetLayoutAsync\(\s*ConfirmLayoutResetAsync\s*,"),
            layout);

        // The shell hands the delegate the app-wide prompt, as a CONFIRMATION —
        // a plain message dialog completes true on dismiss, which would gate
        // nothing at all.
        var wiring = shell.IndexOf("ConfirmLayoutResetAsync =", StringComparison.Ordinal);
        Assert.True(wiring >= 0,
            "The shell no longer wires ConfirmLayoutResetAsync — Reset Layout would refuse silently.");
        var block = shell[wiring..Math.Min(shell.Length, wiring + 600)];
        Assert.Contains("Dialogs.Message.ShowAsync", block, StringComparison.Ordinal);
        Assert.Contains("confirmation: true", block, StringComparison.Ordinal);
        Assert.Contains("ResetLayout_ConfirmMessage", block, StringComparison.Ordinal);
    }
}
