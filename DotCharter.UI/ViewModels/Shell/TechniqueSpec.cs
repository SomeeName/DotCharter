namespace DotCharter.UI.ViewModels.Shell;

/// <summary>
///     One technique toggle, as the status rail needs to see it: its name and how to read what
///     it left on a note or chord. A state is the resx key of the label that says what the sound
///     now carries ("Technique_Hopo_PullOff"), or null when the technique is off.
/// </summary>
public sealed record TechniqueSpec(
    string NameKey,
    Func<GuitarNote, string?> NoteState,
    Func<Chord, IEnumerable<string?>> ChordStates)
{
    public static readonly TechniqueSpec Hopo = PerSound("Technique_Hopo", s => s.Hopo switch
    {
        HopoType.HammerOn => "Technique_Hopo_HammerOn",
        HopoType.PullOff => "Technique_Hopo_PullOff",
        HopoType.Tap => "Technique_Hopo_Tap",
        _ => null
    });

    public static readonly TechniqueSpec Harmonic = Flag("Technique_Harmonic", s => s.Harmonic != HarmonicType.None);

    public static readonly TechniqueSpec Mute = PerSound("Technique_Mute", s => s.Mute switch
    {
        MuteType.Full => "Technique_Mute_Full",
        MuteType.Palm => "Technique_Mute_Palm",
        _ => null
    });

    public static readonly TechniqueSpec LinkNext = Flag("Technique_LinkNext", s => s.LinkNext);
    public static readonly TechniqueSpec Vibrato = Flag("Technique_Vibrato", s => s.Vibrato);
    public static readonly TechniqueSpec Tremolo = Flag("Technique_Tremolo", s => s.Tremolo);

    public static readonly TechniqueSpec Picking = PerSound("Technique_Picking", s => s.BassPicking switch
    {
        BassPickingType.Pop => "Technique_Picking_Pop",
        BassPickingType.Slap => "Technique_Picking_Slap",
        _ => null
    });

    public static readonly TechniqueSpec Accent = WholeChord("Technique_Accent", n => n.Accent, c => c.Accent);
    public static readonly TechniqueSpec Ignore = WholeChord("Technique_Ignore", n => n.Ignore, c => c.Ignore);

    public static readonly TechniqueSpec PassOtherNotes =
        WholeChord("Technique_PassOtherNotes", n => n.PassOtherNotes, c => c.PassOtherNotes);

    /// <summary>What the technique reads as on every selected note and chord (one entry per sound).</summary>
    public IEnumerable<string?> StatesOf(IEnumerable<GuitarNote> notes, IEnumerable<Chord> chords) =>
        notes.Select(NoteState).Concat(chords.SelectMany(ChordStates));

    private static TechniqueSpec PerSound(string nameKey, Func<IGuitarSound, string?> state) =>
        new(nameKey, n => state(n), c => c.ChordNotes.Select(state));

    private static TechniqueSpec Flag(string nameKey, Func<IGuitarSound, bool> on) =>
        PerSound(nameKey, s => on(s) ? nameKey : null);

    private static TechniqueSpec WholeChord(string nameKey, Func<GuitarNote, bool> note, Func<Chord, bool> chord) =>
        new(nameKey, n => note(n) ? nameKey : null, c => [chord(c) ? nameKey : null]);
}

/// <summary>The status-rail words for a technique toggle ("Hammer-on set on 9 items").</summary>
public static class TechniqueToggleStatus
{
    public static (string Text, StatusSeverity Severity) Describe(
        TechniqueSpec spec, IReadOnlyCollection<string?> states, ILocalizationService l)
    {
        if (states.Count == 0) return (l["Status_TechniqueNothingSelected"], StatusSeverity.Warning);

        var distinct = states.Distinct().ToList();
        if (distinct.Count > 1)
            return (Items(l, "Status_TechniqueToggled", l[spec.NameKey], states.Count), StatusSeverity.Info);

        return distinct[0] is { } on
            ? (Items(l, "Status_TechniqueSet", l[on], states.Count), StatusSeverity.Info)
            : (Items(l, "Status_TechniqueCleared", l[spec.NameKey], states.Count), StatusSeverity.Info);
    }

    private static string Items(ILocalizationService l, string key, string name, int count) =>
        l.PluralWith(key + "_One", key + "_Many", count, name, count);
}
