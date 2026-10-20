using DotCharter.Services.Plugins;

namespace DotCharter.Services.Infrastructure.Features;

/// <summary>
/// One feature the public-build switch removes. The id, menu keys,
///     palette ids and component ids are what a build with the switch off leaves
///     out; <c>docs/agents/services/contested-features.md</c> lists the same set
///     and <c>ContestedFeaturesDocSyncTests</c> keeps the two equal.
/// </summary>
/// <param name="Id">Stable kebab-case id, the first column of the docs table.</param>
/// <param name="MenuKeys">
///     The <c>Menu_*</c> resource keys of the rows that open it. Each row carries
///     <c>ContestedMenu.Feature="&lt;id&gt;"</c> in its axaml and is removed, not disabled.
/// </param>
/// <param name="PaletteCommandIds">The command-palette ids that run it.</param>
/// <param name="ComponentIds"><c>ComponentCatalog</c> entries that only exist for it.</param>
/// <param name="SourcePaths">Repo-relative files and folders that hold it (the docs list them too).</param>
public sealed record ContestedFeature(
    string Id,
    IReadOnlyList<string> MenuKeys,
    IReadOnlyList<string> PaletteCommandIds,
    IReadOnlyList<string> ComponentIds,
    IReadOnlyList<string> SourcePaths);

/// <summary>
///     The public-build switch. MSBuild property <c>DotCharterContestedFeatures</c>
///     (default <c>true</c>; <c>Directory.Build.props</c>) defines the compile
///     constant <c>CONTESTED_FEATURES</c>; <see cref="Enabled" /> is that constant
///     as a <c>const</c>, so the off build folds every guarded branch away.
/// </summary>
/// <remarks>
///     <para>
///         <b>Off means absent, not greyed.</b> Nothing is registered with DI
///         (<c>ContestedFeaturesModule</c>), the menu rows are removed
///         (<c>ContestedMenu</c>), the palette entries are never listed, yt-dlp is
///         not listed in the component inventory. Nothing contacts a YouTube host at run time.
///     </para>
///     <para>
///         <b>The code stays in the source.</b> The types are compiled in both builds
///         (a type shared with the rest of the app, such as <c>MediaToolResolver</c>,
///         cannot leave), so the host names remain as string literals in the off
///         binary and in the repository; <c>PRIVACY.md</c> keeps listing them and
///         <c>PrivacyHostsLintTests</c> still passes. A takedown of the SOURCE needs a
///         separate commit that deletes the paths in <see cref="All" />.
///     </para>
///     <para>
///         Every other consumer asks <see cref="Enabled" /> (or takes it as a
///         parameter whose default it is, so a test can pass <c>false</c>).
///     </para>
/// </remarks>
public static class ContestedFeatures
{
#if CONTESTED_FEATURES
    public const bool Enabled = true;
#else
    public const bool Enabled = false;
#endif

    public const string YoutubeImportId = "youtube-import";

    public static IReadOnlyList<ContestedFeature> All { get; } =
    [
        new(YoutubeImportId,
            ["Menu_AttachYoutubeAudio"],
            ["file.attachYoutubeAudio"],
            ["tool-yt-dlp"],
            [
                "DotCharter.Services/Import/Youtube/YtDlpAudioDownloader.cs",
                "DotCharter.Services/Import/Youtube/YoutubeUrl.cs",
                "DotCharter.Presentation/Screens/Dialogs/YoutubeAudioViewModel.cs",
                "DotCharter.Presentation/Screens/Dialogs/YoutubeAudioLauncher.cs",
                "DotCharter.UI/Views/Dialogs/YoutubeAudioDialogView.axaml"
            ])
    ];

    /// <summary>
    ///     Palette ids that READ game packages or the game install, by the
    /// <see cref="PackageFeatures" /> id that gates each. None today: the
    ///     one package import is a Tone Designer button, and the writer's entries stay.
    /// </summary>
    public static IReadOnlyDictionary<string, string> PackagePaletteCommandIds { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    ///     The palette entries of a build with the switch <paramref name="enabled" /> and the
    ///     package <paramref name="packages" />: the contested ones are dropped when off, a
    ///     package one when <paramref name="packages" /> does not allow it.
    /// </summary>
    public static IReadOnlyList<T> FilterPalette<T>(IReadOnlyList<T> commands, Func<T, string> idOf, bool enabled,
        IPackageFeatures? packages = null, IReadOnlyDictionary<string, string>? packagePaletteIds = null)
    {
        packagePaletteIds ??= PackagePaletteCommandIds;
        var hidden = new HashSet<string>(StringComparer.Ordinal);
        if (!enabled) hidden.UnionWith(All.SelectMany(f => f.PaletteCommandIds));
        if (packages is not null)
            hidden.UnionWith(packagePaletteIds.Where(p => !PackageFeatures.Allows(packages, p.Value)).Select(p => p.Key));
        return hidden.Count == 0 ? commands : commands.Where(c => !hidden.Contains(idOf(c))).ToList();
    }
}
