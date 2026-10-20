using System.Reflection;

namespace DotCharter.Tests.UI;

/// <summary>
///     File ▸ Exit shipped as a DEAD ROW: the MenuItem carried a Header and an
///     "Alt+F4" gesture LABEL (an <c>InputGesture</c> is display text, not a
///     binding) but no <c>Command</c> and no <c>Click</c> — clicking it did
///     nothing, and the palette had no quit verb either. The fix routes
///     <c>ExitApplicationCommand</c> → <c>ExitRequested</c> →
///     <c>MainWindow.Close()</c>, so an exit from the menu or the palette runs
///     the SAME OnClosing unsaved-changes gate as the caption button. These
///     lints pin every hop; a full shell cannot be constructed in the default
///     gate, so the chain is asserted as source.
/// </summary>
public sealed class ExitCommandWiringTests
{
    [Fact]
    public void TheExitMenuRow_BindsTheExitCommand()
    {
        var menu = MainMenuSource.Document();
        var ns = menu.Root!.Name.Namespace;

        var exit = menu.Descendants(ns + "MenuItem")
            .Single(e => (string?)e.Attribute("Header") == "{localization:Loc Menu_Exit}");

        Assert.Equal("{Binding ExitApplicationCommand}", (string?)exit.Attribute("Command"));
    }

    [Fact]
    public void TheCommand_RaisesExitRequested_AndTheWindowAnswersWithClose()
    {
        var shell = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.UI", "ViewModels", "Shell", "MainWindowViewModel.cs")));
        Assert.Contains("ExitRequested?.Invoke()", shell);

        var window = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.UI", "Views", "MainWindow.axaml.cs")));
        // Subscribe AND detach — DataContextChanged registers, DetachViewModelHandlers unhooks.
        Assert.Contains("ExitRequested += _exitRequestedHandler", window);
        Assert.Contains("ExitRequested -= _exitRequestedHandler", window);
        // The handler must be Close (the OnClosing gate), never TerminateNow —
        // terminating directly skips the unsaved-changes prompt.
        Assert.Contains("_exitRequestedHandler = Close;", window);
    }

    /// <summary>
    ///     …and it is the ONLY one. <c>MainWindowViewModel.RequestApplicationExit</c>
    ///     was a second quit seam that read as a live feature and was not: the
    ///     window assigned it <c>Close</c>, its own doc said <c>HandleEscapeKey</c>
    ///     invoked it, and nothing on earth did — it outlived the Esc-quits
    ///     behaviour (and the launch footer's "ESC QUIT" hint) that were removed
    ///     on purpose. A dead delegate beside a live one is how the next reader
    ///     comes to wire the wrong half.
    /// </summary>
    [Fact]
    public void TheShellHasExactlyOneApplicationExitSeam()
    {
        var vmType = typeof(DotCharter.UI.ViewModels.Shell.MainWindowViewModel);

        // Reflection, not a grep for a name: any Action-shaped member the window
        // could assign a quit handler to would do the same damage.
        var delegates = vmType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => typeof(Delegate).IsAssignableFrom(p.PropertyType) && p.CanWrite)
            .Select(p => p.Name)
            .Where(n => n.Contains("Exit", StringComparison.OrdinalIgnoreCase) ||
                        n.Contains("Quit", StringComparison.OrdinalIgnoreCase) ||
                        n.Contains("Close", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(delegates.Count == 0,
            "the shell view model exposes a settable exit/quit delegate again: " +
            string.Join(", ", delegates) +
            " — route new quit entry points through ExitApplicationCommand instead.");

        // The event is the seam that IS live; without this the lint above would
        // still pass on a shell that could not quit at all.
        Assert.NotNull(vmType.GetEvent("ExitRequested"));

        // And the window assigns nothing else. Recursive because the shell's
        // partials/collaborators live under Views/Shell/ since 2026-09-04.
        var windowSources = UiSourceTree
            .GetFiles(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views")), "MainWindow*.cs",
                SearchOption.AllDirectories);
        Assert.True(windowSources.Length >= 4,
            "MainWindow*.cs matched only " + windowSources.Length +
            " files — the glob went stale and this lint is vacuous.");

        foreach (var file in windowSources)
            Assert.DoesNotContain("RequestApplicationExit", UiSourceTree.ReadAllText(file), StringComparison.Ordinal);
    }

    [Fact]
    public void ThePalette_CarriesTheExitVerb()
    {
        var catalog = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.UI", "ViewModels", "CommandPalette", "CommandCatalog.cs")));
        Assert.Contains("\"file.exit\"", catalog);
    }
}
