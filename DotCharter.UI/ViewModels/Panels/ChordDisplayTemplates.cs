using DotCharter.Services.Editing.ChordShapes;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.UI.ViewModels.Panels;

/// <summary>
///     Display-only helpers for the chord-template panel: which template a selected chord shows,
///     and how a hand shape's notes are overlaid on the per-string rows.
/// </summary>
internal static class ChordDisplayTemplates
{
    /// <summary>
    /// The template to DISPLAY for a selected chord. Returns the chord's own
    /// template when it exists and its frets match the chord's notes; otherwise
    /// reflects the chord's ACTUAL notes — reusing an equivalent real template
    /// (its name + fingering win) or synthesising a display-only template
    /// (Id -1) with a suggested fingering and name so the finger column and the
    /// 3D preview agree. The synthesised template is never added to the
    /// arrangement here — reconciliation happens on the first edit
    /// (<c>ChordTemplateEditPlanner</c>), so merely selecting a
    /// still-untemplated chord neither dirties the project nor bloats the list.
    /// </summary>
    internal static ChordTemplate Resolve(Chord chord, Arrangement arr, IFingeringGenerationService fingeringService,
        Func<Arrangement, ChordNamingOptions> naming)
    {
        var assigned = arr.FindChordTemplate(chord.TemplateId);
        if (assigned != null && !assigned.IsArpeggio && ChordTemplateEditPlanner.TemplateMatchesChord(assigned, chord))
            return assigned;

        var seed = new ChordTemplate { Id = -1, Name = string.Empty };
        foreach (var cn in chord.ChordNotes)
            seed.Frets[cn.StringIndex] = cn.Fret;

        var equiv = fingeringService.FindEquivalentTemplate(arr, seed);
        if (equiv != null) return equiv;

        ArrangementFingeringStyle.SetSuggestedFingers(arr, seed, arr.StringCount);
        seed.Name = NewChordNames.For(arr, seed.Frets, naming(arr));
        return seed;
    }

    internal static void OverlayNotes(HandShape hs, ChartEditorViewModel editor, IReadOnlyList<StringFingeringInput> inputs)
    {
        // Walk every note + chord-note inside the handshape's range and make
        // sure each (string, fret) pair shows up on the fretboard preview. The
        // template might be empty (handshape created via right-click) or hold
        // only a subset; we never overwrite explicit template frets.
        bool InRange(FractionalPosition p) => p >= hs.Position && p < hs.EndPosition;

        foreach (var n in editor.Notes.Where(n => InRange(n.Position)))
            ApplyOverlayFret(inputs, n.StringIndex, n.Fret);

        foreach (var ch in editor.Chords.Where(c => InRange(c.Position)))
        {
            foreach (var cn in ch.ChordNotes)
                ApplyOverlayFret(inputs, cn.StringIndex, cn.Fret);
        }
    }

    private static void ApplyOverlayFret(IReadOnlyList<StringFingeringInput> inputs, int stringIndex, int fret)
    {
        if (stringIndex < 0 || stringIndex >= inputs.Count) return;
        var input = inputs[stringIndex];
        if (!string.IsNullOrEmpty(input.FretText)) return;
        input.FretText = fret.ToString();
    }
}
