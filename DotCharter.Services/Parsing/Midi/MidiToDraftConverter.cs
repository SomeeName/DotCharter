namespace DotCharter.Services.Parsing.Midi;

public sealed class MidiDraftOptions
{
    public static readonly IReadOnlyList<int> StandardGuitarMidi = new[] { 40, 45, 50, 55, 59, 64 };
    public static readonly IReadOnlyList<int> StandardBassMidi = new[] { 28, 33, 38, 43 };

    /// <summary>Open-string MIDI pitches, lowest string first. Map the
    /// arrangement tuning to these before converting.</summary>
    public IReadOnlyList<int> OpenStringMidi { get; init; } = StandardGuitarMidi;

    public int MaxFret { get; init; } = FretLimits.MaxFret;

    /// <summary>Only convert this track (0-based). Null = merge all tracks.</summary>
    public int? TrackIndex { get; init; }

    /// <summary>Channel 10 (index 9) is percussion in General MIDI and is
    /// skipped unless this is set.</summary>
    public bool IncludeDrumChannel { get; init; }
}

/// <summary>A draft note candidate from a MIDI file: the chartable note plus
/// the raw source info a review UI needs.</summary>
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
/// Positions follow elapsed time on the suggested beat grid, including tempo
/// changes inside a beat (see <see cref="MidiBeatGrid.PositionFromTicks"/>).
/// Format-2 sequences keep their own clocks; merging places them at their own
/// elapsed times on the first selected track's grid.
/// Pitch → (string, fret) uses the same lowest-fret-near-hand-position
/// heuristic:
/// prefer open strings, then the
/// fret closest to where the hand already is, penalize going above fret 12,
/// never two simultaneous notes on one string. Unmappable pitches are skipped.
/// </summary>
public static class MidiToDraftConverter
{
    private const int InitialReferenceFret = 3; // neutral hand position

    public static MidiChartDraft Convert(MidiFileData file, MidiDraftOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(file);
        options ??= new MidiDraftOptions();
        if (options.OpenStringMidi.Count == 0)
            throw new ArgumentException("OpenStringMidi must not be empty.", nameof(options));

        var gridTrack = options.TrackIndex is { } selected && selected >= 0 && selected < file.Tracks.Count
            ? file.Tracks[selected]
            : file.Tracks.FirstOrDefault();
        var tempoMap = new MidiTempoMap(file.TicksPerQuarterNote,
            file.Format == 2 ? gridTrack?.TempoEvents ?? [] : file.TempoEvents);

        var sourceNotes = SelectNotes(file, options, tempoMap);
        var drafts = new List<MidiDraftNote>(sourceNotes.Count);
        // Placed once the grid exists: the grid's length depends on the last
        // note kept, and a note's position depends on the grid.
        var placements = new List<(GuitarNote Note, decimal StartTime, decimal EndTime)>(sourceNotes.Count);
        var referenceFret = InitialReferenceFret;
        var lastEndTime = 0m;

        // Notes sharing an elapsed time are one onset: they compete for strings.
        foreach (var onset in GroupByStartTime(sourceNotes))
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
                placements.Add((note, midiNote.StartTime, midiNote.EndTime));
                drafts.Add(new MidiDraftNote(note, (double)(midiNote.StartTime / file.TicksPerQuarterNote / 1_000_000),
                    midiNote.Pitch, midiNote.Velocity));
                lastEndTime = Math.Max(lastEndTime, midiNote.EndTime);
            }
        }

        var signatures = file.Format == 2 ? gridTrack?.TimeSignatureEvents ?? [] : file.TimeSignatureEvents;
        var grid = tempoMap.BuildBeatGrid(tempoMap.TicksAtTickTime(lastEndTime), signatures);
        foreach (var (note, startTime, endTime) in placements)
        {
            (note.Position, note.Sustain) = grid.SpanFromTickTimes(startTime, endTime);
        }

        return new MidiChartDraft(drafts, grid.Beats);
    }

    private static List<TimedNote> SelectNotes(MidiFileData file, MidiDraftOptions options, MidiTempoMap sharedMap)
    {
        var tracks = options.TrackIndex is { } index
            ? index >= 0 && index < file.Tracks.Count
                ? [file.Tracks[index]]
                : []
            : file.Tracks.ToArray();

        return tracks.SelectMany(track =>
            {
                var map = file.Format == 2
                    ? new MidiTempoMap(file.TicksPerQuarterNote, track.TempoEvents)
                    : sharedMap;
                return track.Notes.Where(n => options.IncludeDrumChannel || n.Channel != 9)
                    .Select(n => new TimedNote(n.Channel, n.Pitch, n.Velocity,
                        map.TickTime(n.StartTicks), map.TickTime(n.StartTicks + n.LengthTicks)));
            })
            .OrderBy(n => n.StartTime)
            .ThenBy(n => n.Pitch) // low pitch first: the bass note claims the low string
            .ThenBy(n => n.Channel)
            .ToList();
    }

    private readonly record struct TimedNote(int Channel, int Pitch, int Velocity, decimal StartTime, decimal EndTime);

    private static IEnumerable<List<TimedNote>> GroupByStartTime(List<TimedNote> notes)
    {
        var group = new List<TimedNote>();
        foreach (var note in notes)
        {
            if (group.Count > 0 && group[0].StartTime != note.StartTime)
            {
                yield return group;
                group = [];
            }

            group.Add(note);
        }

        if (group.Count > 0) yield return group;
    }

    /// <summary>Lowest-fret-near-hand-position heuristic:
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
