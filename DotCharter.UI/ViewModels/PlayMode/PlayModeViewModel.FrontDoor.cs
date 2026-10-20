// PlayModeViewModel: the front door's facts snapshot, the selected song and its detail rail - arrangements and the tuning check.
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Shared;

namespace DotCharter.UI.ViewModels.PlayMode;

/// <summary>
///     The front door and the song library's own read-models (0.47) — the two
///     screens rebuilt from the owner's <c>Main Menu Sidebar.dc.html</c> and
///     <c>Song Library.dc.html</c>.
///
///     <para>
///         Sixth partial of <see cref="PlayModeViewModel" />; the field rule is
///         in <c>PlayModeViewModel.cs</c> (injected dependencies and
///         plain fields live in the ROOT file; <c>[ObservableProperty]</c>
///         backing fields travel with their responsibility, which is why they
///         are here).
///     </para>
///     <para>
///         <b>Every figure on both screens is real.</b> The front door's go
///         through <see cref="IFrontDoorFacts" /> and the library rail's
///         per-arrangement note counts through
///         <see cref="ISongArrangementFacts" />, so each is computed in exactly
///         one place instead of scattered as literals in XAML — and a figure
///         with no source is HIDDEN (every one carries a <c>Has*</c>
///         companion) rather than filled with a plausible number.
///     </para>
/// </summary>
public sealed partial class PlayModeViewModel
{
    // ---- The front door's figures -------------------------------------------

    /// <summary>Every number the front door prints, as ONE immutable snapshot.
    /// Starts empty (all <c>Has*</c> false) so a headless construction with no
    /// services renders the chrome and no figures; <see cref="RefreshFrontDoor" />
    /// swaps in the live one on every entry to Song Select.</summary>
    [ObservableProperty] private IFrontDoorFacts _frontDoor = FrontDoorFacts.Empty;

    /// <summary>Rebuilds <see cref="FrontDoor" />. Called on every entry to
    /// Song Select — every figure here is a cheap in-memory read, never a scan and
    /// never a network call.</summary>
    private void RefreshFrontDoor()
    {
        FrontDoorLastSession? last = null;
        try
        {
            // The newest stored run across the whole history. GetMostRecentSession,
            // never GetSessionHistory: that one PROJECTS every stored session into
            // a SessionHistoryItem (and a SessionSectionLine per section) and sorts
            // twice, and this line then sorted the lot a third time to keep exactly
            // one of them. Measured on a 200-arrangement × 6-run × 14-section store,
            // paid on EVERY entry to the Menu: 1.28 ms / 1.48 MB, now one record.
            var newest = _stats.GetMostRecentSession();
            if (newest != null)
            {
                last = new FrontDoorLastSession(
                    Math.Max(1, (int)Math.Round(newest.DurationSeconds / 60.0)).ToString("N0"),
                    PercentText.Format(newest.Accuracy, 1),
                    "+" + newest.Score.ToString("N0"));
            }
        }
        catch (Exception ex)
        {
            // A corrupt practice store must not take the front door down — it is
            // the app's BOOT surface with AppConfig.StartInPlayMode.
            _logger.Warning(AppLogCategory.Application, $"Front-door last-session read failed: {ex.Message}");
        }

        var ownCharts = SongSelect.AllSongs.Count(s => s.IsOwnChart);
        // What the guitar is tuned to: the tuning the player last CONFIRMED on
        // the pre-play check. That is the app's own answer already — the pre-play
        // screen skips the check when a song matches it — so the front door and
        // the library's tuning line cannot disagree with it.
        var verified = _configService.LoadAppConfig().LastVerifiedTuning ?? "";
        var tuning = verified.Length == 0 ? "" : TuningNameResolver.Resolve(verified) ?? verified;

        FrontDoor = new FrontDoorFacts
        {
            LibraryCount = SongSelect.SongTotalCountText,
            AppVersion = FrontDoorFacts.ResolveAppVersion(),
            TuningLabel = tuning,
            OwnChartCount = ownCharts.ToString("N0"),
            HasOwnCharts = ownCharts > 0,
            LastSession = last
        };

        // The library's tuning line compares against the same reading.
        RefreshSelectedSongDetail();
    }

    // ---- Song library: the detail rail --------------------------------------

    /// <summary>The song Song Select's list has selected — and the song every later
    /// screen (Get Ready, Pause, setlists, Home's recent entry) acts on, which is
    /// why it lives here rather than on <see cref="SongSelect" />.</summary>
    [ObservableProperty] private PlaySongItem? _selectedSong;

    /// <summary>Per-arrangement facts for the selected song: the path label and
    /// its REAL note count, read off the library index
    /// (<see cref="SongArrangementFacts" />).</summary>
    public ObservableCollection<SongArrangementFact> SelectedSongArrangements { get; } = [];

    /// <summary>The one place the arrangement read-model is chosen. Static
    /// because it is a policy, not per-instance state (the field rule in
    /// <c>PlayModeViewModel.cs</c> keeps instance fields in the root
    /// file).</summary>
    private static readonly ISongArrangementFacts ArrangementFacts = new SongArrangementFacts();

    /// <summary>Which arrangement the rail has selected — and it DOES something:
    /// <c>PreparePrePlay</c> opens the pre-play screen on the matching path, so
    /// picking "Bass" here is picking the path you play.</summary>
    [ObservableProperty] private SongArrangementFact? _selectedArrangement;

    /// <summary>Picks an arrangement in the rail. A command rather than a
    /// two-way binding because the tiles are <c>ToggleButton</c>s and the
    /// selected one has to be re-stamped across the whole set.</summary>
    [RelayCommand]
    private void SelectArrangement(SongArrangementFact? fact)
    {
        if (fact == null) return;
        for (var i = 0; i < SelectedSongArrangements.Count; i++)
        {
            var current = SelectedSongArrangements[i];
            var selected = string.Equals(current.Label, fact.Label, StringComparison.Ordinal);
            // The PICKED tile is republished even when nothing changed, which is
            // the one case that looks like a pointless write and is not. The tile
            // is a ToggleButton whose IsChecked is bound ONE-WAY: its own click
            // has already flipped the visual to UNCHECKED before this command
            // runs, so only a fresh notification puts it back. Skipping the
            // no-op replace left the tile drawn as unselected while
            // SelectedArrangement still pointed at it and PreparePrePlay still
            // opened the run on it — and because RefreshSelectedSongDetail
            // pre-selects the first path, the very first click a player makes on
            // the rail is this case.
            if (selected || current.IsSelected)
                SelectedSongArrangements[i] = current with { IsSelected = selected };
        }

        SelectedArrangement = SelectedSongArrangements
            .FirstOrDefault(a => string.Equals(a.Label, fact.Label, StringComparison.Ordinal));
    }

    /// <summary>Localized "E STANDARD — IN TUNE" / "TUNE TO DROP D" line. Both
    /// halves are real: the song's expected tuning from the library index, and
    /// the guitar's from the tuning the player last verified
    /// (<see cref="IFrontDoorFacts.TuningLabel" />). With no verified tuning the
    /// line states the song's requirement and claims nothing about the
    /// guitar.</summary>
    [ObservableProperty] private string _selectedTuningCheckLabel = "";

    /// <summary>The song's tuning differs from the verified one — the amber
    /// state of that line.</summary>
    [ObservableProperty] private bool _selectedTuningNeedsChange;

    /// <summary>
    ///     The song's tuning MATCHES the one the player last verified — the green
    ///     state, and the only state entitled to it.
    ///     <para>
    ///         The view must not key the green pill on <c>!SelectedTuningNeedsChange</c>,
    ///         which is a different question. Two of the three branches below
    ///         claim nothing — "this song carries no tuning" (own charts, peer
    ///         rows) and "you have not verified a tuning yet" — and both leave
    ///         <see cref="SelectedTuningNeedsChange" /> false, so both would be
    ///         painted in the IN-TUNE green, with its dot, right beside the Play
    ///         button. Colour is semantic on the Fire layer: green there is the
    ///         screen asserting the guitar is ready for a song it has just said
    ///         it cannot judge.
    ///     </para>
    /// </summary>
    [ObservableProperty] private bool _selectedTuningIsVerified;

    /// <summary>Neither verified nor known-wrong — the neutral pill. Kept as its
    /// own flag rather than a computed negation so the three states are set in
    /// ONE place and cannot overlap.</summary>
    [ObservableProperty] private bool _selectedTuningIsUnverified;

    /// <summary>Refreshes everything the detail rail shows for
    /// <see cref="SelectedSong" />.</summary>
    private void RefreshSelectedSongDetail()
    {
        SelectedSongArrangements.Clear();
        var song = SelectedSong;
        if (song == null)
        {
            SelectedArrangement = null;
            SelectedTuningCheckLabel = "";
            SetTuningCheckState(verified: false, needsChange: false);
            return;
        }

        var facts = ArrangementFacts.For(song);
        for (var i = 0; i < facts.Count; i++)
            SelectedSongArrangements.Add(facts[i] with { IsSelected = i == 0 });
        SelectedArrangement = SelectedSongArrangements.FirstOrDefault();

        var loc = _localization;
        var expected = song.TuningDisplay;
        var verified = FrontDoor.TuningLabel;
        if (expected.Length == 0)
        {
            // Own charts carry no tuning; say so rather than
            // implying the guitar is right for it — NEUTRAL, not green.
            SelectedTuningCheckLabel = loc["PlayMode_SongSelect_TuningUnknown"];
            SetTuningCheckState(verified: false, needsChange: false);
        }
        else if (verified.Length == 0)
        {
            // Nothing verified yet: state the requirement, claim nothing.
            SelectedTuningCheckLabel = loc.Format("PlayMode_SongSelect_NeedsTuningFmt", expected);
            SetTuningCheckState(verified: false, needsChange: false);
        }
        else
        {
            var matches = string.Equals(expected, verified, StringComparison.CurrentCultureIgnoreCase);
            SetTuningCheckState(verified: matches, needsChange: !matches);
            SelectedTuningCheckLabel = matches
                ? loc.Format("PlayMode_SongSelect_InTuneFmt", expected)
                : loc.Format("PlayMode_SongSelect_TuneToFmt", expected);
        }
    }

    /// <summary>The three tuning-check states, written together so exactly one
    /// pill is ever visible. "Unverified" is the remainder — it is not a fourth
    /// case, it is the absence of the other two.</summary>
    private void SetTuningCheckState(bool verified, bool needsChange)
    {
        SelectedTuningIsVerified = verified;
        SelectedTuningNeedsChange = needsChange;
        SelectedTuningIsUnverified = !verified && !needsChange;
    }

    partial void OnSelectedSongChanged(PlaySongItem? value)
    {
        RefreshSelectedSongDetail();
        _songBrowser?.PrefetchAudio(value?.Inner);
    }
}
