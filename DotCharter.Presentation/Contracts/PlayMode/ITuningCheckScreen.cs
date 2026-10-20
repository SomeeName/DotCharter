using System.ComponentModel;
using System.Windows.Input;

namespace DotCharter.Presentation.Contracts.PlayMode;

/// <summary>
///     The redesigned Get Ready / tuning-check screen — the densest screen in
///     the app and the one place a run's settings are decided.
///     <para>
///         Three things and no more: the PATH you take, the TUNING the chart
///         expects with a live per-string check, and HOW IT PLAYS (speed,
///         scroll speed, hit window, the backing track and the tone). The
///         current screen is already a full-bleed four-region layout because of
///         that density and it measures 100 % of the window at every resolution
///         — the redesign keeps the four regions and only re-groups them.
///     </para>
/// </summary>
public interface ITuningCheckScreen : INotifyPropertyChanged
{
    string SongTitle { get; }

    string Artist { get; }

    bool HasArtist { get; }

    /// <summary>The resolved tuning NAME, with the capo folded in.</summary>
    string TuningLabel { get; }

    /// <summary>The raw note letters — the tooltip, never the cell.</summary>
    string TuningNotes { get; }

    /// <summary>True when this profile has played the selected path.</summary>
    bool HasBest { get; }

    /// <summary>"BEST A · 94.1 %", already formatted.</summary>
    string BestLabel { get; }

    /// <summary>The arrangements as pickable tiles.</summary>
    IReadOnlyList<IPathTile> Paths { get; }

    /// <summary>The tile the run would take. Always republishes when picked.</summary>
    IPathTile? SelectedPath { get; set; }

    /// <summary>
    ///     False when there is nothing to check — no path picked, or no input.
    ///     The tuner panel then says so instead of drawing six dead rows.
    /// </summary>
    bool HasTunerCheck { get; }

    /// <summary>True once every string has been heard in tune.</summary>
    bool AllVerified { get; }

    /// <summary>"4 OF 6 VERIFIED".</summary>
    string VerifiedLabel { get; }

    /// <summary>One row per string of the expected tuning.</summary>
    IReadOnlyList<ITunerString> Strings { get; }

    /// <summary>Starts the check again from the lowest string.</summary>
    ICommand RestartTunerCommand { get; }

    /// <summary>Accepts the tuning unverified — and says what that costs.</summary>
    ICommand SkipTunerCommand { get; }

    /// <summary>The input device, already formatted.</summary>
    string InputLabel { get; }

    /// <summary>False when no input is open — the check cannot run.</summary>
    bool IsInputOpen { get; }

    /// <summary>The live input level, 0..1.</summary>
    double InputLevel { get; }

    /// <summary>The same level as a dB reading.</summary>
    string InputLevelLabel { get; }

    /// <summary>"100 %" — the practice speed for this run.</summary>
    string SpeedLabel { get; }

    /// <summary>"2.4 S ON SCREEN".</summary>
    string ScrollSpeedLabel { get; }

    /// <summary>"Normal" / "Strict" / "Lenient".</summary>
    string HitWindowLabel { get; }

    /// <summary>What the backing track will do, already formatted.</summary>
    string BackingLabel { get; }

    /// <summary>Which tone the run uses, already formatted.</summary>
    string ToneLabel { get; }

    /// <summary>Whether the player's own instrument is muted out of the mix.</summary>
    bool MuteMyInstrument { get; set; }

    /// <summary>Whether the song's own in-game tones are used.</summary>
    bool UseInGameTones { get; set; }

    ICommand StartCommand { get; }

    /// <summary>Whether <see cref="StartCommand" /> would do anything.</summary>
    bool CanStart { get; }

    ICommand BackCommand { get; }

    /// <summary>Keeps this run's settings as the defaults for the next one.</summary>
    ICommand SaveAsDefaultCommand { get; }

    /// <summary>Into the Amp &amp; FX rig without leaving the screen.</summary>
    ICommand OpenRigCommand { get; }

    /// <summary>The running setlist: where it stands, and the songs skipped on the way here.</summary>
    IGetReadySetlist Setlist { get; }
}

/// <summary>One arrangement, as a path tile.</summary>
public interface IPathTile : INotifyPropertyChanged
{
    string Name { get; }

    /// <summary>"LEAD" — the type as a caps mark.</summary>
    string TypeLabel { get; }

    /// <summary>"612 notes", already formatted.</summary>
    string NoteCountLabel { get; }

    /// <summary>"A · 94.1 %", or empty when unplayed.</summary>
    string RecordLabel { get; }

    bool HasRecord { get; }

    bool IsSelected { get; }

    /// <summary>Picks this path. Always republishes, even when already picked.</summary>
    ICommand SelectCommand { get; }
}

/// <summary>One string of the expected tuning, as the guided check sees it.</summary>
public interface ITunerString : INotifyPropertyChanged
{
    /// <summary>"E2", "A2" — the note this string must reach.</summary>
    string Name { get; }

    /// <summary>"-14", already formatted. Empty while awaiting a pluck.</summary>
    string CentsLabel { get; }

    /// <summary>"FLAT" / "SHARP" / "IN TUNE" / "PLUCK" — one word.</summary>
    string StateText { get; }

    /// <summary>How far off, 0..100, as the needle's position along its track.</summary>
    double NeedlePercent { get; }

    bool IsVerified { get; }

    /// <summary>True while this is the string the check is listening for.</summary>
    bool IsAwaiting { get; }

    /// <summary>True when the reading is far enough off to be a problem.</summary>
    bool IsOff { get; }
}
