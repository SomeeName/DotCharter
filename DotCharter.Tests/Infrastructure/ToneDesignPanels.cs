using DotCharter.Tone.Ai;
using DotCharter.Tone.UI.ViewModels.PlayMode;
using DotCharter.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     The AI "Design a tone" panel for a rig a test builds by hand. The rig
///     (DotCharter.Tone.UI) cannot name <see cref="ToneDesignViewModel" />, so the
///     host passes a factory; this is the one tests pass — the same construction
///     <c>App.ViewModelServices</c> registers, over a design service the test
///     chooses (none by default, i.e. the panel's NOT-CONFIGURED state).
/// </summary>
internal static class ToneDesignPanels
{
    public static Func<ToneDesignHostSeams, object?> Real(Func<IToneDesignService?>? service = null) =>
        seams => new ToneDesignViewModel(
            service ?? (static () => null),
            seams.Apply, seams.SnapshotLive, seams.SaveUser, seams.Localization, seams.ModuleName,
            post: seams.Post, logger: seams.Logger);

    /// <summary>The panel a rig built with <see cref="Real" /> carries.</summary>
    public static ToneDesignViewModel Of(PresetBrowserViewModel browser) =>
        Assert.IsType<ToneDesignViewModel>(browser.ToneDesign);
}
