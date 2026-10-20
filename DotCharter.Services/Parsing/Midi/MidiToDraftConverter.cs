namespace DotCharter.Services.Parsing.Midi;

public sealed class MidiDraftOptions
{
    public static readonly IReadOnlyList<int> StandardGuitarMidi = new[] { 40, 45, 50, 55, 59, 64 };
    public static readonly IReadOnlyList<int> StandardBassMidi = new[] { 28, 33, 38, 43 };

    /// <summary>Open-string MIDI pitches, lowest string first. Mirrors
    /// <c>AudioToChartOptions.OpenStringMidi</c>; map the arrangement tuning
    /// to these before converting.</summary>
    public IReadOnlyList<int> OpenStringMidi { get; init; } = StandardGuitarMidi;

    public int MaxFret { get; init; } = 24;

    /// <summary>Only convert this track (0-based). Null = merge all tracks.</summary>
    public int? TrackIndex { get; init; }

    /// <summary>Channel 10 (index 9) is percussion in General MIDI and is
    /// skipped unless this is set.</summary>
    public bool IncludeDrumChannel { get; init; }
}

/// <summary>A draft note candidate from a MIDI file: the chartable note plus
/// the raw source info a review UI needs. Mirrors the shape of
/// <c>DotCharter.Analysis</c>'s <c>DraftNote</c> so the chart-from-audio
/// draft flow (new draft arrangement, accept/reject) can consume it
/// unchanged.</summary>
public sealed class MidiDraftNote
{
    public MidiDraftNote(GuitarNote note, double timeSeconds, int midi, int velocity)
    {
        Note = note;
        TimeSeconds = timeSeconds;
        Midi = midi;
        Velocity = velocity;
    }

    public GuitarNote Note { get; }
    public double TimeSeconds { get; }
    public int Midi { get; }
    public int Velocity { get; }
}

public sealed class MidiChartDraft
{
    public MidiChartDraft(IReadOnlyList<MidiDraftNote> notes, IReadOnlyList<BeatMarker> suggestedBeats)
    {
        Notes = notes;
        SuggestedBeats = suggestedBeats;
    }

    /// <summary>Draft candidates in time order. Never auto-committed.</summary>
    public IReadOnlyList<MidiDraftNote> Notes { get; }

    /// <summary>Beat-grid suggestion from the MIDI tempo map: one marker per
    /// beat of each time signature (an eighth in 7/8), measure flags from the
    /// time-signature events. The notes' positions are on THIS grid.</summary>
    public IReadOnlyList<BeatMarker> SuggestedBeats { get; }
}

/// <summary>
/// Converts a parsed MIDI file into draft <see cref="GuitarNote"/>s plus a
/// beat-grid suggestion. Deterministic — same file + options, same draft.
///
/// Positions are exact rationals on the suggested beat grid (ticks into the
/// beat ÷ the beat's ticks — see <see cref="MidiBeatGrid.PositionFromTicks"/>),
/// so no double-rounding ever touches a <see cref="FractionalPosition"/>.
/// Pitch → (string, fret) uses the same lowest-fret-near-hand-position
/// heuristic as
/// <c>AudioToChartAnalyzer.TryAssignString</c>: prefer open strings, then the
/// fret closest to where the hand already is, penalize going above fret 12,
/// never two simultaneous notes on one string. Unmappable pitches are skipped.
/// </summary>
public static class MidiToDraftConverter
{
    private const int InitialReferenceFret = 3; // neutral hand position, as in AudioToChartAnalyzer

    public static MidiChartDraft Convert(MidiFileData file, MidiDraftOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(file);
        options ??= new MidiDraftOptions();
        if (options.OpenStringMidi.Count == 0)
            throw new ArgumentException("OpenStringMidi must not be empty.", nameof(options));

        var tempoMap = new MidiTempoMap(file.TicksPerQuarterNote, file.TempoEvents);

        var sourceNotes = SelectNotes(file, options);
        var drafts = new List<MidiDraftNote>(sourceNotes.Count);
        // Placed once the grid exists: the grid's length depends on the last
        // note kept, and a note's position depends on the grid.
        var placements = new List<(GuitarNote Note, long StartTicks, long EndTicks)>(sourceNotes.Count);
        var referenceFret = InitialReferenceFret;
        long lastEndTicks = 0;

        // Notes sharing a start tick are one onset: they compete for strings.
        foreach (var onset in GroupByStartTick(sourceNotes))
        {
            var usedStrings = 0;
            foreach (var midiNote in onset)
            {
                if (!TryAssignString(midiNote.Pitch, options, usedStrings, referenceFret,
                        out var stringIndex, out var fret))
                {
                    continue;
                }

                usedStrings |= 1 << stringIndex;
                if (fret > 0) referenceFret = fret;

                var note = new GuitarNote { StringIndex = stringIndex, Fret = fret };
                placements.Add((note, midiNote.StartTicks, midiNote.StartTicks + midiNote.LengthTicks));
                drafts.Add(new MidiDraftNote(note, tempoMap.TicksToMs(midiNote.StartTicks) / 1000.0,
                    midiNote.Pitch, midiNote.Velocity));
                lastEndTicks = Math.Max(lastEndTicks, midiNote.StartTicks + midiNote.LengthTicks);
            }
        }

        var grid = tempoMap.BuildBeatGrid(lastEndTicks, file.TimeSignatureEvents);
        foreach (var (note, startTicks, endTicks) in placements)
        {
            note.Position = grid.PositionFromTicks(startTicks);
            note.Sustain = grid.PositionFromTicks(endTicks) - note.Position;
        }

        return new MidiChartDraft(drafts, grid.Beats);
    }

    private static List<MidiNoteEvent> SelectNotes(MidiFileData file, MidiDraftOptions options)
    {
        var tracks = options.TrackIndex is { } index
            ? index >= 0 && index < file.Tracks.Count
                ? [file.Tracks[index]]
                : []
            : file.Tracks.ToArray();

        return tracks
            .SelectMany(t => t.Notes)
            .Where(n => options.IncludeDrumChannel || n.Channel != 9)
            .OrderBy(n => n.StartTicks)
            .ThenBy(n => n.Pitch) // low pitch first: the bass note claims the low string
            .ThenBy(n => n.Channel)
            .ToList();
    }

    private static IEnumerable<List<MidiNoteEvent>> GroupByStartTick(List<MidiNoteEvent> notes)
    {
        var group = new List<MidiNoteEvent>();
        foreach (var note in notes)
        {
            if (group.Count > 0 && group[0].StartTicks != note.StartTicks)
            {
                yield return group;
                group = [];
            }

            group.Add(note);
        }

        if (group.Count > 0) yield return group;
    }

    /// <summary>Same heuristic as <c>AudioToChartAnalyzer.TryAssignString</c>:
    /// open strings are free, otherwise stay near the current hand position;
    /// playing above the 12th fret when a lower voicing exists is penalized.</summary>
    private static bool TryAssignString(int midi, MidiDraftOptions options, int usedStrings,
        int referenceFret, out int stringIndex, out int fret)
    {
        stringIndex = -1;
        fret = -1;
        var bestCost = int.MaxValue;

        for (var s = 0; s < options.OpenStringMidi.Count; s++)
        {
            if ((usedStrings & (1 << s)) != 0) continue;
            var f = midi - options.OpenStringMidi[s];
            if (f < 0 || f > options.MaxFret) continue;

            var cost = (f == 0 ? 0 : Math.Abs(f - referenceFret)) + (f > 12 ? 3 : 0);
            if (cost < bestCost || (cost == bestCost && f < fret))
            {
                bestCost = cost;
                stringIndex = s;
                fret = f;
            }
        }

        return stringIndex >= 0;
    }
}
