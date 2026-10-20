using System.ComponentModel;
using System.Windows.Input;

namespace DotCharter.Presentation.Contracts.Home;

/// <summary>
///     The app's landing screen: what you were last playing, the places you go
///     from here, and what you were part-way through.
///     <para>
///         It answers "what do I do now" rather than listing features. The hero
///         is the song you last played, because resuming it is the commonest
///         intent by a wide margin; the cards are the only whole-app
///         destinations, each with a LIVE subtitle so the card says what state
///         it is in.
///     </para>
///     <para>
///         <b>This screen is FULL-BLEED</b> — the shell hides its rail, title
///         bar and status rail for it — so everything the user needs from here
///         is on it, including the way into Settings and the command palette.
///         There is no facts strip: the one figure that can be WRONG rather
///         than merely interesting is <see cref="DeviceNeedsAttention" />, and
///         it says so on the device chip itself.
///     </para>
/// </summary>
public interface IHomeScreen : INotifyPropertyChanged
{
    /// <summary>"312 songs in the library, 41 with a chart" — the library in one line.</summary>
    string LibrarySummary { get; }

    /// <summary>"Alex, player 1", shown as a chip beside the device.</summary>
    string ProfileSummary { get; }

    /// <summary>"Focusrite 2i2, 4.2 ms". Empty when no device is open, and the chip goes with it.</summary>
    string DeviceSummary { get; }

    bool HasDevice { get; }

    /// <summary>
    ///     The device chip is the one thing on this screen that can be WRONG
    ///     rather than merely informative (the engine lost its device), so it
    ///     carries its own warning ink. Home has no facts strip to put it in.
    /// </summary>
    bool DeviceNeedsAttention { get; }

    ICommand OpenCommandPaletteCommand { get; }

    /// <summary>The header's Settings chip. Home is full-bleed — there is no rail to reach it from.</summary>
    ICommand OpenSettingsCommand { get; }

    // ── the hero ─────────────────────────────────────────────────────────

    /// <summary>False on a fresh install, where the hero is replaced by an invitation to add music.</summary>
    bool HasRecentSong { get; }

    /// <summary>Cover art for the last played song; empty when there is none.</summary>
    string RecentArtPath { get; }

    bool HasRecentArt { get; }

    /// <summary>The hero image moves (Settings ▸ Graphics). Off, it is drawn once and left still.</summary>
    bool AnimateHero { get; }

    /// <summary>"Last played, yesterday" — already formatted, because "yesterday" is not a date format.</summary>
    string RecentWhen { get; }

    string RecentTitle { get; }

    /// <summary>"Baroness, Yellow &amp; Green, E Standard, 4:05" — artist, album, tuning and length in one line.</summary>
    string RecentMeta { get; }

    /// <summary>One per arrangement: "Lead, best 96 %" / "Bass, not played".</summary>
    IReadOnlyList<IArrangementChip> RecentArrangements { get; }

    /// <summary>The primary action, captioned for the best arrangement ("Play the lead path").</summary>
    string PlayRecentCaption { get; }

    ICommand PlayRecentCommand { get; }

    /// <summary>Goes to the chart editor when it has the hero's song open.</summary>
    ICommand OpenRecentChartCommand { get; }

    /// <summary>The hero's song is the chart the editor has open; the link is absent
    /// otherwise, rather than opening a different song under this one's title.</summary>
    bool CanOpenRecentChart { get; }

    // ── destinations ─────────────────────────────────────────────────────

    /// <summary>Play, Chart, Amp &amp; FX and Library — each with a live subtitle, so the card says what state it is in.</summary>
    IReadOnlyList<IDestinationCard> Destinations { get; }

    // ── jump back in ─────────────────────────────────────────────────────

    bool HasJumpBackIn { get; }

    IReadOnlyList<IProgressRow> JumpBackIn { get; }
}

/// <summary>One arrangement of the hero song and how it has gone so far.</summary>
public interface IArrangementChip
{
    /// <summary>"Lead, best 96 %" or "Bass, not played".</summary>
    string Label { get; }

    /// <summary>The one the primary action would play — drawn lit.</summary>
    bool IsBest { get; }
}

/// <summary>A whole-app destination: what it is, what state it is in, and the way in.</summary>
public interface IDestinationCard
{
    string Title { get; }

    /// <summary>Live, not static copy: "4 charts in progress, 3 with export blockers".</summary>
    string Subtitle { get; }

    /// <summary>Icon resource key, so the view does not map titles to glyphs.</summary>
    string IconKey { get; }

    /// <summary>The primary destination is drawn lit; exactly one is.</summary>
    bool IsPrimary { get; }

    ICommand OpenCommand { get; }
}

/// <summary>A song you are part-way through.</summary>
public interface IProgressRow
{
    string Title { get; }

    string ArtPath { get; }

    bool HasArt { get; }

    /// <summary>"88 %" — already formatted, because the unit is per-culture.</summary>
    string ProgressLabel { get; }

    ICommand OpenCommand { get; }
}
