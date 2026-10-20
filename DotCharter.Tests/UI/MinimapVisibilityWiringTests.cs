namespace DotCharter.Tests.UI;

/// <summary>
///     The minimap strip has TWO independent reasons to be hidden — the user
///     closed it, or the gameplay presentation has taken the window — and they
///     must not overwrite each other. Folding the second into the first would
///     make starting a run silently forget the preference, which is exactly the
///     bug <c>MainWindowViewModel</c>'s pre-game panel snapshot exists to avoid
///     for the docked panes.
///     <para>
///         Source/markup lints, because <c>MainWindowViewModel</c> needs the
///         whole booted shell to construct and nothing in the default gate can
///         build one. What is pinned is the wiring, which is where this class of
///         toggle actually breaks: a menu item that does nothing because the
///         derived property was never notified, or a preference that is not
///         written and so does not survive the launch that set it.
///     </para>
/// </summary>
public sealed class MinimapVisibilityWiringTests
{
    // The shell view model is partial; the panel-visibility state — the minimap
    // preference and the gameplay-presentation snapshot included — lives in
    // MainWindowViewModel.Layout.cs.
    private static string Vm() => UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(),
        "DotCharter.UI", "ViewModels", "Shell", "MainWindowViewModel.Layout.cs")));

    /// <summary>Default ON: a minimap nobody asked to hide must still be there.</summary>
    [Fact]
    public void TheMinimapIsShownByDefault() => Assert.True(new AppConfig().ShowMinimap);

    /// <summary>
    ///     The strip binds the DERIVED value. Binding `!IsGameplayPresentation`
    ///     (what it was) ignores the user entirely; binding `IsMinimapVisible`
    ///     alone would leave the strip on screen during gameplay.
    /// </summary>
    [Fact]
    public void TheStripBindsTheDerivedVisibility()
    {
        var markup = MainWindowSource.ReadAllText();

        // Scoped to the FRAME element — other surfaces bind
        // !IsGameplayPresentation quite correctly, they just have no user
        // toggle. The anchor used to be the seek handler with a fixed 600-char
        // look-behind; since 0.51 the strip also carries a SONG MAP caption
        // cell and the handler moved onto the map's own border, so "near the
        // handler" is no longer "on the strip".
        var frame = markup.IndexOf(@"Classes=""MinimapFrame""", StringComparison.Ordinal);
        Assert.True(frame > 0, "the minimap frame was not found");
        var element = markup[frame..markup.IndexOf('>', frame)];

        Assert.Contains(@"IsVisible=""{Binding IsMinimapShown}""", element);
        Assert.DoesNotContain(@"IsVisible=""{Binding !IsGameplayPresentation}""", element);
    }

    /// <summary>
    ///     Both inputs must raise the derived property. A missing
    ///     <c>NotifyPropertyChangedFor</c> is the silent version of this bug: the
    ///     menu item ticks, the field changes, and the strip does not move —
    ///     no exception, no log line.
    /// </summary>
    [Fact]
    public void BothInputsNotifyTheDerivedVisibility()
    {
        var source = Vm();

        foreach (var field in new[] { "_isGameplayPresentation", "_isMinimapVisible" })
        {
            var declaration = source.IndexOf($"private bool {field};", StringComparison.Ordinal);
            if (declaration < 0)
                declaration = source.IndexOf($"private bool {field} =", StringComparison.Ordinal);
            Assert.True(declaration > 0, $"{field} not found");

            // The attribute block sits immediately above the field declaration.
            var blockStart = Math.Max(0, declaration - 500);
            Assert.Contains("[NotifyPropertyChangedFor(nameof(IsMinimapShown))]",
                source[blockStart..declaration]);
        }
    }

    /// <summary>
    ///     The View menu drives the PREFERENCE, never the derived value. A
    ///     two-way binding onto a computed getter is a silent no-op — and would
    ///     also let the menu claim the minimap is visible during a run.
    /// </summary>
    [Fact]
    public void TheViewMenuTogglesThePreference()
    {
        var menu = MainMenuSource.ReadAllText();

        Assert.Contains(@"IsChecked=""{Binding IsMinimapVisible, Mode=TwoWay}""", menu);
        Assert.DoesNotContain("IsMinimapShown", menu);
    }

    /// <summary>
    ///     The preference is persisted, and ONLY the preference is. If entering
    ///     the gameplay presentation wrote the config, a single run would turn a
    ///     temporary hide into the user's saved setting.
    /// </summary>
    [Fact]
    public void OnlyTheUsersOwnChangeIsPersisted()
    {
        var source = Vm();

        var handler = source.IndexOf("partial void OnIsMinimapVisibleChanged", StringComparison.Ordinal);
        Assert.True(handler > 0, "OnIsMinimapVisibleChanged not found");
        var body = source[handler..(handler + 500)];
        Assert.Contains("ShowMinimap = value", body);
        Assert.Contains("SaveAppConfig()", body);

        var gameplay = source.IndexOf("partial void OnIsGameplayPresentationChanged", StringComparison.Ordinal);
        Assert.True(gameplay > 0, "OnIsGameplayPresentationChanged not found");
        Assert.DoesNotContain("ShowMinimap", source[gameplay..(gameplay + 1200)]);
        Assert.DoesNotContain("IsMinimapVisible", source[gameplay..(gameplay + 1200)]);
    }
}
