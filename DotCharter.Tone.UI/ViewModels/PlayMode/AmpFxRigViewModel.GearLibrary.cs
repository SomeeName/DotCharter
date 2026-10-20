// AmpFxRigViewModel: the gear library's Browse/Favourites tabs and WHERE an added unit lands when no insert gap is armed (the serial run, or a lane of the live split region).

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DotCharter.Tone.UI.ViewModels.PlayMode;

/// <summary>Which half of the gear library is showing.</summary>
public enum RigGearLibraryTabKind
{
    /// <summary>The whole catalogue — the default, and what the panel has always
    /// shown.</summary>
    Browse,

    /// <summary>Only the units the player starred
    /// (<c>AppConfig.RigFavoriteGear</c>).</summary>
    Favorites
}

/// <summary>
///     One tab of the gear library. It carries its OWN command, like
///     <c>RigGearItem</c> and <c>GearModelOption</c> do: the tab strip is an
///     ItemsControl and a row that has to cast back through the host's DataContext
///     to find a command is the binding this screen keeps getting wrong.
/// </summary>
public sealed partial class RigGearLibraryTab : ObservableObject
{
    public RigGearLibraryTab(RigGearLibraryTabKind kind, string label,
        System.Windows.Input.ICommand command)
    {
        Kind = kind;
        Label = label;
        Command = command;
    }

    public RigGearLibraryTabKind Kind { get; }
    public string Label { get; }
    public System.Windows.Input.ICommand Command { get; }

    /// <summary>The lit state. Driven by the screen (the tabs are toggles over one
    /// enum, not a ListBox selection).</summary>
    [ObservableProperty] private bool _isActive;
}

/// <summary>
///     One option in "where does an added unit go": the serial run (the end of the
///     chain, <see cref="Lane" /> null) or a lane of the live split region.
///     Carries its own select command for the tab reason above.
/// </summary>
public sealed partial class RigAddTargetLane : ObservableObject
{
    public RigAddTargetLane(int? lane, string label, System.Windows.Input.ICommand command)
    {
        Lane = lane;
        Label = label;
        Command = command;
    }

    /// <summary>Null = the serial run / the end of the chain; 1..3 = a branch of
    /// the region (the same 1-based numbering <c>RigBoardLane</c> prints).</summary>
    public int? Lane { get; }

    public string Label { get; }
    public System.Windows.Input.ICommand Command { get; }

    [ObservableProperty] private bool _isActive;
}

public sealed partial class AmpFxRigViewModel
{
    // ── the gear library's two tabs ───────────────────────────────────────────

    /// <summary>Browse and Favourites, in that order. Built in the constructor
    /// alongside the category chips, because both are filter surfaces over one
    /// catalogue.</summary>
    public IReadOnlyList<RigGearLibraryTab> GearLibraryTabs { get; }

    /// <summary>
    ///     Which half of the library is showing. The tab is the SCREEN's state and
    ///     the filter is the RAIL's (<c>GearRailViewModel.ShowFavoritesOnly</c>,
    ///     which is what actually rebuilds the cards) — one enum here drives that
    ///     one boolean there, so the two can never disagree about what the list is
    ///     showing.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGearLibraryBrowse))]
    [NotifyPropertyChangedFor(nameof(IsGearLibraryFavorites))]
    private RigGearLibraryTabKind _gearLibraryTab = RigGearLibraryTabKind.Browse;

    public bool IsGearLibraryBrowse => GearLibraryTab == RigGearLibraryTabKind.Browse;
    public bool IsGearLibraryFavorites => GearLibraryTab == RigGearLibraryTabKind.Favorites;

    /// <summary>Whether the player has starred anything at all — what the
    /// Favourites tab's empty note binds to ("star a unit to keep it here"), so
    /// the tab never reads as an empty catalogue.</summary>
    public bool HasFavoriteGear => _rig.GearRail.HasFavorites;

    [RelayCommand]
    private void ShowBrowse() => GearLibraryTab = RigGearLibraryTabKind.Browse;

    [RelayCommand]
    private void ShowFavorites() => GearLibraryTab = RigGearLibraryTabKind.Favorites;

    partial void OnGearLibraryTabChanged(RigGearLibraryTabKind value)
    {
        RefreshGearLibraryTabs();
        // The rail owns the filter; its Cards change comes back through the
        // collection subscription. Rebuilt here as well because a tab press with
        // no card change at all (an empty catalogue) must still settle the groups.
        _rig.GearRail.ShowFavoritesOnly = value == RigGearLibraryTabKind.Favorites;
        RebuildGearGroups();
    }

    private void RefreshGearLibraryTabs()
    {
        foreach (var tab in GearLibraryTabs) tab.IsActive = tab.Kind == GearLibraryTab;
        OnPropertyChanged(nameof(HasFavoriteGear));
    }

    // ── where an added unit lands with no gap armed ───────────────────────────

    /// <summary>
    ///     The lane a gear-panel add drops into when NO insert gap is armed: null
    ///     for the serial run (the end of the chain — what the panel has always
    ///     done), 1..3 for a lane of the live split region.
    ///     <para>
    ///         It exists because the panel's add was the one add that could not
    ///         reach a lane: the armed gap carries its branch
    ///         (<see cref="InsertBranch" />) and a drag carries the row it was
    ///         dropped on, but a plain click appended, which lands OUTSIDE the
    ///         region — so filling lane B meant adding a unit and then dragging it,
    ///         every time.
    ///     </para>
    ///     <para>
    ///         An armed gap always WINS: it is the more specific gesture, and the
    ///         player just clicked it.
    ///     </para>
    /// </summary>
    [ObservableProperty] private int? _addTargetLane;

    partial void OnAddTargetLaneChanged(int? value) => RefreshAddTargetSelection();

    /// <summary>The choices for <see cref="AddTargetLane" />: the serial run, plus
    /// ONE PER LANE THE BOARD ACTUALLY RUNS. A lane that does not exist is never
    /// offered — the same rule that keeps <c>SplitLanes</c> from showing a lane the
    /// engine is not running.</summary>
    public ObservableCollection<RigAddTargetLane> AddTargetLanes { get; } = [];

    /// <summary>False on a serial chain: with no region there is only one place a
    /// unit can go, and a picker over one option is a control over nothing.</summary>
    public bool HasAddTargetChoice => AddTargetLanes.Count > 1;

    /// <summary>
    ///     Re-derives <see cref="AddTargetLanes" /> from the folded board — called
    ///     at the end of every refold, so opening, widening, narrowing or closing
    ///     the region moves the options with it. A target lane that stops existing
    ///     falls back to the serial run rather than silently addressing lane 1.
    /// </summary>
    private void RefreshAddTargetLanes()
    {
        var lanes = BoardSegments.OfType<RigSplitSegment>()
            .SelectMany(s => s.Lanes)
            .Select(l => l.Branch)
            .Distinct()
            .OrderBy(b => b)
            .ToList();

        if (AddTargetLane is { } current && !lanes.Contains(current)) AddTargetLane = null;

        // Reconciled by index like every other collection this screen owns: the
        // options are bound by a non-virtualized strip, and a clear+refill
        // re-inflates it on every board gesture.
        var wanted = new List<int?> { null };
        wanted.AddRange(lanes.Select(b => (int?)b));

        while (AddTargetLanes.Count > wanted.Count) AddTargetLanes.RemoveAt(AddTargetLanes.Count - 1);
        for (var i = 0; i < wanted.Count; i++)
        {
            if (i < AddTargetLanes.Count && AddTargetLanes[i].Lane == wanted[i]) continue;
            var option = NewAddTargetOption(wanted[i]);
            if (i < AddTargetLanes.Count) AddTargetLanes[i] = option;
            else AddTargetLanes.Add(option);
        }

        RefreshAddTargetSelection();
        OnPropertyChanged(nameof(HasAddTargetChoice));
    }

    private RigAddTargetLane NewAddTargetOption(int? lane) => new(
        lane,
        lane is { } branch
            ? _loc.Format("Rig_AddTargetLane", LaneLabel(branch))
            : _loc["Rig_AddTargetSerial"],
        new RelayCommand(() => AddTargetLane = lane));

    private void RefreshAddTargetSelection()
    {
        foreach (var option in AddTargetLanes) option.IsActive = option.Lane == AddTargetLane;
        OnPropertyChanged(nameof(AddTargetOption));
    }

    /// <summary>The picker's face for <see cref="AddTargetLane" />: the option
    /// whose lane is the current target (the serial run when nothing is picked).
    /// Writing it sets the lane, so the dropdown and the option commands agree.</summary>
    public RigAddTargetLane? AddTargetOption
    {
        get => AddTargetLanes.FirstOrDefault(o => o.Lane == AddTargetLane) ?? AddTargetLanes.FirstOrDefault();
        set { if (value is not null) AddTargetLane = value.Lane; }
    }

    /// <summary>The lane's letter, the same fold <c>RigBoardLane.Label</c> prints —
    /// a lane is "B" to a player, never "2".</summary>
    private static string LaneLabel(int branch) => branch switch
    {
        1 => "A",
        2 => "B",
        3 => "C",
        _ => branch.ToString(System.Globalization.CultureInfo.InvariantCulture)
    };

    /// <summary>
    ///     The chain slot an add targeted at <paramref name="branch" /> should land
    ///     in: right after that lane's last unit, or — for an EMPTY lane — the
    ///     region body's first slot. Exactly what <see cref="InsertIntoLane" />
    ///     computes for a lane gap, because it is the same question; -1 when the
    ///     lane is not on the board.
    /// </summary>
    private int LaneAddIndex(int branch)
    {
        var lane = BoardSegments.OfType<RigSplitSegment>()
            .SelectMany(s => s.Lanes)
            .FirstOrDefault(l => l.Branch == branch);
        if (lane is null) return -1;
        return lane.Items.Count > 0 ? Chain.IndexOf(lane.Items[^1]) + 1 : BodyStartOf(lane);
    }
}
