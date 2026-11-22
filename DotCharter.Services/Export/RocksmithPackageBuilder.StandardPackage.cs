using DotCharter.Services.Export.Package;
using DotCharter.Services.Export.Rocksmith;

namespace DotCharter.Services.Export;

/// <summary>
///     The standard package of a mixed export: the builder runs a second time on the
///     stock arrangements alone, so the RS Extended Range package keeps its bytes and the
///     standard one is exactly what a stock-only export of the same project writes.
/// </summary>
public sealed partial class RocksmithPackageBuilder
{
    private const string VocalsIdentityKey = "\0vocals";

    /// <summary>
    ///     In an RS Extended Range package, a stock Main arrangement on the same path as an
    ///     extended Main ships as Alternate, so each path keeps one Main, the extended one. The
    ///     standard package has no such duplicate and keeps it Main; nothing else changes.
    /// </summary>
    private static List<RsPackageArrangement> DemoteStockDuplicateMains(List<RsPackageArrangement> arrangements)
    {
        var extendedMainPaths = arrangements.Where(a => a.Sng.Extension != null && a.IsRepresentative)
            .Select(a => a.RouteMaskValue).ToHashSet();
        foreach (var stock in arrangements.Where(a => a.Sng.Extension == null && a.IsRepresentative))
            stock.DemotedToAlternate = extendedMainPaths.Contains(stock.RouteMaskValue);
        return arrangements;
    }

    private async Task<RocksmithBuildResult> BuildPackagesAsync(
        ChartProject project,
        RocksmithBuildOptions options,
        IProgress<RocksmithBuildProgress>? progress,
        CancellationToken cancellationToken,
        Func<string, CancellationToken, Task>? beforePublish)
    {
        if (!options.WriteStandardPackage)
            return await BuildAsync(project, options, progress, cancellationToken, beforePublish).ConfigureAwait(false);

        var shared = options with { SharedArrangementIds = new(StringComparer.Ordinal) };
        var extended = await BuildAsync(project, shared, progress, cancellationToken, beforePublish).ConfigureAwait(false);
        if (!extended.IsPsxr || extended.StockArrangements.Count == 0) return extended;

        var standardOptions = shared with
        {
            OutputPath = options.StandardOutputPath
                         ?? ExtendedRangePackageName.Untag(options.OutputPath),
            PlainPackage = options.StandardPlainPackage ?? options.PlainPackage,
            ArrangementFilter = extended.StockArrangements,
            CleanUpChordSidecars = false,
            WriteStandardPackage = false
        };
        RocksmithBuildResult standard;
        try
        {
            standard = await BuildAsync(project, standardOptions, progress, cancellationToken, beforePublish).ConfigureAwait(false);
        }
        catch (InvalidDataException ex)
        {
            // The RS Extended Range package is already published; say why its companion is missing.
            return extended with { Warnings = [.. extended.Warnings, "The standard package was not written: " + ex.Message] };
        }
        return extended with
        {
            Warnings = [.. extended.Warnings.Concat(standard.Warnings).Distinct(StringComparer.Ordinal)],
            PackagePaths = [.. extended.PackagePaths, .. standard.PackagePaths],
            StandardPackagePaths = standard.PackagePaths
        };
    }
}
