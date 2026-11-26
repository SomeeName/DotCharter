using DotCharter.Services.Export;
using DotCharter.Services.Export.Rocksmith;

namespace DotCharter.Presentation.Screens.Dialogs;

internal static class RocksmithExportOwnPackages
{
    /// <summary>
    ///     Every package path this output name can produce (each platform, tagged and standard): a Library
    ///     package at one of them is this project's own earlier export, which the build replaces.
    /// </summary>
    public static IReadOnlyList<string> For(string outputPath, ExportPlatform platform, bool psxr)
    {
        if (string.IsNullOrWhiteSpace(outputPath)) return [];
        var files = new List<string>();
        foreach (var mac in (bool[])[false, true])
        {
            var path = RocksmithPlatformTargets.OutputPathFor(outputPath, platform, mac);
            files.Add(path);
            files.Add(ExtendedRangePackageName.Untag(path));
            if (!mac) continue;
            var tagged = ExtendedRangePackageName.ApplyMac(path, psxr);
            files.Add(tagged);
            files.Add(ExtendedRangePackageName.Untag(tagged));
        }

        return [.. files.Distinct(StringComparer.OrdinalIgnoreCase)];
    }
}
