using System.Xml.Linq;

namespace DotCharter.Tests.UI;

/// <summary>
///     <c>LibraryAudioCacheStore</c> is a new service, and this repo has shipped
///     the same failure four times: an <c>AddSingleton</c> factory is not invoked
///     until somebody asks for the type, so a registered-but-unresolved service
///     compiles, keeps its own passing unit tests, and reads as done while every
///     method on it is dead code (<c>CoopRigApplier</c>, the 0.22 power tools,
///     couch co-op's entry point). A registration is NOT a use.
///     <para>
///         So these lint the whole chain instead: the store is registered, it is
///         RESOLVED by two callers outside its own folder, a package source
///         actually DRIVES it (a store nobody calls <c>PrepareForUse</c> on caps
///         nothing), and the Settings row's bindings and strings all exist.
///     </para>
/// </summary>
public sealed class LibraryAudioCacheReachabilityTests
{
    [Fact]
    public void The_store_is_registered_in_the_composition_root()
    {
        var app = CompositionRootSource();
        Assert.Contains("AddSingleton<LibraryAudioCacheStore>", app);
    }

    /// <summary>
    ///     …and the composition root is the ONLY place that arms it. Automatic
    ///     eviction is off by default because a package source's
    ///     fallback store points at the real <c>%LocalAppData%</c> cache — with the
    ///     default the other way, <c>RocksmithPackageBuilderTests</c> loading its
    ///     own temp <c>goldentest_p.psarc</c> pruned the owner's live 127.6 GB cache
    ///     to the cap in a single gate run. Without this assertion the safe default
    ///     silently becomes a DEAD FEATURE instead: the cap would bound nothing in
    ///     the shipped app and no other test would notice.
    /// </summary>
    [Fact]
    public void Only_the_composition_root_arms_automatic_eviction()
    {
        Assert.Contains("enableAutomaticSweep: true", CompositionRootSource());

        var services = SourceText.StripComments(Read("DotCharter.Services", "Library", "Cache", "LibraryAudioCacheStore.cs"))
                       + SourceText.StripComments(Read("DotCharter.Services", "Library", "LibraryService.cs"))
                       + SourceText.StripComments(Read("DotCharter.Services", "Package", "DcSong", "Library", "DcSongLibrarySource.cs"));
        Assert.DoesNotContain("enableAutomaticSweep: true", services);
    }

    /// <summary>
    ///     …and both consumers ask the CONTAINER for it, rather than each
    ///     newing their own over the same root. Two stores would sweep behind
    ///     each other's backs and report numbers nobody could reconcile.
    /// </summary>
    [Fact]
    public void The_library_service_and_the_settings_page_are_handed_the_same_instance()
    {
        var app = CompositionRootSource();
        Assert.Contains("GetRequiredService<LibraryAudioCacheStore>()", app);

        // Settings > Files & backups asks the container for the same singleton.
        var module = Read("DotCharter.Presentation", "Screens", "Settings", "SettingsModule.cs");
        Assert.Contains("sp.GetService<LibraryAudioCacheStore>()", module);
        var files = Read("DotCharter.Presentation", "Screens", "Settings", "FilesSettingsViewModel.cs");
        Assert.Contains("LibraryAudioCacheStore? cache", files);
    }

    /// <summary>
    ///     The whole point of the row: a user staring at a full disk can see the
    ///     number and reclaim it. A command nothing binds is a button that cannot
    ///     be pressed.
    /// </summary>
    [Fact]
    public void The_settings_row_binds_the_usage_readout_the_clear_and_the_refresh()
    {
        var tab = Read("DotCharter.UI", "Views", "Settings", "FilesSettingsView.axaml");

        Assert.Contains("{Binding LibraryAudioCacheUsage}", tab);
        Assert.Contains("ClearLibraryAudioCacheCommand", tab);
        Assert.Contains("RefreshLibraryAudioCacheCommand", tab);
        Assert.Contains("{Binding LibraryAudioCacheHasEntries}", tab);

        // Searchable: the row is declared to the cross-tab search, and the view
        // hides it by the same key the screen's search puts in HiddenKeys.
        var group = Between(tab, "Settings_PsarcAudioCache_Title", "</Border>");
        Assert.Contains("ConverterParameter=files.psarcCache", group);
    }

    /// <summary>
    ///     The measure has to happen on the path that OPENS the page: the
    ///     settings shell's Load verb reaches Files &amp; backups through
    ///     <c>FilesSettingsViewModel.Load</c>.
    ///     Wired only to the Refresh button, the row would read empty until
    ///     pressed — which is exactly how a user concludes the cache is fine.
    /// </summary>
    [Fact]
    public void Opening_settings_measures_the_cache()
    {
        var files = SourceText.StripComments(Read("DotCharter.Presentation", "Screens", "Settings", "FilesSettingsViewModel.cs"));
        var load = Between(files, "public void Load()", "public void Save()");
        Assert.Contains("RefreshLibraryAudioCacheAsync()", load);
    }

    /// <summary>
    ///     The three readouts are STATUS. Left tracked, measuring a cache would
    ///     move the footer's "N unsaved changes" counter — the exact fault
    ///     <c>BackupStatusText</c> and <c>AsioControlPanelError</c> each shipped.
    /// </summary>
    [Theory]
    [InlineData("LibraryAudioCacheUsage")]
    [InlineData("LibraryAudioCacheHasEntries")]
    [InlineData("LibraryAudioCacheDetail")]
    public void The_cache_readouts_are_excluded_from_the_unsaved_changes_counter(string property)
    {
        var type = typeof(DotCharter.Presentation.Screens.Settings.FilesSettingsViewModel);
        var tracked = (string[])type
            .GetField("Tracked", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetValue(null)!;

        // Two ways this could pass while asserting nothing: an empty tracked set,
        // or a property that no longer exists under that name at all.
        Assert.NotEmpty(tracked);
        var info = type.GetProperty(property);
        Assert.NotNull(info);
        Assert.DoesNotContain(property, tracked);
        Assert.False(info!.SetMethod?.IsPublic == true, $"{property} is status; nothing outside the tab may set it");
    }

    [Theory]
    [InlineData("Settings_PsarcAudioCache_Title")]
    [InlineData("Settings_PsarcAudioCache_Subtitle")]
    [InlineData("Settings_PsarcAudioCache_UsageLabel")]
    [InlineData("Settings_PsarcAudioCache_UsageOne")]
    [InlineData("Settings_PsarcAudioCache_UsageFmt")]
    [InlineData("Settings_PsarcAudioCache_Empty")]
    [InlineData("Settings_PsarcAudioCache_Clear")]
    [InlineData("Settings_PsarcAudioCache_ClearTooltip")]
    [InlineData("Settings_PsarcAudioCache_ClearedOne")]
    [InlineData("Settings_PsarcAudioCache_ClearedFmt")]
    public void Every_string_the_row_shows_exists_in_both_languages(string key)
    {
        foreach (var file in new[] { "Resources.resx", "Resources.de-DE.resx" })
        {
            var path = Path.Combine(TestPaths.RepoRoot(), "DotCharter.Localization", file);
            var value = XDocument.Load(path).Root!
                .Elements("data")
                .FirstOrDefault(d => d.Attribute("name")?.Value == key)
                ?.Element("value")?.Value;
            Assert.False(string.IsNullOrWhiteSpace(value), $"{file} is missing (or empties) '{key}'.");
        }
    }

    private static string Between(string text, string start, string end)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"marker '{start}' not found");
        var to = text.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(to > from, $"marker '{end}' not found after '{start}'");
        return text.Substring(from, to - from);
    }

    /// <summary>The composition root is ONE class spread over several partial
    /// files, so a lint over registrations must read all of them — anchored on
    /// one file it silently stops covering whichever slice a registration moved
    /// into.</summary>
    private static string CompositionRootSource() =>
        string.Concat(UiSourceTree
            .EnumerateFiles(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI"), "App*.cs")
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(UiSourceTree.ReadAllText));

    private static string Read(params string[] relativeParts)
    {
        var path = Path.Combine(new[] { TestPaths.RepoRoot() }.Concat(relativeParts).ToArray());
        Assert.True(File.Exists(path), $"source file not found at {path}");
        return UiSourceTree.ReadAllText(path);
    }
}
