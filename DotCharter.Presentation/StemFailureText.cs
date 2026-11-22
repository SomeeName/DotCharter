using DotCharter.Localization;
using DotCharter.Services.Audio.StemSeparation;

namespace DotCharter.Presentation;

/// <summary>
///     Maps a stem-separation failure code to its localized message. The services keep an English
///     <see cref="StemSeparationResult.ErrorMessage" /> for the log only; this is what the user reads.
/// </summary>
public static class StemFailureText
{
    public static string Resolve(StemSeparationResult result, ILocalizationService loc)
    {
        var key = KeyFor(result.FailureKind);
        if (key is null) return result.ErrorMessage ?? "";
        return result.FailureArgs.Count == 0
            ? loc[key]
            : loc.Format(key, result.FailureArgs.Cast<object>().ToArray());
    }

    /// <summary>The resx key for a failure kind, or null when the raw message is all there is.</summary>
    public static string? KeyFor(StemFailureKind kind) => kind switch
    {
        StemFailureKind.NoBackend => "StemSeparation_Fail_NoBackend",
        StemFailureKind.NativeNotCompiled => "StemSeparation_Fail_NativeNotCompiled",
        StemFailureKind.InputMissing => "StemSeparation_Fail_InputMissing",
        StemFailureKind.ModelMissing => "StemSeparation_Fail_ModelMissing",
        StemFailureKind.NoStemsFound => "StemSeparation_Fail_NoStemsFound",
        StemFailureKind.OnnxNotEngaged => "StemSeparation_Fail_OnnxNotEngaged",
        StemFailureKind.OnnxCannotDecode => "StemSeparation_Fail_OnnxCannotDecode",
        StemFailureKind.OnnxNeedsStereo => "StemSeparation_Fail_OnnxNeedsStereo",
        _ => null
    };
}
