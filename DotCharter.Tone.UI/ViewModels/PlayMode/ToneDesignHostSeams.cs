using DotCharter.Tone.Audio.Dsp.Presets;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Tone.UI.ViewModels.PlayMode;

/// <summary>
///     What a design panel hosted at the top of the preset browser may do to the
///     browser: apply a preset to the live chain, snapshot the live tone (the
///     restore point), and save a designed tone as a user preset — the same paths
///     a card click and the save-as dialog use.
///     <para>
///         The panel itself is the AI tone designer (<c>ToneDesignViewModel</c>,
///         DotCharter.Tone.UI), which needs <c>DotCharter.Tone.Ai</c>. This rig
///         assembly cannot name it, so the host passes a factory
///         (<see cref="PresetBrowserViewModel" />'s <c>toneDesignPanel</c>) and
///         the browser hands it these seams. The standalone rig passes none and
///         the Tones mode simply has no design column.
///     </para>
/// </summary>
public sealed record ToneDesignHostSeams(
    Action<DspTonePreset> Apply,
    Func<DspTonePreset> SnapshotLive,
    Func<string, DspToneCategory, string, DcRigPreset, DspTonePreset> SaveUser,
    Func<string, string>? ModuleName,
    Action<Action>? Post,
    IAppLogger? Logger,
    ILocalizationService Localization);
