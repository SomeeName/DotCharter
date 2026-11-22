using System.Collections.ObjectModel;
using DotCharter.Services.State;

namespace DotCharter.Services.Editing;

/// <summary>
///     The Song tab's Refresh as one pass over every arrangement: section and phrase markers and tone
///     changes are put in time order and EXACT duplicates go (the same place with the same name, phrase,
///     events and status; for a tone change, the same place and tone). <see cref="Plan" /> writes nothing
///     and returns null when every list is already tidy; otherwise one undo node rewrites them all.
/// </summary>
public static class SongMarkerTidy
{
    /// <summary>The one action that tidies every arrangement, or null when there is nothing to do.</summary>
    public static IChartAction? Plan(ChartProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var parts = new List<IChartAction>();
        foreach (var arrangement in project.Arrangements)
        {
            AddIfChanged(parts, arrangement.EventPoints, Tidy(arrangement.EventPoints, e => e.Position, SameEvent));
            AddIfChanged(parts, arrangement.ToneChanges, Tidy(arrangement.ToneChanges, t => t.Position, SameToneChange));
        }

        return parts.Count == 0 ? null : new CompositeAction(parts) { LabelKey = "History_TidyMarkers" };
    }

    private static bool SameEvent(EventPoint a, EventPoint b) =>
        a.Position == b.Position && a.Phrase == b.Phrase && a.Section == b.Section && a.Events == b.Events &&
        a.Status == b.Status;

    private static bool SameToneChange(ToneChange a, ToneChange b) => a.Position == b.Position && a.ToneName == b.ToneName;

    /// <summary>A stable time sort, keeping the first of each exact duplicate.</summary>
    private static List<T> Tidy<T>(IEnumerable<T> items, Func<T, FractionalPosition> position, Func<T, T, bool> same)
    {
        var kept = new List<T>();
        foreach (var item in items.OrderBy(position))
        {
            if (!kept.Any(k => same(k, item))) kept.Add(item);
        }

        return kept;
    }

    private static void AddIfChanged<T>(List<IChartAction> parts, ObservableCollection<T> list, List<T> tidy)
    {
        if (!list.SequenceEqual(tidy)) parts.Add(new ReplaceListContentAction<T>(list, tidy));
    }

    /// <summary>Rewrites one list's content; undo puts the old order and items back.</summary>
    private sealed class ReplaceListContentAction<T>(ObservableCollection<T> list, List<T> after) : IChartAction, ILinkPolicyAction
    {
        private readonly List<T> _before = [.. list];

        /// <summary>Markers, not content in beats.</summary>
        public LinkPolicy LinkPolicy => LinkPolicy.Retain;

        public void Execute() => Fill(after);

        public void Undo() => Fill(_before);

        private void Fill(List<T> items)
        {
            list.Clear();
            foreach (var item in items) list.Add(item);
        }
    }
}
