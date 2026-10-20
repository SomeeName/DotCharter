using DotCharter.Tone.Audio.Vst;
using DotCharter.Tone.Infrastructure.Configuration;

namespace DotCharter.RigLab;

/// <summary>
///     RigLab's settings file (<c>riglab.json</c>): everything the rig and the
///     audio host read (<see cref="RigConfig" />) plus the few things only this
///     host has. Nothing here is shared with DotCharter's <c>appconfig.json</c>.
/// </summary>
public sealed class RigLabConfig : RigConfig
{
    /// <summary>A fresh install: the whole point of RigLab is hearing the
    /// guitar, so live input starts ON (DotCharter's default is off because
    /// most charting sessions have no guitar plugged in).</summary>
    public static RigLabConfig CreateDefault() => new() { EnableInput = true };

    /// <summary>The VST3 effects on the guitar input, in signal order. Same
    /// shape DotCharter persists as <c>AppConfig.GuitarVstChain</c>.</summary>
    public List<VstChainEntry> GuitarVstChain { get; set; } = [];

    /// <summary>"system", or a culture name the app ships ("en-US", "de-DE",
    /// "ja-JP") — the value <c>LocalizationService.SetConfiguredCulture</c>
    /// takes.</summary>
    public string LanguageCulture { get; set; } = "system";

    public double WindowWidth { get; set; } = 1440;

    public double WindowHeight { get; set; } = 900;

    public bool WindowMaximized { get; set; }
}
