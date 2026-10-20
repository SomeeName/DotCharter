// ToneDesignViewModel root: the shared ToneDesignCoreViewModel plus this app's two blocks (.Match, .Sections) - the constructor.
using DotCharter.Tone.Ai;
using DotCharter.Tone.Audio.Dsp.Presets;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.Infrastructure.Shell;
using DotCharter.Tone.UI.ViewModels.PlayMode;

namespace DotCharter.UI.ViewModels.PlayMode;

/// <summary>
///     DotCharter's "Design a tone": the shared core
///     (<see cref="ToneDesignCoreViewModel" />, in DotCharter.Tone.UI) plus the
///     two blocks only this app can offer — "Match from audio"
///     (<c>ToneDesignViewModel.Match.cs</c>) and "Match by section"
///     (<c>ToneDesignViewModel.Sections.cs</c>). Everything the core does is
///     documented there; the standalone RigLab hosts the core alone.
/// </summary>
public sealed partial class ToneDesignViewModel : ToneDesignCoreViewModel
{
    /// <summary>Creates the panel's view model over the host's seams.</summary>
    /// <param name="service">The design service, resolved per read.</param>
    /// <param name="apply">The drawer's apply seam.</param>
    /// <param name="snapshotLive">Wraps the live chain as a user preset.</param>
    /// <param name="saveUser">The drawer's save seam.</param>
    /// <param name="localization">The host's strings.</param>
    /// <param name="moduleName">Module type id to display name.</param>
    /// <param name="post">Marshals onto the UI thread; inline by default.</param>
    /// <param name="logger">Optional app logger.</param>
    /// <param name="clipboard">The system clipboard, for the copy and paste route.</param>
    public ToneDesignViewModel(
        Func<IToneDesignService?> service,
        Action<DspTonePreset> apply,
        Func<DspTonePreset> snapshotLive,
        Func<string, DspToneCategory, string, DcRigPreset, DspTonePreset> saveUser,
        ILocalizationService localization,
        Func<string, string>? moduleName = null,
        Action<Action>? post = null,
        IAppLogger? logger = null,
        ISystemClipboard? clipboard = null)
        : base(service, apply, snapshotLive, saveUser, localization, moduleName, post, logger, clipboard)
    {
    }
}
