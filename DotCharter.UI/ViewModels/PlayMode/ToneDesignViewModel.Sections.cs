// ToneDesignViewModel: "Match by section" - the IToneSectionHost seam and ConfigureSections; the flow lives in ToneSectionsViewModel.
using DotCharter.Tone.Ai;
using DotCharter.Services.Ai.Match;
using DotCharter.Tone.Audio.Dsp.Presets;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.UI.ViewModels.PlayMode;
using DotCharter.UI.ViewModels.PlayMode.ToneSections;

namespace DotCharter.UI.ViewModels.PlayMode;

/// <summary>
///     "Match by section" — the panel's THIRD door onto the same result block,
///     and the one that produces a whole song's worth of tones at once.
///
///     <para>
///         <b>This partial is a SEAM and nothing else.</b> The flow itself lives
///         in <see cref="ToneSectionsViewModel" /> under
///         <c>ViewModels/PlayMode/ToneSections/</c>, in its own view with its own
///         file set, because a section list is a screen — and a big screen
///         declared inline inside another screen is how <c>PlayModeView</c>
///         reached 780 KB and how the pedalboard overlay ended up with nothing to
///         host. What lives HERE is the host wiring: the five answers the section
///         flow needs from this panel (<see cref="IToneSectionHost" />) and the
///         one <see cref="ConfigureSections" /> call the composition root makes.
///     </para>
///     <para>
///         <b>Every dependency is optional and arrives through
///         <see cref="ConfigureSections" />, not the constructor</b> — the same
///         rule <c>ConfigureMatch</c> follows, for the same reason. With neither a
///         proposer nor a set matcher registered
///         <see cref="IsSectionsAvailable" /> is false and the block is not on
///         screen at all.
///     </para>
/// </summary>
public sealed partial class ToneDesignViewModel : IToneSectionHost
{
    private ToneSectionsViewModel? _sections;

    /// <summary>
    ///     The section list. Built on first read rather than in the constructor:
    ///     it is cheap (a stem picker's four labels), but every host that opens
    ///     the drawer pays for it and only the ones with the section slice wired
    ///     ever show it.
    /// </summary>
    public ToneSectionsViewModel Sections => _sections ??= new ToneSectionsViewModel(this, Loc, Post, Logger);

    /// <summary>False in a build with neither a proposer nor a set matcher
    /// registered, which takes the whole section block off screen — not merely
    /// disabled.</summary>
    public bool IsSectionsAvailable => _sections?.IsAvailable ?? false;

    /// <summary>
    ///     The host hands the section slice in.
    /// </summary>
    /// <param name="proposer">Proposes the song's parts from the audio. Null ⇒
    /// only the chart's own tone changes can be proposed.</param>
    /// <param name="setMatcher">Matches every section in one run. Null ⇒ no
    /// "Match all".</param>
    /// <param name="narrator">Explains a chosen rig in words. Null ⇒ no
    /// "Explain".</param>
    /// <param name="playheadMs">The transport's position, for the two "set from
    /// playhead" buttons. Defaults to the engine this panel was already given
    /// through <c>ConfigureMatch</c>, so a host that wires the match slice gets
    /// working playhead buttons for free.</param>
    /// <param name="placeToneChanges">Places one tone change per entry at its
    /// section's start through the host's undoable edit pipeline, answering how
    /// many it placed — or <c>null</c> when the project has no arrangement, which
    /// is the "presets only, and say so" outcome.</param>
    /// <param name="saveTone">Overrides where a section's tone is saved. Omitted,
    /// it is the drawer's own "Save as tone" path, which is what a host should
    /// want.</param>
    public void ConfigureSections(
        Func<IToneSectionProposer?> proposer,
        Func<IToneSetMatcher?> setMatcher,
        Func<IToneNarrator?>? narrator = null,
        Func<double>? playheadMs = null,
        Func<IReadOnlyList<ToneSetEntry>, int?>? placeToneChanges = null,
        Func<string, DspToneCategory, string, DcRigPreset, DspTonePreset>? saveTone = null)
    {
        _sectionSaveTone = saveTone;
        Sections.Configure(
            proposer,
            setMatcher,
            narrator,
            // The engine already known to the match slice is the resting answer:
            // the playhead is the transport's business and asking a host to wire
            // it twice is how the two drift apart.
            playheadMs ?? (() => _audio()?.CurrentTimeMs ?? 0.0),
            placeToneChanges);

        OnPropertyChanged(nameof(IsSectionsAvailable));
    }

    private Func<string, DspToneCategory, string, DcRigPreset, DspTonePreset>? _sectionSaveTone;

    // ---- IToneSectionHost -----------------------------------------------------
    //
    // Explicit implementations: these are the section flow's view of this panel,
    // not five new members on the panel's own public surface.

    ToneMatchSource? IToneSectionHost.ResolveMatchSource() => ResolveSource();

    Task<MonoAudio?> IToneSectionHost.EnsureReferenceDiAsync(CancellationToken ct) =>
        EnsureReferenceDiAsync(ct);

    string IToneSectionHost.ChainSummary(DcRigPreset rig) => ChainSummary(rig);

    (string? Title, string? Artist) IToneSectionHost.SongContext() =>
        SongContextProvider?.Invoke() ?? (null, null);

    void IToneSectionHost.PublishResult(ToneDesignResult result, string chainSummary) =>
        Result = new ToneDesignResultViewModel(result, chainSummary, Loc);

    DspTonePreset IToneSectionHost.SaveTone(
        string name, DspToneCategory category, string description, DcRigPreset rig) =>
        (_sectionSaveTone ?? SaveUserSeam)(name, category, description, rig);
}
