using System.Collections.ObjectModel;
using BendValue = DotCharter.Core.Domain.Guitar.BendValue;

namespace DotCharter.Services.Parsing.GuitarPro;

/// <summary>
///     Small helpers for bend-value collections shared by the note parser
///     (which seeds bends from GP's Bend/Bended properties) and the
///     post-processor (which merges bends when tying notes together).
/// </summary>
internal static class GpBendUtilities
{
    public static bool ContainsBendAtPosition(IList<BendValue> bends, FractionalPosition position)
    {
        foreach (var bend in bends)
        {
            if (bend.Position.CompareTo(position) == 0)
                return true;
        }

        return false;
    }

    public static void SortUniqueBends(ObservableCollection<BendValue> bends)
    {
        if (bends.Count <= 1) return;

        var uniqueBends = new List<BendValue>(bends.Count);
        foreach (var bend in bends)
        {
            if (!ContainsBendAtPosition(uniqueBends, bend.Position))
                uniqueBends.Add(bend);
        }

        uniqueBends.Sort((left, right) => left.Position.CompareTo(right.Position));
        bends.Clear();
        foreach (var bend in uniqueBends)
            bends.Add(bend);
    }

    public static void AddMissingBends(
        ObservableCollection<BendValue> target,
        ObservableCollection<BendValue> source)
    {
        foreach (var bend in source)
        {
            if (!ContainsBendAtPosition(target, bend.Position))
                target.Add(bend.Clone());
        }

        SortUniqueBends(target);
    }
}
