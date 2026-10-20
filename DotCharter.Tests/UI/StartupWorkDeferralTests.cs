namespace DotCharter.Tests.UI;

/// <summary>
///     Pins the two pieces of work that must NOT happen inside the shell view
///     model's constructor, plus the resolution order that makes the startup
///     phase log tell the truth about where boot time went. All three were found
///     by profiling cold start and all three are invisible in normal use — they
///     only cost time (or hide it), so nothing else would fail if they came back.
///     <para>
///         Source lints, for the same reason as <c>PenEscapeReachabilityTests</c>:
///         constructing <c>MainWindowViewModel</c> needs the whole app booted, so
///         its behavioural tests are <c>*UiPerfSmokeTests</c> and sit outside the
///         default gate. Deliberately NOT wall-clock assertions — this repo has
///         been burned by those; these assert the STRUCTURE that makes the cost
///         impossible.
///     </para>
/// </summary>
public sealed class StartupWorkDeferralTests
{
    /// <summary>
    ///     <c>AppConfig.StartInPlayMode</c> makes the Play Mode tab the boot
    ///     surface, so <c>MainWindowViewModel</c>'s constructor sets
    ///     <c>SelectedMainTabIndex</c> itself and lands in
    ///     <c>OnSelectedMainTabIndexChanged</c>. Pausing there reaches the native
    ///     JUCE engine (stem transports, scheduled clicks, MIDI all-notes-off)
    ///     while <c>ProjectSessionService.Initialize</c> is still opening the audio
    ///     device on a worker thread — a UI-thread block before the window even
    ///     exists. Nothing can be playing at that point, so the guard is free.
    /// </summary>
    [Fact]
    public void EnteringPlayModeOnlyPausesATransportThatIsActuallyPlaying()
    {
        var source = ShellSource();

        var handlerStart = source.IndexOf(
            "partial void OnSelectedMainTabIndexChanged(int value)", StringComparison.Ordinal);
        Assert.True(handlerStart >= 0,
            "OnSelectedMainTabIndexChanged is the tab-switch hook; it must still exist.");

        var handler = source[handlerStart..];
        var pauseIndex = handler.IndexOf("Transport.PauseCommand.Execute(null)", StringComparison.Ordinal);
        Assert.True(pauseIndex >= 0,
            "Leaving the chart editor must still pause the editor's song (Play Mode, and #154: Library / Home).");

        // The guard has to sit on the SAME condition that reaches the pause, so
        // look at the statement that precedes it rather than anywhere in the file.
        // #154: EVERY tab but the chart editor pauses (the Library and Home used to
        // let the song, and the chart synth, play on), so the gate is "not the editor".
        // The `if` read is the one DIRECTLY guarding the pause: searching back for the
        // text alone matched an earlier, unrelated `if (value != ChartEditorTabIndex)`
        // and passed with the old Play-Mode-only condition back in place.
        var conditionStart = handler.LastIndexOf("if (", pauseIndex, StringComparison.Ordinal);
        Assert.True(conditionStart >= 0, "The pause must be guarded by a condition.");
        Assert.True(
            handler.AsSpan(conditionStart).StartsWith("if (value != ChartEditorTabIndex", StringComparison.Ordinal),
            "The pause must stay gated on leaving the chart editor tab (any other tab), not on one destination.");

        var condition = handler[conditionStart..pauseIndex];
        Assert.Contains("Transport.IsPlaying", condition, StringComparison.Ordinal);
        Assert.Contains("!LivePlay.IsActive", condition, StringComparison.Ordinal);
    }

    /// <summary>
    ///     <c>AppKeybindRegistrar</c> takes <c>MainWindowViewModel</c> as a
    ///     constructor argument, so resolving the registrar builds the entire
    ///     view-model graph. The phase log has to stamp the shell BEFORE the
    ///     registrar or <c>vmGraph</c> reads 0 ms and the shell's cost hides
    ///     inside <c>services</c> — which is exactly how it read while boot time
    ///     was being investigated.
    /// </summary>
    [Fact]
    public void TheStartupPhaseLogResolvesTheShellBeforeTheKeybindRegistrar()
    {
        var source = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "App.axaml.cs")));

        var shellIndex = source.IndexOf(
            "Services.GetRequiredService<MainWindowViewModel>()", StringComparison.Ordinal);
        var registrarIndex = source.IndexOf(
            "Services.GetRequiredService<AppKeybindRegistrar>()", StringComparison.Ordinal);

        Assert.True(shellIndex >= 0, "The composition root must still resolve the shell view model.");
        Assert.True(registrarIndex >= 0, "The composition root must still register the keybinds.");
        Assert.True(shellIndex < registrarIndex,
            "Resolve MainWindowViewModel before AppKeybindRegistrar — the registrar takes the shell as a " +
            "constructor argument, so resolving it first makes the vmGraph phase report 0 ms.");
    }

    private static string ShellSource() => UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(
        TestPaths.RepoRoot(), "DotCharter.UI", "ViewModels", "Shell", "MainWindowViewModel.cs")));
}
