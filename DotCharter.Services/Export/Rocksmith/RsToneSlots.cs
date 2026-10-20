namespace DotCharter.Services.Export.Rocksmith;

/// <summary>
///     The tone slot resolution a package build uses when no tone-map override exists —
///     shared by the SNG and RS-XML writers, the export validator and the Tone Designer's
///     prefill, so all of them agree.
/// </summary>
public static class RsToneSlots
{
    /// <summary>
    /// Resolves the four RS tone slots (Tone_A..Tone_D) and the base tone for
    /// the arrangement. Slot ids are the SNG tone-event ids. Tone change
    /// names not already assigned to a slot fill the first free slot.
    /// </summary>
    public static (string ToneBase, string[] Slots) Resolve(Arrangement arrangement)
    {
        var slots = new List<string>(4);
        foreach (var configured in new[]
                 {
                     arrangement.ToneA, arrangement.ToneB, arrangement.ToneC, arrangement.ToneD
                 })
        {
            if (!string.IsNullOrWhiteSpace(configured) && !slots.Contains(configured))
                slots.Add(configured);
        }

        foreach (var change in arrangement.ToneChanges)
        {
            if (string.IsNullOrWhiteSpace(change.ToneName) || slots.Contains(change.ToneName)) continue;
            if (slots.Count >= 4) break;
            slots.Add(change.ToneName);
        }

        var toneBase = !string.IsNullOrWhiteSpace(arrangement.ToneBase)
            ? arrangement.ToneBase
            : !string.IsNullOrWhiteSpace(arrangement.StartingTone)
                ? arrangement.StartingTone
                : slots.FirstOrDefault() ?? "";

        return (toneBase, slots.ToArray());
    }
}
