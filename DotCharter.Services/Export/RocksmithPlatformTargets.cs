using DotCharter.Services.Export.Package;
using DotCharter.Services.Infrastructure.Psarc;

namespace DotCharter.Services.Export;

/// <summary>
/// Which packages one export writes, and where. The Mac package has three rules
///     the PC one does not: it is always ENCRYPTED (a plain package loads only through the
///     Windows CDLC enabler's patch), so it needs the TOC key and the Mac SNG key, and it is never
///     written plain on request. The RS Extended Range package is the exception: it is plain on
///     both platforms, so its Mac form needs no key. A Mac-only export that breaks a rule fails with the reason; PC + Mac writes
///     the PC package alone and says why. The export dialog asks the same questions first
///     (<see cref="MacUnavailableReason" />) so the user sees the reason before exporting.
/// </summary>
public static class RocksmithPlatformTargets
{
    /// <summary>
    ///     Why no Mac package can be written: no Mac encryption
    ///     (<see cref="RocksmithKeys.CanWriteMac" />). Null when the Mac package can be built.
    /// </summary>
    public static RocksmithMacBlock? MacUnavailableReason() =>
        !RocksmithKeys.CanWriteMac ? RocksmithMacBlock.NoEncryption
        : null;

    /// <summary>The package path of <paramref name="mac" />'s platform for an export whose
    /// output field reads <paramref name="outputPath" />: the field itself for a single
    /// platform, the field re-suffixed (<c>_p</c> / <c>_m</c>) for PC + Mac.</summary>
    public static string OutputPathFor(string outputPath, ExportPlatform target, bool mac) =>
        target == ExportPlatform.PcAndMac ? ExportPathDefaults.WithPlatformSuffix(outputPath, mac) : outputPath;

    internal static string OutputPathFor(RocksmithBuildOptions options, RsPlatformLayout platform) =>
        OutputPathFor(options.OutputPath, options.Platform, platform.IsMac);

    /// <summary>The platforms to write, PC first. Throws when the target is Mac only and the
    /// Mac package cannot be built; drops Mac with a warning when PC is written too.</summary>
    internal static IReadOnlyList<RsPlatformLayout> Resolve(RocksmithBuildOptions options,
        IReadOnlyList<RsPackageArrangement> arrangements, List<string> warnings)
    {
        if (options.Platform == ExportPlatform.Pc) return [RsPlatformLayout.Pc];

        // The RS Extended Range package is plain on Mac too, so neither key rule applies to it.
        var reason = arrangements.Any(a => a.Sng.Extension != null)
            ? null
            : MacUnavailableReason() switch
            {
                RocksmithMacBlock.NoEncryption => "a Mac package is always encrypted, and packages are written unencrypted here.",
                _ => options.PlainPackage == true ? "a Mac package is always encrypted, and a plain package was asked for." : null
            };

        if (reason == null)
            return options.Platform == ExportPlatform.Mac ? [RsPlatformLayout.Mac] : [RsPlatformLayout.Pc, RsPlatformLayout.Mac];
        if (options.Platform == ExportPlatform.Mac)
            throw new InvalidDataException("No Mac package can be written: " + reason);

        warnings.Add("Only the PC package was written: " + reason);
        return [RsPlatformLayout.Pc];
    }
}

/// <summary>Why a Mac package cannot be written for the content.</summary>
public enum RocksmithMacBlock
{
    /// <summary>No Mac encryption available: the package would be plain.</summary>
    NoEncryption
}
