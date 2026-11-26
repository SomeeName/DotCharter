namespace DotCharter.Services.Parsing.GuitarPro;

/// <summary>
///     Attaches a track's collected GP5 second-voice sidecar to its arrangement. It gets the
///     NOTATION-relevant passes only — tied-beat merge (which sums the authored
///     <see cref="GuitarNote.NotationDuration"/>) and slide linking — but NEVER
///     <see cref="GpPostProcessor.AdjustSustains"/>: the sidecar keeps its authored rhythm
///     verbatim, exactly like the gpif voices-2-4 path.
/// </summary>
internal static class GpSidecarFinish
{
    public static void Attach(NotationVoice sidecar, Arrangement arr, int lastFret, ISet<IGuitarSound> deadSlideSounds)
    {
        if (sidecar.Notes.Count == 0 && sidecar.Chords.Count == 0) return;

        var shim = new ChartLevel();
        foreach (var n in sidecar.Notes) shim.Notes.Add(n);
        foreach (var c in sidecar.Chords) shim.Chords.Add(c);
        GpPostProcessor.MergeTiedNotes(shim, arr.StringCount);
        GpDeadNoteSlide.Resolve(shim, arr.Capo, lastFret, deadSlideSounds);
        GpPostProcessor.LinkSlides(shim, arr.StringCount);
        sidecar.Notes.Clear();
        foreach (var n in shim.Notes) sidecar.Notes.Add(n);
        sidecar.Chords.Clear();
        foreach (var c in shim.Chords) sidecar.Chords.Add(c);

        arr.NotationVoices.Add(sidecar);
    }
}
