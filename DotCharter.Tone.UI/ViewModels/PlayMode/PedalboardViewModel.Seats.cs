// Which PLAYER's rig this one surface is editing (per-seat rigs, ABI v28+).

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DotCharter.Tone.UI.ViewModels.PlayMode;

/// <summary>One entry in the rig's seat picker: the engine seat and what to call
/// it. The label is whatever the publisher supplied.</summary>
public sealed record RigSeatOption(int Player, string Label)
{
    public override string ToString() => Label;
}

public partial class PedalboardViewModel
{
    /// <summary>
    ///     The seats this rig can be pointed at.
    ///
    ///     <para>
    ///         Exactly ONE by default, which is the whole single-player story: the
    ///         picker is hidden, <see cref="SelectedSeat" /> is seat 0, and nothing
    ///         about the rig changed. A publisher replaces the list through
    ///         <see cref="SetSeats" />; no product code publishes more than one seat
    ///         (there is no couch co-op).
    ///     </para>
    /// </summary>
    public ObservableCollection<RigSeatOption> Seats { get; } =
        [new RigSeatOption(0, "Player 1")];

    /// <summary>
    ///     Whether to offer the picker at all. One seat is not a choice, and a
    ///     dropdown over it is a live-looking control over nothing — the same rule
    ///     that keeps a capability the app cannot honour off the screen.
    /// </summary>
    public bool HasMultipleSeats => Seats.Count > 1;

    [ObservableProperty] private RigSeatOption? _selectedSeat;

    /// <summary>The seat every edit on this surface lands on. Seat 0 whenever
    /// nothing has been selected, so no path can silently address a seat the
    /// roster does not have.</summary>
    public int SelectedPlayer => SelectedSeat?.Player ?? 0;

    /// <summary>
    ///     Point the rig at another seat.
    ///
    ///     <para>
    ///         Repointing <c>_dsp</c> and re-syncing is the WHOLE switch: every
    ///         <c>PedalNodeViewModel</c> is handed the chain by
    ///         <c>CreateNodeVm</c>, so a rebuild re-binds all of them, and the
    ///         board columns / neural-amp status are re-projected from the new
    ///         chain's own nodes. Nothing is copied between seats — each one keeps
    ///         the rig it already had, which is the point of per-player rigs.
    ///     </para>
    ///     <para>
    ///         This is the one path that must use <c>ResyncAll</c> (the full
    ///         teardown), never the incremental <c>Sync</c>: the diff reuses a
    ///         card by node id, node ids are only unique per chain, and a reused
    ///         card would go on editing the seat it was BUILT against.
    ///     </para>
    /// </summary>
    partial void OnSelectedSeatChanged(RigSeatOption? value)
    {
        if (value is null) return;

        // The TUNER moves with the board, always. It reads that seat's own capture ring (ABI v28),
        // and never the GLOBAL tap for every seat: switching the
        // rig to player 2 would leave the needle on player 1's guitar, so "tune each
        // guitar" would tune one guitar twice. Set BEFORE the chain repoint so a
        // `Sync()` that raises anything cannot observe a half-moved surface.
        RigTuner.Player = value.Player;
        OnPropertyChanged(nameof(SelectedPlayer));

        // The engine-rebuild subscription follows the surface onto the new seat:
        // every seat's chain raises EngineAttached for itself, and a handler left
        // on the chain we are leaving would rebuild this board for somebody
        // else's engine.
        _dsp.EngineAttached -= _onEngineAttached;
        _dsp = _chains.For(value.Player);
        _dsp.EngineAttached += _onEngineAttached;
        ResyncAll();
    }

    /// <summary>
    ///     Replace the seat list (slot → display name), or
    ///     collapse back to the single-player seat with an empty/one-entry list.
    ///
    ///     <para>
    ///         Pushed IN rather than pulled: this view model is a DI singleton
    ///         shared by the chart editor and Play Mode, and giving it a session
    ///         dependency would make the rig unconstructable in every context that
    ///         has no session.
    ///     </para>
    ///     <para>
    ///         Seats the ENGINE cannot host are dropped rather than offered:
    ///         <c>IPlayerDspChains.For</c> clamps out-of-range indices into range,
    ///         so a seat past the ceiling would silently edit somebody else's rig.
    ///     </para>
    /// </summary>
    public void SetSeats(IReadOnlyList<RigSeatOption>? seats)
    {
        var ceiling = _chains.MaxPlayers;
        var wanted = (seats ?? [])
            .Where(s => s.Player >= 0 && s.Player < ceiling)
            .OrderBy(s => s.Player)
            .ToList();

        if (wanted.Count == 0) wanted.Add(new RigSeatOption(0, "Player 1"));

        // Keep the current seat selected across a roster edit when it survives —
        // a name change must not yank the rig out from under whoever is dialling
        // it in.
        var keep = SelectedPlayer;

        Seats.Clear();
        foreach (var seat in wanted) Seats.Add(seat);
        OnPropertyChanged(nameof(HasMultipleSeats));

        var target = Seats.FirstOrDefault(s => s.Player == keep) ?? Seats[0];
        if (!ReferenceEquals(SelectedSeat, target)) SelectedSeat = target;
    }
}
