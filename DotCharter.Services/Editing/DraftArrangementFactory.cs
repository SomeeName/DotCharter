namespace DotCharter.Services.Editing;

/// <summary>
///     Builds the empty "draft layer" arrangement the two draft importers add
///     beside the arrangement the user is editing — the MIDI import
///     (<c>ChartMaintenanceService.ApplyMidiDraft</c>) and chart-from-audio
///     (<c>ChartFromAudioDialogViewModel.Apply</c>).
/// </summary>
/// <remarks>
///     <b>It exists because both call sites carried the same five-property
///     initializer and both forgot the SIXTH: <see cref="Arrangement.TuningPitches" />.</b>
///     A chart states its tuning twice — letters and per-string offsets — and
///     every exporter resolves through <c>TuningOffsetResolution</c>, which
///     derives offsets from letters ONLY for 4- and 6-string instruments
///     (a 5-string bass offset has no agreed base). So a draft taken beside a
///     7- or 8-string arrangement inherited its LETTERS and exported with an
///     empty offset vector, i.e. as standard tuning — while the draft's own
///     frets had been computed against the template's REAL offsets
///     (<c>MidiImportDialogViewModel.ResolveOpenStringMidi</c> reads
///     <c>TuningPitches</c>). Even on six strings the letters-only derivation is
///     lossy: it returns the nearest spelling in [-6, +5], so an octave-down
///     offset collapses to 0.
/// </remarks>
public static class DraftArrangementFactory
{
    /// <summary>
    ///     A new, empty arrangement named <paramref name="name" /> carrying
    ///     <paramref name="template" />'s instrument settings verbatim, or
    ///     E-standard six-string when there is no template to copy.
    /// </summary>
    public static Arrangement FromTemplate(Arrangement? template, string name)
    {
        var arrangement = new Arrangement
        {
            Name = name,
            ArrangementType = template?.ArrangementType ?? ArrangementType.Lead,
            StringCount = template?.StringCount ?? 6,
            Tuning = template?.Tuning.ToArray() ?? ["E", "A", "D", "G", "B", "e"],
            TuningType = template?.TuningType ?? "E_STANDARD",
            Capo = template?.Capo ?? 0
        };

        // The offsets, not just the letters — and a NEW TuningOffset per string,
        // because it is a mutable reference type: handing the template's own
        // instances over would make an edit to the draft's tuning silently
        // retune the arrangement it was taken from.
        if (template != null)
        {
            foreach (var offset in template.TuningPitches)
                arrangement.TuningPitches.Add(new TuningOffset(offset.Step));
        }

        return arrangement;
    }
}
