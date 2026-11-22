namespace DotCharter.UI.ViewModels.Panels;

internal sealed record ChordGripMatches(IReadOnlyList<ChordTemplate> SameFrets, IReadOnlyList<ChordTemplate> Transposed)
{
    internal static ChordGripMatches Find(Arrangement arrangement, ChordTemplate loaded)
    {
        var flavour = ArpeggioFlavour.Of(loaded);
        var same = arrangement.ChordTemplates.Where(t => !ReferenceEquals(t, loaded) &&
            flavour.Matches(t) && t.Frets.Count > 0 && MapEquality.SameEntries(t.Frets, loaded.Frets)).ToArray();
        var seen = new HashSet<ChordTemplate>(same, ReferenceEqualityComparer.Instance) { loaded };
        var transposed = ChordShapeTransposition.FindTransposed(arrangement, loaded, arrangement.Capo)
            .Where(t => flavour.Matches(t) && seen.Add(t)).ToArray();
        return new(same, transposed);
    }

    internal IReadOnlyList<ChordTemplate> For(ChordTemplateEditKind kind) => kind switch
    {
        ChordTemplateEditKind.Fingering or ChordTemplateEditKind.Technique => [.. SameFrets, .. Transposed],
        ChordTemplateEditKind.Name => SameFrets,
        _ => []
    };
}
