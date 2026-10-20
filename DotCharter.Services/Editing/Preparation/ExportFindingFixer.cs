using DotCharter.Services.Editing.Fixing;
using DotCharter.Services.Editing.Structure;
using DotCharter.Services.Export.Validation;

namespace DotCharter.Services.Editing.Preparation;

/// <summary>
/// The Fix beside an export finding: the chart verb that repairs it, run IN PLACE on a project
///     the caller owns — <c>ChartMaintenanceService</c> hands it a snapshot clone and records the
///     before/after pair as ONE undo node. Every fix is an existing verb; this class only picks
///     it and keeps it off the charter's hand work (a level's fret-hand positions are only ever
///     filled in before its first anchor, never moved).
/// </summary>
public sealed class ExportFindingFixer(IArrangementAutoFixService autoFix, IFretAnchorGenerationService anchors)
{
    /// <summary>Runs the chart verb behind <paramref name="kind" /> on one arrangement; the number of
    /// changes it made. <see cref="ExportFixKind.OpenToneDesigner" /> is not a chart edit and does nothing here.</summary>
    public int Fix(ExportFixKind kind, ChartProject project, Arrangement arrangement)
    {
        switch (kind)
        {
            case ExportFixKind.AutoFix:
            {
                var result = new ArrangementFixResult();
                autoFix.Fix(arrangement, result);
                return result.TotalChanges;
            }
            case ExportFixKind.FillFhp:
                return arrangement.Levels.Sum(level => FillBeforeFirstAnchor(arrangement, level));
            case ExportFixKind.GenerateStructure:
                return ApplyStructure(arrangement, project.Beats);
            default:
                return 0;
        }
    }

    private static int ApplyStructure(Arrangement arrangement, IList<BeatMarker> beats)
    {
        if (beats.Count == 0) return 0;
        var action = StructureGenerationService.BuildAction(arrangement, beats, out var result, out _, out _);
        if (action == null) return 0;
        action.Execute();
        return result.Total;
    }

    private IReadOnlyList<FretAnchor> Generate(Arrangement arrangement, ChartLevel level,
        FractionalPosition? start = null, FractionalPosition? end = null) =>
        anchors.GenerateAnchors(level, start, end, arrangement.ChordTemplates, arrangement.Capo,
            ArrangementInstrument.IsBassPart(arrangement), tuning: arrangement);

    private int GenerateAll(Arrangement arrangement, ChartLevel level)
    {
        var generated = Generate(arrangement, level);
        if (generated.Count == 0) return 0;
        level.Anchors.Clear();
        foreach (var anchor in generated.OrderBy(a => a.Position)) level.Anchors.Add(anchor);
        return generated.Count;
    }

    /// <summary>
    ///     Covers the sounds before a level's first anchor and touches nothing else: no anchor is
    ///     moved or removed. A level with no anchors at all is generated whole.
    /// </summary>
    private int FillBeforeFirstAnchor(Arrangement arrangement, ChartLevel level)
    {
        if (level.Anchors.Count == 0) return GenerateAll(arrangement, level);

        var firstAnchor = level.Anchors.Min(a => a.Position);
        var uncovered = level.Notes.Select(n => n.Position)
            .Concat(level.Chords.Select(c => c.Position))
            .Where(p => p < firstAnchor)
            .ToList();
        if (uncovered.Count == 0) return 0;

        var start = uncovered.Min();
        var added = Generate(arrangement, level, start, uncovered.Max())
            .Where(a => a.Position < firstAnchor)
            .OrderBy(a => a.Position)
            .ToList();

        // The walk may open after the first sound; the hand still has to be somewhere there.
        if (added.Count == 0 || added[0].Position > start)
        {
            var model = added.Count > 0 ? added[0] : level.Anchors.OrderBy(a => a.Position).First();
            added.Insert(0, new FretAnchor(start, model.Fret, model.Width));
        }

        foreach (var anchor in added) InsertSorted(level.Anchors, anchor);
        return added.Count;
    }

    private static void InsertSorted(IList<FretAnchor> list, FretAnchor anchor)
    {
        var index = 0;
        while (index < list.Count && list[index].Position <= anchor.Position) index++;
        list.Insert(index, anchor);
    }
}
