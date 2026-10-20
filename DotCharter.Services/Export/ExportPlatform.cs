namespace DotCharter.Services.Export;

/// <summary>Target platform of a Rocksmith 2014 package build.</summary>
public enum ExportPlatform
{
    /// <summary>Windows / Steam PC package (<c>_p.psarc</c>).</summary>
    Pc,

    /// <summary>Mac package (<c>_m.psarc</c>), always encrypted.</summary>
    Mac,

    /// <summary>Both from one build (DLC Builder's default): <c>_p.psarc</c> and
    /// <c>_m.psarc</c> side by side.</summary>
    PcAndMac
}
