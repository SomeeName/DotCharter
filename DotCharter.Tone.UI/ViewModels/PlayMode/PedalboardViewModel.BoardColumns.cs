// The board's column view-models: a single pedal or junction column, a split region's fanned branch rows, and one branch row with its lane mute.

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DotCharter.Tone.UI.ViewModels.PlayMode;

/// <summary>
/// Base for one column of the row-grouped board (ABI v23): either a single pedal
/// / junction (<see cref="PedalColumnViewModel"/>) or the fanned split region
/// body (<see cref="RegionColumnViewModel"/>). The board ItemsControl binds
/// <see cref="PedalboardViewModel.BoardColumns"/> and picks a template by type.
/// </summary>
public abstract class BoardColumnViewModel
{
}

/// <summary>A single-pedal column: a linear pedal, OR a full-height
/// splitter/merger junction (<see cref="IsJunction"/>) spanning a region's rows.</summary>
public sealed class PedalColumnViewModel : BoardColumnViewModel
{
    public PedalColumnViewModel(PedalNodeViewModel pedal, bool isJunction)
    {
        Pedal = pedal;
        IsJunction = isJunction;
    }

    public PedalNodeViewModel Pedal { get; }

    /// <summary>True when this pedal is the region-opening splitter or the
    /// region-closing merger — rendered as a tall junction beside the fan.</summary>
    public bool IsJunction { get; }

    public bool IsPedal => !IsJunction;
}

/// <summary>The fanned parallel body of a split region: N stacked branch rows.</summary>
public sealed class RegionColumnViewModel : BoardColumnViewModel
{
    public RegionColumnViewModel(IReadOnlyList<RegionRowViewModel> rows, bool autoMergeAtEnd)
    {
        Rows = rows;
        AutoMergeAtEnd = autoMergeAtEnd;
    }

    /// <summary>The branch rows, top (branch 1) to bottom.</summary>
    public IReadOnlyList<RegionRowViewModel> Rows { get; }

    /// <summary>True when the region has no closing merger — the engine auto-
    /// averages the branches at the chain end (shown as a subtle indicator chip).</summary>
    public bool AutoMergeAtEnd { get; }
}

/// <summary>One branch row inside a split region: the pedals tagged to this
/// branch, in signal order. An empty row still renders (a drop target).</summary>
public sealed partial class RegionRowViewModel : ObservableObject
{
    public RegionRowViewModel(int branch, IEnumerable<PedalNodeViewModel> pedals)
    {
        Branch = branch;
        Pedals = new ObservableCollection<PedalNodeViewModel>(pedals);
    }

    /// <summary>1-based branch index this row represents (= native branch tag).</summary>
    public int Branch { get; }

    public ObservableCollection<PedalNodeViewModel> Pedals { get; }

    public bool IsEmpty => Pedals.Count == 0;

    /// <summary>The lane letter the mixer strip prints (branch 1 = A). Mirrors
    /// the numbered branch markers the chain builder already draws.</summary>
    public string LaneLabel => Branch switch { 1 => "A", 2 => "B", 3 => "C", _ => Branch.ToString() };

    // This legacy projection has no closing merger. Bypassing the pedals passes
    // dry audio and is NOT lane mute. The live RigBoardLane.Mixer owns the real
    // native level/pan/mute/solo surface; this old projection stays unavailable.
    public bool IsMuted => false;
    public bool CanMute => false;

    [RelayCommand(CanExecute = nameof(CanMute))]
    private void ToggleMute() { }
}
