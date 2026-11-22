namespace DotCharter.UI.ViewModels.Editor.Inspector;

internal sealed class GripInspectorDraft
{
    internal Dictionary<int, int> ChordFrets { get; } = [];
    internal Dictionary<int, int?> GripFrets { get; } = [];
    internal Dictionary<int, int> Fingers { get; } = [];
    internal Dictionary<int, bool> Ghosts { get; } = [];
    internal ChordTemplate? Template { get; set; }
    internal bool MintTemplate { get; set; }
    internal string? Name { get; set; }
    internal bool? Arpeggio { get; set; }
    internal bool? DoubleStop { get; set; }
    internal bool? NoName { get; set; }
    internal bool? Fingerless { get; set; }
    internal bool? Pin { get; set; }

    internal bool HasGripEdits => Template != null || MintTemplate || Name != null || Arpeggio.HasValue ||
        DoubleStop.HasValue || NoName.HasValue || Fingerless.HasValue || Pin.HasValue ||
        GripFrets.Count + Fingers.Count + Ghosts.Count > 0;

    internal bool AppliesTo(GripShapeFields fields) => fields switch
    {
        GripShapeFields.All => HasGripEdits,
        GripShapeFields.NameAndFingers => Name != null || NoName.HasValue || Fingers.Count > 0,
        _ => Fingers.Count > 0
    };

    internal ChordTemplate Project(HandShape shape, ChordTemplate original, Func<HandShape, ChordTemplate, int> inferGhosts,
        GripShapeFields fields = GripShapeFields.All)
    {
        if (fields != GripShapeFields.All)
        {
            var matched = original.Clone();
            matched.GhostMask = GripEdit.Ghosts(shape, original, inferGhosts);
            if (fields == GripShapeFields.NameAndFingers) ApplyName(matched);
            ApplyFingers(matched);
            return matched;
        }

        var grip = (Template ?? original).Clone();
        foreach (var (index, fret) in GripFrets)
        {
            if (fret is { } value)
            {
                grip.Frets[index] = value;
            }
            else
            {
                grip.Frets.Remove(index);
                grip.Fingers.Remove(index);
            }
        }

        var opening = GripFrets.Count == 0 && Template == null
            ? GripEdit.Ghosts(shape, original, inferGhosts)
            : GhostGemsForEdit(shape, original, grip, inferGhosts);
        grip.GhostMask = 0;
        foreach (var index in grip.Frets.Keys)
        {
            var bit = ChordTemplate.GhostBit(index);
            if (Ghosts.GetValueOrDefault(index, (opening & bit) != 0)) grip.GhostMask |= bit;
        }

        if (Arpeggio is { } arpeggio && grip.IsArpeggio != arpeggio)
        {
            grip.Arpeggio = arpeggio;
            grip.ForceArpeggioInRs = false;
        }
        grip.Fingerless = Fingerless ?? grip.Fingerless;
        grip.NopInRs = DoubleStop ?? grip.NopInRs;
        grip.Pinned = Pin ?? grip.Pinned;
        ApplyName(grip);
        ApplyFingers(grip);
        if (GripFrets.Count > 0 || Fingerless.HasValue) grip.FollowedFingering = false;
        return grip;
    }

    private void ApplyName(ChordTemplate grip)
    {
        grip.Unnamed = NoName ?? grip.Unnamed;
        if (Name != null) grip.Name = Name;
        if (grip.Unnamed) grip.Name = "";
    }

    private void ApplyFingers(ChordTemplate grip)
    {
        if (grip.Fingerless)
        {
            grip.Fingers.Clear();
            return;
        }

        foreach (var (index, finger) in Fingers)
        {
            if (!grip.Frets.ContainsKey(index)) continue;
            if (finger >= 0) grip.Fingers[index] = finger;
            else grip.Fingers.Remove(index);
            grip.FollowedFingering = false;
        }
    }

    private int GhostGemsForEdit(HandShape shape, ChordTemplate original, ChordTemplate grip,
        Func<HandShape, ChordTemplate, int> inferGhosts)
    {
        var inferred = inferGhosts(shape, grip);
        if (Template != null) return Template.GhostMask == 0 ? inferred : Template.GhostMask;
        var old = GripEdit.Ghosts(shape, original, inferGhosts);
        foreach (var index in grip.Frets.Keys.Where(original.Frets.ContainsKey))
        {
            if (!shape.GhostMask.HasValue && original.GetFret(index) != grip.GetFret(index)) continue;
            var bit = ChordTemplate.GhostBit(index);
            inferred = (inferred & ~bit) | (old & bit);
        }
        return inferred;
    }
}
