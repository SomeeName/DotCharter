using DotCharter.Localization;
using DotCharter.Services.Parsing.GuitarPro;

namespace DotCharter.Services.Import.GuitarPro;

/// <summary>
///     What to check by hand after an add-new Guitar Pro import, the three places a tab most often reads
///     differently in the game: the meter (<see cref="MeterChanges" />, counting measure 1's time signature),
///     the slides (<see cref="Slides" />: pitched and unpitched, single notes and chord notes) and the ties
///     the import kept as Link-Next (<see cref="LinkedTies" />). Appended to the import's status line.
/// </summary>
public sealed record GpImportChecklist(int MeterChanges, int Slides, int LinkedTies)
{
    public static GpImportChecklist Count(GpImportMetadata metadata, IEnumerable<Arrangement> imported)
    {
        var meter = 0;
        (int, int)? previous = null;
        foreach (var bar in metadata.Bars)
        {
            var signature = (bar.BeatsInMeasure, bar.NoteDenominator);
            if (signature != previous) meter++;
            previous = signature;
        }

        var (slides, ties) = (0, 0);
        foreach (var level in imported.SelectMany(a => a.Levels))
        {
            foreach (var note in level.Notes) Tally(note.SlideTo != null || note.UnpitchedSlide, note.LinkNext, ref slides, ref ties);
            foreach (var note in level.Chords.SelectMany(c => c.ChordNotes))
                Tally(note.SlideTo != null || note.UnpitchedSlide, note.LinkNext, ref slides, ref ties);
        }

        return new GpImportChecklist(meter, slides, ties);
    }

    private static void Tally(bool slide, bool linked, ref int slides, ref int ties)
    {
        if (slide) slides++;
        if (linked) ties++;
    }

    public string Text(ILocalizationService localization) =>
        localization.Format("Status_GpImport_Checklist", MeterChanges, Slides, LinkedTies);
}
