using System.Xml.Linq;
using DotCharter.Services.Gameplay.Profiles;
using DotCharter.UI.ViewModels.Application;
using DotCharter.UI.ViewModels.PlayMode;
using DotCharter.UI.Views;

namespace DotCharter.Tests.UI;

/// <summary>
///     Default-gate guards for the LAUNCH screen — the app's very first surface
///     (<c>StartScreenRoot</c> in <c>PlayModeView.axaml</c>), rebuilt on the Fire
///     layer from <c>Launch Screen.dc.html</c>.
///
///     <para>
///         Everything here is either PURE (the facts snapshot's tri-states, the
///         chord gate, the profile detail line) or a SOURCE-STRUCTURE assertion
///         over the real XAML parsed as XML — the same split
///         <c>FrontDoorAndLibraryTests</c> uses, and for the same reason: booting
///         the app session for a visual-tree walk is the
///         <c>HeadlessSessionFixture</c> teardown trap the default gate excludes.
///         The runtime halves (construction, attach, measured bounds in BOTH
///         panel states) live in <c>Perf/PlayModeScreensUiPerfSmokeTests</c>.
///     </para>
/// </summary>
public sealed class LaunchScreenTests
{
    // ══ The rule this screen exists to enforce: no fabricated values ══════════

    /// <summary>
    ///     A chip that cannot say "I do not know" will say something false on a
    ///     fresh install, which is exactly what happened to the mockup's
    ///     "2,079 songs · LIBRARY READY" on a machine that has never scanned.
    ///     Every state transition of the library chip is pinned here, including
    ///     the one that matters most: a RUNNING scan is never READY, whatever
    ///     count happens to be in memory.
    /// </summary>
    [Fact]
    public void TheLibraryChip_CanNeverClaimReadyWhileItIsStillScanning()
    {
        // Nothing has looked yet — no count, and it says so.
        Assert.Equal(LaunchLibraryState.NotScanned, LaunchFacts.StateFor(0, false, false));
        // A scan is running: SCANNING wins even over a populated list, because
        // the list is being replaced.
        Assert.Equal(LaunchLibraryState.Scanning, LaunchFacts.StateFor(0, true, false));
        Assert.Equal(LaunchLibraryState.Scanning, LaunchFacts.StateFor(2079, true, true));
        // A finished scan that found nothing is its own state, not a silent zero.
        Assert.Equal(LaunchLibraryState.Empty, LaunchFacts.StateFor(0, false, true));
        // …and only a finished scan with songs in it is READY.
        Assert.Equal(LaunchLibraryState.Ready, LaunchFacts.StateFor(2079, false, true));
    }

    /// <summary>
    ///     "Guitar connected" is not a concept the engine has: the nearest real
    ///     signal is an OPEN input capture stream. Three states, and "no engine"
    ///     is not the same claim as "engine up, nothing capturing".
    /// </summary>
    [Fact]
    public void TheInputChip_DistinguishesNoDeviceFromNoInput()
    {
        Assert.Equal(LaunchInputState.NoDevice, LaunchFacts.InputStateFor(hasEngine: false, 0));
        Assert.Equal(LaunchInputState.NoDevice, LaunchFacts.InputStateFor(hasEngine: false, 48_000));
        Assert.Equal(LaunchInputState.NoInput, LaunchFacts.InputStateFor(hasEngine: true, 0));
        Assert.Equal(LaunchInputState.Connected, LaunchFacts.InputStateFor(hasEngine: true, 48_000));
    }

    /// <summary>
    ///     The all-unknown snapshot a freshly-constructed view model holds — and
    ///     therefore what the app's FIRST FRAME renders. Every <c>Has*</c> is
    ///     false, so the screen draws its whole chrome with no figures rather
    ///     than with invented ones.
    /// </summary>
    [Fact]
    public void TheEmptySnapshot_ShowsNoFigureAtAll()
    {
        var empty = LaunchFacts.Empty;
        Assert.False(empty.HasLibraryCount);
        Assert.False(empty.IsLibraryReady);
        Assert.True(empty.IsLibraryPending);
        Assert.False(empty.IsInputConnected);
        Assert.False(empty.HasLatency);
        Assert.False(empty.HasAppVersion);
    }

    /// <summary>
    ///     Every figure on the screen must reach a <c>Launch.*</c> member (the
    ///     one snapshot) or a real command / view-model property — never a
    ///     literal. This walks the markup and refuses any bound TEXT under the
    ///     screen root whose path is not resolvable on a real type.
    ///     <para>
    ///         The minimum count is asserted first: a walk that finds nothing
    ///         passes vacuously, and this suite has shipped one of those.
    ///     </para>
    /// </summary>
    [Fact]
    public void EveryFigureOnTheScreen_BindsARealSource()
    {
        var bindings = ScreenRoot("StartScreenRoot").Descendants()
            .SelectMany(e => e.Attributes())
            .Select(a => a.Value)
            .Where(v => v.StartsWith("{Binding ", StringComparison.Ordinal))
            .ToList();

        Assert.True(bindings.Count >= 25,
            $"expected the launch screen to carry at least 25 bindings, found {bindings.Count} — " +
            "the walk is looking at the wrong subtree and every assertion below is vacuous");

        // Every `Launch.X` path must be a real member of ILaunchFacts.
        var members = typeof(ILaunchFacts).GetProperties().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        Assert.True(members.Count >= 10, "ILaunchFacts lost its members — the check below cannot fail.");

        var launchPaths = bindings
            .Select(ExtractPath)
            .Where(p => p.StartsWith("Launch.", StringComparison.Ordinal))
            .Select(p => p["Launch.".Length..])
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.True(launchPaths.Count >= 8,
            $"the screen should print at least 8 distinct snapshot values, found {launchPaths.Count}");
        foreach (var path in launchPaths)
            Assert.Contains(path, members);
    }

    /// <summary>
    ///     The mockup's <c>LEVEL 14</c> has no backing concept anywhere in this
    ///     app, so nothing may print one. A fake level is the single most
    ///     reviewable-looking piece of stub data on the whole mockup — it reads
    ///     like a feature.
    /// </summary>
    [Fact]
    public void NoLevelConceptWasInvented()
    {
        // PlayerProfile still carries no level/XP/rank field…
        var profileFields = typeof(PlayerProfile).GetProperties().Select(p => p.Name).ToList();
        Assert.NotEmpty(profileFields);
        Assert.DoesNotContain(profileFields,
            n => n.Contains("Level", StringComparison.OrdinalIgnoreCase) ||
                 n.Contains("Xp", StringComparison.OrdinalIgnoreCase) ||
                 n.Contains("Rank", StringComparison.OrdinalIgnoreCase));

        // …and the screen prints no such caption. Attribute VALUES only — the
        // markup's own comment explains why the level was dropped, and a naive
        // whole-subtree text scan would trip over that explanation.
        var values = ScreenRoot("StartScreenRoot").Descendants()
            .SelectMany(e => e.Attributes())
            .Select(a => a.Value)
            .ToList();
        Assert.NotEmpty(values);
        foreach (var value in values)
        {
            Assert.DoesNotContain("Level", value, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("_Xp", value, StringComparison.OrdinalIgnoreCase);
        }

        // The resx carries no level caption either.
        Assert.DoesNotContain("PlayMode_Launch_Level",
            UiSourceTree.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "DotCharter.Localization", "Resources.resx")),
            StringComparison.Ordinal);
    }

    /// <summary>
    ///     A profile row's detail line is built ONLY from fields a
    ///     <see cref="PlayerProfile" /> really stores. Pure, with an explicit
    ///     "now", so the bucketing is asserted without a clock.
    /// </summary>
    [Fact]
    public void AProfilesDetailLine_UsesOnlyStoredFacts()
    {
        var now = new DateTime(2026, 8, 2, 12, 0, 0, DateTimeKind.Utc);

        var used = StatusBarViewModel.DescribeProfile(
            new PlayerProfile { Name = "Test", LastUsedUtc = now.AddDays(-3) }, now, FakeLocalization.English());
        Assert.NotEqual("", used);

        var lefty = StatusBarViewModel.DescribeProfile(
            new PlayerProfile { Name = "Lefty", LastUsedUtc = now.AddDays(-3), LeftyFlip = true }, now, FakeLocalization.English());
        Assert.Contains("·", lefty, StringComparison.Ordinal);
        Assert.True(lefty.Length > used.Length, "the left-handed flag must add a real segment");

        // A registry entry with no last-used stamp reports nothing rather than
        // bucketing `default(DateTime)` into "55 years ago".
        Assert.Equal("", StatusBarViewModel.DescribeProfile(new PlayerProfile { Name = "Fresh" }, now, FakeLocalization.English()));
    }

    // ══ The printed key hints ════════════════════════════════════════════════

    /// <summary>
    ///     Every hint the footer prints has a real handler. A printed shortcut
    ///     that does nothing is worse than no shortcut, and this screen prints
    ///     three of them.
    ///     <para>
    ///         Each pair is (the localized hint, the code that honours it). The
    ///         resx VALUE is read, so renaming the caption without moving the
    ///         handler fails here.
    ///     </para>
    /// </summary>
    [Fact]
    public void EveryPrintedKeyHint_HasARealHandler()
    {
        var hints = ScreenRoot("StartScreenRoot").Descendants()
            .SelectMany(e => e.Attributes())
            .Select(a => a.Value)
            .Where(v => v.Contains("PlayMode_Launch_Hint_", StringComparison.Ordinal))
            .Select(v => v[(v.IndexOf("PlayMode_Launch_Hint_", StringComparison.Ordinal))..].TrimEnd('}', ' '))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // TWO now: "ESC QUIT" went with the behaviour it described — Esc no
        // longer closes the application, and a printed shortcut that does
        // nothing is worse than no shortcut.
        Assert.Equal(2, hints.Count);

        var window = MainWindowCodeBehind();
        var shell = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "ViewModels", "Shell",
            "MainWindowViewModel.cs")));

        foreach (var hint in hints)
        {
            var caption = LocValue(hint);
            switch (hint)
            {
                case "PlayMode_Launch_Hint_Start":
                    Assert.StartsWith("Enter", caption, StringComparison.Ordinal);
                    // Key.Enter is claimed at the window tunnel and runs START (on to Home).
                    Assert.Contains("Key.Enter", window, StringComparison.Ordinal);
                    Assert.Contains("GlobalChordGate.AllowsLaunchStart", window, StringComparison.Ordinal);
                    Assert.Contains("vm.PlayMode.EnterHomeCommand.Execute(null)", window, StringComparison.Ordinal);
                    break;
                case "PlayMode_Launch_Hint_Settings":
                    Assert.StartsWith("S ", caption, StringComparison.Ordinal);
                    Assert.Contains("e.Key == Key.S", window, StringComparison.Ordinal);
                    Assert.Contains("GlobalChordGate.AllowsLaunchShortcut", window, StringComparison.Ordinal);
                    Assert.Contains("vm.PlayMode.Settings.OpenGameplaySettingsCommand.Execute(null)", window,
                        StringComparison.Ordinal);
                    break;
                case "PlayMode_Launch_Hint_Quit":
                    // Esc no longer closes the application, so this hint must
                    // not be printed. Kept as a case so re-adding the caption
                    // fails loudly instead of promising a dead shortcut.
                    Assert.Fail("the launch screen must not print an ESC QUIT hint — " +
                                "Esc no longer closes the application");
                    break;
                default:
                    Assert.Fail($"unhandled key hint '{hint}' — add its handler assertion or drop the hint");
                    break;
            }
        }
    }

    /// <summary>
    ///     The two bare-letter shortcuts stand down where they must. Both are
    ///     single characters, so a focused text input keeps them — the screen's
    ///     own new-profile and guest-name fields would otherwise open Settings on
    ///     every "s" typed. Enter additionally stands down for a focused Button,
    ///     which is what keeps the screen arrow-key navigable.
    /// </summary>
    [Fact]
    public void TheLaunchShortcuts_StandDownWhereTheyMust()
    {
        var onScreen = new GlobalChordGate.Context(
            TextInputFocused: false, KeybindCaptureFocused: false, PaletteOpen: false,
            HasOpenDialog: false, ChartEditorVisible: false, LaunchScreenVisible: true);

        Assert.True(GlobalChordGate.AllowsLaunchShortcut(onScreen));
        Assert.True(GlobalChordGate.AllowsLaunchStart(onScreen));

        // Not on the launch screen at all.
        Assert.False(GlobalChordGate.AllowsLaunchShortcut(onScreen with { LaunchScreenVisible = false }));
        Assert.False(GlobalChordGate.AllowsLaunchStart(onScreen with { LaunchScreenVisible = false }));

        // Typing a name.
        Assert.False(GlobalChordGate.AllowsLaunchShortcut(onScreen with { TextInputFocused = true }));
        Assert.False(GlobalChordGate.AllowsLaunchStart(onScreen with { TextInputFocused = true }));

        // Rebinding, a modal dialog, the palette.
        Assert.False(GlobalChordGate.AllowsLaunchShortcut(onScreen with { KeybindCaptureFocused = true }));
        Assert.False(GlobalChordGate.AllowsLaunchShortcut(onScreen with { HasOpenDialog = true }));
        Assert.False(GlobalChordGate.AllowsLaunchShortcut(onScreen with { PaletteOpen = true }));

        // A focused Button owns Enter — but NOT S, which is not an activation key.
        Assert.False(GlobalChordGate.AllowsLaunchStart(onScreen with { ActivatableFocused = true }));
        Assert.True(GlobalChordGate.AllowsLaunchShortcut(onScreen with { ActivatableFocused = true }));
    }

    // ══ Structure ════════════════════════════════════════════════════════════

    /// <summary>
    ///     A FULL-HEIGHT frame, not a centred island: a body row that takes the
    ///     surplus over a pinned footer row. The surface this replaced was a
    ///     420 px <c>VerticalAlignment=Center</c> StackPanel with no floor at
    ///     all, and that is the defect the mockup fixes.
    /// </summary>
    [Fact]
    public void TheScreenIsAFullHeightFrame_WithAPinnedFooter()
    {
        var root = ScreenRoot("StartScreenRoot");
        Assert.Equal("Grid", root.Name.LocalName);
        Assert.Equal("*,Auto", Attr(root, "RowDefinitions"));
        Assert.Null(Attr(root, "Width"));

        var footer = Named(root, "LaunchFooter");
        Assert.Equal("1", Attr(footer, "Grid.Row"));
        Assert.Contains("FireFooter", Attr(footer, "Classes") ?? "", StringComparison.Ordinal);

        // The body is the row that stretches, and it scrolls rather than
        // overflowing — opening at its TOP, like every other Fire scroll region.
        var scroll = Named(root, "LaunchBodyScroll");
        Assert.Equal("ScrollViewer", scroll.Name.LocalName);
        Assert.Equal("0", Attr(scroll, "Grid.Row"));
        Assert.Equal("False", Attr(scroll, "BringIntoViewOnFocusChange"));
    }

    /// <summary>
    ///     The SHIPPED logo, never the mockup's 2×2 dot placeholder — that
    ///     placeholder shipped once and the owner rejected it on sight.
    /// </summary>
    [Fact]
    public void TheBrandMarkIsTheShippedAsset()
    {
        var root = ScreenRoot("StartScreenRoot");
        var images = root.Descendants().Where(e => e.Name.LocalName == "Image").ToList();
        Assert.Single(images);
        Assert.Equal("avares://DotCharter/Assets/app-icon.png", Attr(images[0], "Source"));
    }

    /// <summary>
    ///     The screen paints NO ground. <c>MenuBackdropControl</c> renders behind
    ///     every Play Mode screen; the mockup's flat <c>#07060c</c> exists only
    ///     because a web page has nothing behind it, and a screen that paints it
    ///     hides the app's own animated front door.
    /// </summary>
    [Fact]
    public void TheScreenPaintsNoGroundOverTheBackdrop()
    {
        foreach (var element in ScreenRoot("StartScreenRoot").Descendants())
        {
            var classes = Attr(element, "Classes") ?? "";
            Assert.DoesNotContain("FireGround", classes, StringComparison.Ordinal);
        }
    }

    /// <summary>
    ///     Every profile action on the panel is a real command on the real
    ///     profile service's view model. The panel's whole point is that it is
    ///     the SAME profile model the shared switcher flyout drives — a second
    ///     one would drift the moment either side changed.
    /// </summary>
    [Fact]
    public void EveryProfileActionIsARealCommand()
    {
        var panel = Named(ScreenRoot("StartScreenRoot"), "LaunchProfilesPanel");

        var commands = panel.Descendants()
            .Select(e => Attr(e, "Command"))
            .Where(v => v != null)
            // A row's command is reached through `$parent[…].((T)DataContext).X`,
            // so the member is the LAST segment of the path.
            .Select(v => ExtractPath(v!).Split('.')[^1])
            .Where(p => p.EndsWith("Command", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.True(commands.Count >= 6,
            $"the profiles panel must carry switch + rename + picture + export + import + add, found " +
            $"{commands.Count}: {string.Join(", ", commands)}");

        var statusBar = typeof(StatusBarViewModel).GetProperties()
            .Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var command in commands)
            Assert.Contains(command, statusBar);

        // …and the four the mockup names by hand are all there.
        foreach (var required in new[]
                 {
                     "SwitchProfileCommand", "BeginRenameProfileCommand", "ChooseProfilePictureCommand",
                     "ExportProfileCommand", "ImportProfileCommand", "CreateProfileCommand"
                 })
        {
            Assert.Contains(required, commands);
        }

        // The left-handed checkbox writes the REAL per-profile flag, two-way.
        var lefty = Named(panel, "LaunchLeftyCheck");
        Assert.Equal("{Binding IsActiveLefty, Mode=TwoWay}", Attr(lefty, "IsChecked"));
    }

    /// <summary>
    ///     The two inline panels are mutually exclusive and Esc closes the open
    ///     one BEFORE the shell's quit branch — which is what makes "Esc quits"
    ///     safe while the player is typing a profile name, since that field only
    ///     exists inside the open panel.
    /// </summary>
    [Fact]
    public void EscClosesAnOpenLaunchPanelBeforeItQuits()
    {
        // The WHOLE view model: it is one class over many PlayModeViewModel*.cs
        // partials, so reading only the root goes blind when TryCloseOverlay
        // moves to a sibling.
        var vmSource = string.Join("\n", UiSourceTree
            .GetFiles(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "ViewModels", "PlayMode")),
                "PlayModeViewModel*.cs")
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(UiSourceTree.ReadAllText));

        var overlayStart = vmSource.IndexOf("public bool TryCloseOverlay()", StringComparison.Ordinal);
        Assert.True(overlayStart >= 0, "TryCloseOverlay is the Esc dismiss chain; it must still exist.");

        var body = vmSource[overlayStart..];
        var launchIndex = body.IndexOf("TryCloseLaunchPanel()", StringComparison.Ordinal);
        var nextIndex = body.IndexOf("IsGameplaySettingsOpen", StringComparison.Ordinal);
        Assert.True(launchIndex >= 0, "the launch panels must be dismissible by Esc");
        Assert.True(launchIndex < nextIndex,
            "the launch panel check must come FIRST in the dismiss chain");

        // TryCloseOverlay runs ahead of the shell's Play Mode branch, so an open
        // panel is always dismissed before Esc means anything else.
        var shell = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "ViewModels", "Shell",
            "MainWindowViewModel.cs")));
        var escStart = shell.IndexOf("public void HandleEscapeKey()", StringComparison.Ordinal);
        Assert.True(escStart >= 0);
        var chain = shell[escStart..];
        Assert.Contains("PlayMode.TryCloseOverlay()", chain, StringComparison.Ordinal);

        // …and Esc does NOT quit the application from anywhere in that chain.
        // It is the key you press to back out of things; answering it by closing
        // the program turns a reflex into a way to lose your place. Quitting is
        // the window's close button and File → Exit, both of which still run the
        // unsaved-changes prompt.
        Assert.DoesNotContain("RequestApplicationExit.Invoke()", chain, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The launch screen is the BOOT surface, so its library scan must start
    ///     after the first frame — the same rule (and the same failure mode: a
    ///     seconds-long scan marshalling a progress callback per batch onto the
    ///     UI thread) that <c>StartupWorkDeferralTests</c> pins for the Menu's
    ///     plan card.
    /// </summary>
    [Fact]
    public void TheLaunchScreensLibraryScanStartsAfterTheFirstFrame()
    {
        var source = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "ViewModels", "PlayMode",
            "PlayModeViewModel.Launch.cs")));

        var methodStart = source.IndexOf("internal void EnterLaunchScreen()", StringComparison.Ordinal);
        Assert.True(methodStart >= 0, "EnterLaunchScreen is the launch screen's entry hook.");
        var methodEnd = source.IndexOf("private async Task WarmLaunchScreenAsync()", methodStart,
            StringComparison.Ordinal);
        Assert.True(methodEnd > methodStart,
            "the deferred warm-up must still follow EnterLaunchScreen in the same file");

        var method = source[methodStart..methodEnd];
        Assert.Contains("DispatcherPriority.Background", method, StringComparison.Ordinal);
        Assert.Contains("WarmLaunchScreenAsync()", method, StringComparison.Ordinal);

        // The scan itself must NOT be reachable without going through that post.
        Assert.DoesNotContain("EnsureLibrary", method, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Both inline panels are exposed for prewarm. They are the case the
    ///     sibling sweep cannot reach for the OPPOSITE reason to the Progress
    ///     hub's four views: <c>StartScreenRoot</c> is the boot screen, so it is
    ///     VISIBLE and never appears in <c>HiddenChildrenOf</c> — while these two
    ///     gated children inside it have still never been measured. The lookup is
    ///     by <c>x:Name</c>, so a rename in the markup is a SILENT no-op; that is
    ///     what this cross-check exists for.
    /// </summary>
    [Fact]
    public void BothLaunchPanels_AreExposedForPrewarm()
    {
        var declared = DotCharter.UI.Views.PlayMode.PlayModeView.LaunchPanelNames;
        Assert.Equal(2, declared.Length);

        var root = ScreenRoot("StartScreenRoot");
        foreach (var name in declared)
        {
            var panel = Named(root, name);
            // …and each really is an IsVisible-gated surface, which is the only
            // kind that needs prewarming at all.
            Assert.False(string.IsNullOrEmpty(Attr(panel, "IsVisible")),
                $"'{name}' is in the prewarm list but is not IsVisible-gated");
        }

        // Every gated PANEL under the screen root is declared — a new one that
        // forgets this reintroduces the first-open lag for itself. Scoped to
        // `*Panel` on purpose: the screen also gates small leaves (the version
        // text, the inline rename row) whose realization costs nothing, and a
        // rule that swept those in would just be noise nobody obeys.
        var gated = root.Descendants()
            .Where(e => Attr(e, "IsVisible") != null &&
                        Attr(e, "Name") is { } n &&
                        n.StartsWith("Launch", StringComparison.Ordinal) &&
                        n.EndsWith("Panel", StringComparison.Ordinal))
            .Select(e => Attr(e, "Name")!)
            .ToList();
        Assert.NotEmpty(gated);
        Assert.All(gated, n => Assert.Contains(n, declared));
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string ExtractPath(string binding)
    {
        var inner = binding.TrimStart('{').Trim();
        if (inner.StartsWith("Binding ", StringComparison.Ordinal)) inner = inner["Binding ".Length..];
        inner = inner.TrimEnd('}').Trim();
        var comma = inner.IndexOf(',');
        if (comma >= 0) inner = inner[..comma];
        return inner.Trim();
    }

    private static string LocValue(string key)
    {
        var doc = XDocument.Load(Path.Combine(TestPaths.RepoRoot(), "DotCharter.Localization", "Resources.resx"));
        var entry = doc.Root!.Elements("data")
            .FirstOrDefault(e => (string?)e.Attribute("name") == key);
        Assert.True(entry != null, $"'{key}' is missing from Resources.resx");
        return entry!.Element("value")!.Value;
    }

    private static string? Attr(XElement element, string name) =>
        element.Attributes().FirstOrDefault(a => a.Name.LocalName == name)?.Value;

    private static XElement Named(XElement scope, string name)
    {
        var match = scope.DescendantsAndSelf().SingleOrDefault(e =>
            e.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == name));
        Assert.True(match != null, $"'{name}' is not in the Play Mode markup — a rename skips every guard here");
        return match!;
    }

    private static XElement ScreenRoot(string name)
    {
        var root = PlayModeMarkup.Document().Descendants().SingleOrDefault(e =>
            e.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == name));
        Assert.True(root != null, $"'{name}' is not in the Play Mode markup");
        return root!;
    }

    /// <summary>The shell code-behind is SPLIT across <c>MainWindow.axaml.cs</c>,
    /// its <c>MainWindow.*.cs</c> partials AND the <c>MainWindow*</c> collaborators
    /// extracted out of them (<c>Views/Shell/MainWindowPrewarm.cs</c> and friends) — read them all, or a lint
    /// silently stops looking at the half it cares about.</summary>
    private static string MainWindowCodeBehind() =>
        string.Join('\n', UiSourceTree
            .GetFiles(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views")), "MainWindow*.cs",
                SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(UiSourceTree.ReadAllText));
}
