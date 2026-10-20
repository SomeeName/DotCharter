using System.Xml.Linq;
using DotCharter.Services.Editing.Catalog;
using DotCharter.Tone.UI.ViewModels.Shell;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI;

/// <summary>
///     The riff library has more hops than the five a menu command needs, so each
///     one is pinned here against the REAL source files. Miss one and the feature
///     exists but is unreachable — or worse, reachable once and dead afterwards.
///     <para>
///         <b>It became an inspector TAB in 0.42.4</b> (it was a dock pane in a
///         second tab strip under the inspector's own, which is what the owner
///         asked about). So the hops changed shape: instead of a
///         <c>DockPaneDefinition</c> + <c>SetPaneVisible</c>, it needs a tab
///         index, a header bound to it, a hosted view and the shell path that
///         SELECTS the tab when something asks to show it. The visibility flag,
///         the menu item, the palette entries, the keybinds and the
///         <c>ViewLocator</c> convention are unchanged and still pinned below.
///     </para>
/// </summary>
public sealed class PhraseCatalogReachabilityTests
{
    /// <summary>
    ///     The tab exists, its header is bound to it, and its view is hosted.
    ///     A header bound to the wrong flag, or a missing panel, is silent — the
    ///     tab strip simply never shows that surface.
    /// </summary>
    [Fact]
    public void TheTabIsDeclaredHeaderedAndHosted()
    {
        var vm = InspectorSource();
        Assert.Contains("RiffLibraryTabIndex", vm);
        Assert.Contains("IsRiffLibraryTabSelected", vm);
        // The catalog reads every stored riff off disk, so it must load when the
        // tab is opened rather than on the boot path.
        Assert.Contains("RiffLibrary?.EnsureLoaded()", vm);

        var view = Read("DotCharter.UI", "Views", "Editor", "InspectorView.axaml");
        Assert.Contains("IsRiffLibraryTabSelected", view);
        Assert.Contains("PhraseCatalogView", view);
    }

    /// <summary>
    ///     "Show the riff library" still shows it. The View-menu item, the
    ///     palette entry and the keybind all drive <c>IsPhraseCatalogVisible</c>;
    ///     with the pane gone, that flag has to SELECT the tab — and open the
    ///     inspector first, or the tab is selected inside a closed pane.
    /// </summary>
    [Fact]
    public void AskingToShowItSelectsTheTab()
    {
        var window = MainWindowCodeBehind();
        Assert.Contains("IsPhraseCatalogVisible", window);
        Assert.Contains("ShowInspectorTab(InspectorViewModel.RiffLibraryTabIndex)", window);

        // Panel visibility — ShowInspectorTab included — is the shell view model's
        // MainWindowViewModel.Layout.cs partial.
        var shell = Read("DotCharter.UI", "ViewModels", "Shell", "MainWindowViewModel.Layout.cs");
        var start = shell.IndexOf("public void ShowInspectorTab", StringComparison.Ordinal);
        Assert.True(start > 0, "ShowInspectorTab not found on the shell view model");
        var body = shell[start..(start + 400)];
        Assert.Contains("IsInspectorVisible = true", body);
        Assert.Contains("SelectedTabIndex = tabIndex", body);
    }

    /// <summary>
    ///     The dock panes are GONE, which is the whole point — one tab strip, not
    ///     two. A pane left registered would put the surface in both places.
    /// </summary>
    [Fact]
    public void NeitherSurfaceIsStillADockPane()
    {
        var window = MainWindowCodeBehind();

        Assert.DoesNotContain("Id = \"PhraseCatalog\"", window);
        Assert.DoesNotContain("Id = \"StemMixer\"", window);
    }

    [Fact]
    public void TheViewModelResolvesToItsViewByTheViewLocatorConvention()
    {
        // ViewLocator maps VM → View by replacing "ViewModel" with "View" in the
        // full type name; a mismatch renders a "Not Found: …" placeholder in the
        // dock instead of the pane.
        var resolved = DotCharter.Tone.UI.ViewLocator.ResolveViewType(typeof(PhraseCatalogViewModel));

        Assert.NotNull(resolved);
        Assert.Equal("DotCharter.UI.Views.Editor.PhraseCatalogView", resolved!.FullName);
    }

    [Fact]
    public void TheViewModelDerivesFromViewModelBase() =>
        // ViewLocator.Match keys on ViewModelBase — a plain ObservableObject
        // silently never resolves.
        Assert.True(typeof(ViewModelBase).IsAssignableFrom(typeof(PhraseCatalogViewModel)));

    [Fact]
    public void ItIsReachableFromTheMenuAndThePalette()
    {
        var menu = MainMenuSource.ReadAllText();
        var palette = Read("DotCharter.UI", "ViewModels", "CommandPalette", "CommandCatalog.cs");

        Assert.Contains("Menu_PanelPhraseCatalog", menu);
        Assert.Contains("IsPhraseCatalogVisible", menu);
        Assert.Contains("view.togglePhraseCatalog", palette);
        Assert.Contains("tools.phraseCatalogAdd", palette);
        Assert.Contains("tools.phraseCatalogPaste", palette);
        Assert.Contains("tools.phraseCatalogPasteByName", palette);
    }

    [Theory]
    [InlineData("TogglePhraseCatalog")]
    [InlineData("PhraseCatalogAdd")]
    [InlineData("PhraseCatalogPaste")]
    [InlineData("PhraseCatalogPasteByName")]
    public void EachKeybindIsDeclaredRegisteredAndRebindable(string action)
    {
        var config = Read("DotCharter.Services", "Infrastructure", "Configuration", "KeybindConfig.cs");
        var registrar = Read("DotCharter.UI", "Input", "AppKeybindRegistrar.cs");
        var settings = Read("DotCharter.Presentation", "Screens", "Settings", "KeybindRowCatalog.cs");

        // The field is _camelCase; the generated property is PascalCase.
        var field = "_" + char.ToLowerInvariant(action[0]) + action[1..];
        Assert.Contains(field, config);
        // A declared-but-unregistered chord is a dead binding (the shipped
        // ToggleMidi bug); an unlisted one cannot be rebound in Settings.
        Assert.Contains($"_registry.Register(() => _projectState.Keybinds.{action},", registrar);
        Assert.Contains($"nameof(KeybindConfig.{action})", settings);
    }

    [Fact]
    public void EveryPhraseCatalogStringExistsInBothResxFiles()
    {
        var en = ReadResxKeys("Resources.resx");
        var de = ReadResxKeys("Resources.de-DE.resx");

        var required = new[]
        {
            "Panel_PhraseCatalog_Title", "Menu_PanelPhraseCatalog",
            "PhraseCatalog_Issue_StringOutOfRange", "PhraseCatalog_Issue_FretOutOfRange",
            "PhraseCatalog_Issue_FretBelowCapo", "PhraseCatalog_Issue_TuningDiffers",
            "PhraseCatalog_Issue_CapoDiffers",
            "Keybind_TogglePhraseCatalog", "Keybind_PhraseCatalogAdd",
            "Keybind_PhraseCatalogPaste", "Keybind_PhraseCatalogPasteByName"
        };

        foreach (var key in required)
        {
            Assert.True(en.Contains(key), $"Resources.resx is missing '{key}'.");
            Assert.True(de.Contains(key), $"Resources.de-DE.resx is missing '{key}'.");
        }
    }

    [Fact]
    public void ABackupImportDropsTheCatalogCache()
    {
        // The whole-install backup includes <ConfigRoot>/phrases, so an import
        // replaces the entry files BEHIND the service that caches them. Without
        // this hop the pane keeps showing the pre-import library and the next
        // add writes into a stale view of the folder.
        var stores = Read("DotCharter.Presentation", "Screens", "Settings", "ImportedConfigStores.cs");
        Assert.Contains("phrases.InvalidateCache()", stores);

        // …and the import drives it, in its finally too.
        var backup = Read("DotCharter.Presentation", "Screens", "Settings", "BackupMigrateViewModel.cs");
        Assert.Contains("_stores.Reload()", backup);
        Assert.Contains("_stores.DropCaches()", backup);
    }

    [Fact]
    public void TheCompatibilityIssueKeysThatTheCheckEmitsAreAllDefined()
    {
        // The check returns localization KEYS, so a renamed key builds clean and
        // shows the user a raw lookup miss exactly when a paste is refused —
        // the worst possible moment.
        var en = ReadResxKeys("Resources.resx");

        foreach (var key in new[]
                 {
                     PhrasePasteCompatibility.KeyStringOutOfRange,
                     PhrasePasteCompatibility.KeyFretOutOfRange,
                     PhrasePasteCompatibility.KeyFretBelowCapo,
                     PhrasePasteCompatibility.KeyTuningDiffers,
                     PhrasePasteCompatibility.KeyCapoDiffers
                 })
        {
            Assert.True(en.Contains(key), $"PhrasePasteCompatibility emits '{key}' but no resx defines it.");
        }
    }

    private static HashSet<string> ReadResxKeys(string fileName)
    {
        var path = Path.Combine(TestPaths.RepoRoot(), "DotCharter.Localization", fileName);
        Assert.True(File.Exists(path), $"resx not found at {path}");
        return XDocument.Load(path).Root!
            .Elements("data")
            .Select(d => d.Attribute("name")?.Value)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>The whole <c>InspectorViewModel</c>, which is a set of topic
    /// partials — reading only the root would make this guard blind the day the
    /// tab block moves out of it.</summary>
    private static string InspectorSource()
    {
        var dir = UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "ViewModels", "Editor"));
        var files = UiSourceTree.GetFiles(dir, "InspectorViewModel*.cs");
        Assert.NotEmpty(files);
        return string.Join("\n", files.Select(UiSourceTree.ReadAllText));
    }

    private static string Read(params string[] parts)
    {
        var path = Path.Combine(new[] { TestPaths.RepoRoot() }.Concat(parts).ToArray());
        Assert.True(File.Exists(path), $"Source file not found at {path}");
        return UiSourceTree.ReadAllText(path);
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
