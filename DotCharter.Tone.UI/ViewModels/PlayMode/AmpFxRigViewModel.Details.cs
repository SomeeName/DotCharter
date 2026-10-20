// AmpFxRigViewModel: the DETAILS panel beside the stage and the gear browser's two-column grid (RigGearTilePair).
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DotCharter.Tone.UI.ViewModels.PlayMode;

// The AmpliTube layout's DETAILS panel and the gear browser's two-column grid.
public sealed partial class AmpFxRigViewModel
{
    /// <summary>
    ///     Whether the gear bar's DETAILS toggle is on: the panel beside the stage
    ///     with everything that is ABOUT the unit rather than ON its face — the
    ///     capture and its library, every exact value, lyrics, the meters, the
    ///     unit's status and the controllers &amp; MIDI.
    ///     <para>
    ///         It is a STANDING choice, not a per-unit one: stepping through the
    ///         chain with the gear bar's ▲ / ▼ keeps it open, because someone
    ///         reading exact values wants to read them for the next unit too. The
    ///         one thing that opens it for you is a unit whose face does not carry
    ///         every param (<see cref="RigDockUnit.FaceMissesParams" />): on the stage
    ///         alone its controls would be unreachable.
    ///     </para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDetailsShown))]
    private bool _isDetailsOpen;

    /// <summary>The panel is drawn: the toggle is on AND there is a unit to describe.</summary>
    public bool IsDetailsShown => IsDetailsOpen && HasDockUnit;

    [RelayCommand]
    private void ToggleDetails() => IsDetailsOpen = !IsDetailsOpen;

    partial void OnIsDetailsOpenChanged(bool value)
    {
        if (_dockUnit is { } unit) unit.ShowsDetails = value;
    }

    /// <summary>Opens the panel for a unit the stage cannot fully edit.</summary>
    private void RevealDetailsForFacelessParams()
    {
        if (DockUnit is { FaceMissesParams: true }) IsDetailsOpen = true;
    }

    /// <summary>
    ///     The gear browser's rows: <see cref="GearRows" /> two to a row, so the
    ///     grid of thumbnails stays ONE virtualized list (a wrapping panel cannot
    ///     virtualize, and a hundred faceplate tiles realized at once is the cost
    ///     <c>PlayModeScreensUiPerfSmokeTests</c> pins). Replaced wholesale with
    ///     <see cref="GearRows" />; the tiles are the same <see cref="RigGearItem" />
    ///     instances, so selection, favourites and the IN RIG badge are shared.
    /// </summary>
    [ObservableProperty] private IReadOnlyList<RigGearTilePair> _gearTilePairs = [];

    /// <summary>Pairs <paramref name="rows" /> in order: [0,1], [2,3], … — the last
    /// row of an odd count carries its left tile only.</summary>
    internal static IReadOnlyList<RigGearTilePair> PairTiles(IReadOnlyList<RigGearItem> rows)
    {
        var pairs = new List<RigGearTilePair>((rows.Count + 1) / 2);
        for (var i = 0; i < rows.Count; i += 2)
            pairs.Add(new RigGearTilePair(rows[i], i + 1 < rows.Count ? rows[i + 1] : null));
        return pairs;
    }
}

/// <summary>One row of the gear browser's two-column grid.</summary>
public sealed class RigGearTilePair(RigGearItem left, RigGearItem? right)
{
    public RigGearItem Left { get; } = left;
    public RigGearItem? Right { get; } = right;
    public bool HasRight => Right is not null;
}
