// PlayModeViewModel: the Get Ready screen - header, input chip, scroll speed, per-arrangement best, tuning lockup, backing stems and the separation estimate.
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Services.Audio.StemSeparation;
using DotCharter.Services.Gameplay.Scoring;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.Core.Graphics;
using DotCharter.Presentation.Shared;

namespace DotCharter.UI.ViewModels.PlayMode;

/// <summary>
///     The "Get Ready" screen (§6.6) — the pre-play setup between picking a song
///     and starting a run, rebuilt to <c>Get Ready.dc.html</c> on the Fire layer.
///
///     <para><b>Every value here is wired to something real.</b> Where the mockup
///     drew a figure the app could not produce, the backing was BUILT rather than
///     faked — the per-arrangement note count is counted off the loaded chart, the
///     per-path personal best comes from the practice store's own per-arrangement
///     profile (through the key <c>LivePlayViewModel</c> writes it under), the tone
///     chips are the tones that really converted, and the stem-separation estimate
///     is what separation really cost on THIS machine. The one figure with no
///     honest source before its first measurement — that estimate — is HIDDEN
///     rather than guessed.</para>
/// </summary>
public partial class PlayModeViewModel
{
    // ── Header ───────────────────────────────────────────────────────────────

    /// <summary>The song's artist, beside the title in the header lockup.
    /// Empty when the library never had one — the header then just shows the
    /// title rather than a placeholder.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFlowSongArtist))]
    private string _flowSongArtist = "";

    public bool HasFlowSongArtist => !string.IsNullOrWhiteSpace(FlowSongArtist);

    /// <summary>
    ///     The header's title: the song's own title, because the artist sits beside it.
    ///     <see cref="FlowSongTitle" /> is "Artist - Title" for the screens that print no
    ///     artist, and here it read "3 Doors Down - When I'm Gone  3 Doors Down".
    /// </summary>
    [ObservableProperty] private string _flowSongName = "";

    /// <summary>The song's own title when an artist is printed beside it, else the full "Artist - Title".</summary>
    internal static string HeaderTitle(string title, string artist, string fullTitle) =>
        title.Length > 0 && artist.Length > 0 ? title : fullTitle;

    /// <summary>
    ///     Is an input actually open? The nearest real signal the engine has —
    ///     <c>InputSampleRate &gt; 0</c> means a capture device is running, which
    ///     is what <c>LivePlayViewModel</c> itself tests. It is NOT "a guitar is
    ///     plugged into it": nothing in the stack can know that, so the chip says
    ///     "input" and the tuner's own readings are what prove a guitar is there.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InputChipLabel))]
    private bool _isInputOpen;

    /// <summary>The engine's REAL measured output latency, in ms. Zero means no
    /// engine or no device — never a fabricated figure.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InputChipLabel))]
    private double _preRunLatencyMs;

    /// <summary>"INPUT · 12 MS" when a device is open, else "NO INPUT".</summary>
    public string InputChipLabel => IsInputOpen
        ? _localization.Format("PlayMode_GetReady_InputChip", PreRunLatencyMs)
        : _localization["PlayMode_GetReady_NoInput"];

    /// <summary>Best accuracy on the SELECTED path, formatted, or empty when it
    /// has never been finished.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFlowBest))]
    [NotifyPropertyChangedFor(nameof(FlowBestHeaderLabel))]
    private string _flowBestLabel = "";

    /// <summary>The grade letter that goes with <see cref="FlowBestLabel" />.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FlowBestHeaderLabel))]
    private string _flowBestGrade = "";

    public bool HasFlowBest => !string.IsNullOrEmpty(FlowBestLabel);

    /// <summary>"BEST 97.4% · A" — hidden entirely when this path has never been
    /// finished, rather than reading "BEST 0.0% · F".</summary>
    public string FlowBestHeaderLabel => HasFlowBest
        ? _localization.Format("PlayMode_GetReady_Best", FlowBestLabel, FlowBestGrade)
        : "";

    /// <summary>"E STANDARD · CAPO 2" for the selected path — the same sentence
    /// the Song Library's tuning column shows, so the two screens cannot
    /// disagree about what the guitar has to be set to.</summary>
    [ObservableProperty] private string _flowTuningAndCapoLabel = "";

    /// <summary>True when the chart needs the guitar changed before playing (a
    /// non-standard tuning or a capo) — the header lockup goes amber.</summary>
    [ObservableProperty] private bool _flowTuningNeedsAttention;

    /// <summary>The tuner's heading: the tuning the strings are checked against. The chart's,
    /// or the player's own while the song is shifted to it.</summary>
    [ObservableProperty] private string _flowTunerTarget = "";

    /// <summary>Under <see cref="FlowTunerTarget" />: whose tuning it is.</summary>
    [ObservableProperty] private string _flowTunerTargetCaption = "";

    // ── How it plays ─────────────────────────────────────────────────────────

    public double RunScrollSpeedMinimum => HighwaySpeedPolicy.MinScrollSpeed;
    public double RunScrollSpeedMaximum => HighwaySpeedPolicy.MaxScrollSpeed;

    /// <summary>One step of the −/+ pair, matching the numeric field it
    /// replaced.</summary>
    public const double ScrollSpeedStep = 0.1;

    /// <summary>
    ///     "2.3 S ON SCREEN" — what the scroll-speed number MEANS, derived and
    ///     never printed as a constant. Scroll speed is in Rocksmith units where
    ///     lower is faster, so the raw number tells a player nothing;
    ///     <c>HighwaySpeedPolicy.HorizonMs</c> folds it together with the
    ///     highway's own draw distance (both live settings) into the seconds of
    ///     chart the board really holds.
    /// </summary>
    public string RunVisibleSecondsOnScreenLabel =>
        _localization.Format("PlayMode_GetReady_OnScreen",
            HighwaySpeedPolicy.HorizonMs((float)RunScrollSpeed,
                (float)_projectState.HighwayDrawDistanceZ) / 1000f);

    [RelayCommand]
    private void ScrollSpeedDown() =>
        RunScrollSpeed = Math.Round(
            Math.Max(RunScrollSpeedMinimum, RunScrollSpeed - ScrollSpeedStep), 1);

    [RelayCommand]
    private void ScrollSpeedUp() =>
        RunScrollSpeed = Math.Round(
            Math.Min(RunScrollSpeedMaximum, RunScrollSpeed + ScrollSpeedStep), 1);

    /// <summary>
    ///     Makes THIS run's scroll speed the standing one: writes it to
    ///     <c>AppConfig.HighwayScrollSpeed</c> and turns the global override on,
    ///     which is exactly the pair Settings → Graphics writes. Scroll speed is
    ///     the only per-run value on this screen — hit window, chord strictness
    ///     and the CQT verifier are already app-wide and persist the moment they
    ///     are changed.
    /// </summary>
    [RelayCommand]
    private void SaveRunSettingsAsDefault()
    {
        var config = _configService.LoadAppConfig();
        config.HighwayScrollSpeed = RunScrollSpeed;
        config.HighwayScrollSpeedOverrideEnabled = true;
        _configService.SaveAppConfig(config);
        RunScrollSpeedIsFromChart = false;
        IsRunSpeedSavedAsDefault = true;
    }

    /// <summary>Latches once the standing speed has been written this visit, so
    /// the action can confirm it did something.</summary>
    [ObservableProperty] private bool _isRunSpeedSavedAsDefault;

    // ── In-game tones ────────────────────────────────────────────────────────

    /// <summary>The tones that really rebuilt on the rig, by the key the chart
    /// names them with — each one the button that opens it for editing. Empty
    /// for a chart with no parseable tones.</summary>
    public ObservableCollection<SongToneChip> ToneChips { get; } = [];

    /// <summary>The real per-effect approximation lines from the converter's own
    /// reports — what "Review" opens. Never a summary sentence: the point of
    /// reviewing is to see WHICH effects were folded.</summary>
    public ObservableCollection<string> ToneApproximationNotes { get; } = [];

    [ObservableProperty] private bool _areToneApproximationsExpanded;

    /// <summary>Review = show the real per-effect list inline. There is no
    /// separate dialog to open and inventing one would be a second surface for
    /// data that already fits here.</summary>
    [RelayCommand]
    private void ReviewToneApproximations() =>
        AreToneApproximationsExpanded = !AreToneApproximationsExpanded;

    // ── Backing track ────────────────────────────────────────────────────────

    /// <summary>The separated stems, WITHOUT the "Original Mix" master strip —
    /// the master is the thing the stems replace, not one of them.</summary>
    public ObservableCollection<StemTrackViewModel> BackingStems { get; } = [];

    /// <summary>
    ///     How long separating this song will probably take on this machine, or
    ///     empty before the machine has ever finished one. Derived from the
    ///     measured rate in <c>AppConfig.StemSeparationSecondsPerAudioSecond</c>
    ///     times this song's real length — there is no constant to fall back on,
    ///     so an unmeasured machine shows the action with no estimate at all.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStemSeparationEstimate))]
    private string _stemSeparationEstimateLabel = "";

    public bool HasStemSeparationEstimate => !string.IsNullOrEmpty(StemSeparationEstimateLabel);

    // ── Footer ───────────────────────────────────────────────────────────────

    /// <summary>Raised by the footer's "T" hint / tuner action; the shell owns
    /// the tuner dialog, exactly like <c>StemSeparationRequested</c>.</summary>
    public event Action? TunerRequested;

    [RelayCommand]
    private void OpenTuner() => TunerRequested?.Invoke();

    // ── Refresh ──────────────────────────────────────────────────────────────

    /// <summary>
    ///     Stamps the practice store's per-arrangement best onto a path row. The
    ///     store is keyed by the SAME pair <c>LivePlayViewModel</c> records under
    ///     — a mismatch here would silently report every path as unplayed, which
    ///     is indistinguishable from a genuinely new song.
    /// </summary>
    private ArrangementOption ApplyArrangementBest(ArrangementOption option)
    {
        var songKey = LivePlayViewModel.ProfileSongKey(
            _projectState.CurrentProjectPath, _projectState.CurrentSongIdentity);
        var stats = _stats.GetSongStats(songKey,
            LivePlayViewModel.ProfileArrangementKey(option.Arrangement));

        if (stats is { BestAccuracy: > 0 })
        {
            option.BestAccuracy = stats.BestAccuracy;
            option.BestLabel = FormatAccuracy(stats.BestAccuracy);
        }
        else
        {
            option.BestAccuracy = null;
            option.BestLabel = "";
        }

        return option;
    }

    /// <summary>A path's best, as Song select's row prints the same figure (<see cref="PlaySongItem.AccuracyLabel" />).</summary>
    internal static string FormatAccuracy(double accuracy) => PercentText.Format(accuracy, 1);

    /// <summary>
    ///     Everything on the screen that depends on WHICH path is selected.
    ///     Called from <c>OnSelectedFlowArrangementChanged</c> and from
    ///     <c>PreparePrePlay</c>, so switching path re-answers the header, the
    ///     tuning lockup and the scroll-speed meaning together.
    /// </summary>
    private void RefreshGetReadyForArrangement(ArrangementOption? option)
    {
        FlowBestLabel = option?.BestLabel ?? "";
        FlowBestGrade = option is { BestAccuracy: { } acc }
            ? GradeCalculator.Label(GradeCalculator.FromAccuracy(acc))
            : "";

        // The drop tuner and "Play in my tuning" verdicts are per path; their Changed
        // re-answers the lockup. The drop tuner first: it decides whether the other is live.
        DropTuner.Refresh(option);
        TuningMatch.Refresh(option);
        RefreshTuningLockup(option);

        OnPropertyChanged(nameof(RunVisibleSecondsOnScreenLabel));
    }

    /// <summary>
    ///     The tuning lockup: what the guitar has to BE. While the song is shifted to the
    /// player's tuning that is THEIR tuning, not the chart's — the frets are the
    /// same and the strings stay where they are. While the drop tuner is on it is
    ///     standard: the input comes down to the song instead.
    /// </summary>
    private void RefreshTuningLockup(ArrangementOption? option)
    {
        var capo = option?.Arrangement.Capo ?? 0;
        var dropTuned = option != null && DropTuner.IsOn;
        var shifted = option != null && TuningMatch.EffectiveSemitones != 0;
        var tuning = dropTuned ? DropTuner.StandardTuningName
            : shifted ? TuningMatch.MyTuningName
            : option?.TuningDisplayLabel ?? "";
        FlowTuningAndCapoLabel = capo > 0
            ? _localization.Format("PlayMode_GetReady_TuningWithCapo", tuning, capo)
            : tuning;
        // Amber means "you have to do something to the guitar first" — a capo
        // always does, and so does any tuning that is not the standard one, unless
        // the song is coming to the guitar instead.
        FlowTuningNeedsAttention = capo > 0 || (!shifted && !dropTuned && option is { UniformTuningStep: not 0 });
        FlowTunerTarget = tuning;
        FlowTunerTargetCaption = _localization[dropTuned ? "PlayMode_GetReady_Tuner_DropTuner"
            : shifted ? "PlayMode_GetReady_Tuner_YourTuning"
            : "PlayMode_GetReady_Tuner_Expected"];
    }

    /// <summary>How far above the chart's tuning the guided check aims the strings: the song
    /// shift while playing in the player's tuning, or back up to standard while the drop tuner
    /// brings the input down. Never both — the drop tuner wins.</summary>
    private int GuidedTuningTargetTranspose =>
        DropTuner.IsOn ? -DropTuner.EffectiveSemitones : TuningMatch.EffectiveSemitones;

    /// <summary>The player flipped the drop tuner, or the path changed its verdict: tell
    /// "Play in my tuning" whether it is overruled, then re-answer what both feed.</summary>
    private void OnDropTunerChanged()
    {
        TuningMatch.SetDropTunerOn(DropTuner.IsOn);
        OnTuningMatchChanged();
    }

    /// <summary>The player flipped "Play in my tuning" or picked another tuning of
    /// their own: re-answer the lockup, and re-aim a running guided check at the
    /// strings the run will now expect.</summary>
    private void OnTuningMatchChanged()
    {
        RefreshTuningLockup(SelectedFlowArrangement);
        if (IsTuningCheckScreen && GuidedTuningTargetTranspose != _guidedTuningTranspose)
            BeginGuidedTuning();
    }

    /// <summary>(Re)starts the guided check on the selected path, aimed at the player's
    /// tuning while the song is shifted to it, or at standard under the drop tuner.</summary>
    private void BeginGuidedTuning()
    {
        _guidedTuningTranspose = GuidedTuningTargetTranspose;
        GuidedTuning.Begin(SelectedFlowArrangement?.Arrangement, _guidedTuningTranspose);
    }

    /// <summary>Song-level facts that do not change with the selected path.</summary>
    private void RefreshGetReadySongFacts()
    {
        FlowSongArtist = _flowSong?.Artist ?? "";
        FlowSongName = HeaderTitle(_flowSong?.Title ?? "", FlowSongArtist, FlowSongTitle);
        IsRunSpeedSavedAsDefault = false;
        AreToneApproximationsExpanded = false;
        RefreshBackingStems();
        RefreshStemSeparationEstimate();
    }

    /// <summary>Rebuilds the per-stem rows from the real mixer, dropping the
    /// master strip. Called whenever stem availability changes.</summary>
    private void RefreshBackingStems()
    {
        BackingStems.Clear();
        if (_stemMixer?.Tracks == null) return;
        foreach (var track in _stemMixer.Tracks.Where(t => !t.IsMasterMix))
            BackingStems.Add(track);
    }

    private void RefreshStemSeparationEstimate()
    {
        var rate = _configService.LoadAppConfig().StemSeparationSecondsPerAudioSecond;
        var seconds = StemSeparationCost.EstimateSeconds(rate, _projectState.TotalAudioLengthMs / 1000.0);
        if (seconds is not { } s)
        {
            // Never measured on this machine (or no audio yet) — the action ships
            // WITHOUT an estimate rather than with a made-up one.
            StemSeparationEstimateLabel = "";
            return;
        }

        var loc = _localization;
        StemSeparationEstimateLabel = s >= 90
            ? loc.Format("PlayMode_GetReady_StemEstimate_Min", Math.Round(s / 60.0))
            : loc.Format("PlayMode_GetReady_StemEstimate_Sec", Math.Round(s / 5.0) * 5);
    }

    /// <summary>
    ///     One tick of the Get Ready screen's live readouts, driven by the view's
    ///     own timer (the SAME clock and the SAME capture tap the settings
    ///     modal's meter uses — no second audio pump). A no-op off the screen.
    /// </summary>
    public void PollGetReadyMeters()
    {
        if (!IsTuningCheckScreen) return;
        GetReadyMeterPolls++;

        Settings.SampleInputLevel();
        IsInputOpen = _audio.InputSampleRate > 0;
        PreRunLatencyMs = _audio.MeasuredOutputLatencyMs;
    }

    /// <summary>Counts how many times the view has driven this screen's meters.
    /// The poll is the one thing here with a clock in it, so this is the seam a
    /// test uses to assert it runs on the screen and NOT off it.</summary>
    internal int GetReadyMeterPolls { get; private set; }
}
