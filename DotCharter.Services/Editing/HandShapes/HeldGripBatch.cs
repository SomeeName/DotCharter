using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.State;

namespace DotCharter.Services.Editing.HandShapes;

internal sealed class HeldGripBatch(Arrangement arrangement, ArrangementFingeringStyle? songStyle)
{
    private readonly List<ChordTemplate> _minted = [];
    private readonly List<ChordTemplate> _resolved = [];

    public Dictionary<int, int> Held { get; } = [];

    public bool Bind(HandShape shape, Dictionary<int, int> played, int openingStrings)
    {
        if (Held.Count == 0 && played.Count == 0) return false;

        var frets = new Dictionary<int, int>(Held);
        foreach (var (guitarString, fret) in played) frets[guitarString] = fret;
        var grip = ResolveGrip(frets);
        var mask = 0;
        foreach (var guitarString in frets.Keys) mask |= ChordTemplate.GhostBit(guitarString);
        mask &= ~openingStrings;

        // An explicit zero tells the inspector to show the bar's grip even over a partial strum.
        if (shape.TemplateId == grip.Id && shape.GhostMask == mask) return false;

        shape.TemplateId = grip.Id;
        shape.GhostMask = mask;
        return true;
    }

    public bool Commit(IHistoryService history, IChartAction shapes, string labelKey)
    {
        IChartAction[] actions = [new AddChordTemplatesAction(arrangement.ChordTemplates, _minted), shapes];
        return history.Do(new CompositeAction(actions) { LabelKey = labelKey });
    }

    private ChordTemplate ResolveGrip(Dictionary<int, int> frets)
    {
        var match = _resolved.FirstOrDefault(t => MapEquality.SameEntries(t.Frets, frets));
        if (match != null) return match;

        match = arrangement.ChordTemplates.FirstOrDefault(t => t.Id >= 0 &&
            MapEquality.SameEntries(t.Frets, frets) &&
            (t.Fingers.Count > 0 || frets.Values.All(f => f <= arrangement.Capo)));
        if (match == null)
        {
            var seed = ChordTemplateResolver.SuggestGrip(arrangement, frets, songStyle: songStyle);
            match = ChordTemplateResolver.ResolveAuthored(arrangement, seed, _minted);
        }

        _resolved.Add(match);
        return match;
    }
}
