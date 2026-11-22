using DotCharter.Services.Editing.ChordShapes;
using DotCharter.Services.Parsing.Xml.Song;

namespace DotCharter.Services.Editing;

/// <summary>Chord work on one level, judged by the exported sounds and the automatic namer.
/// The chart is read only; lookups and name verdicts live for one queue refresh.</summary>
public sealed class ChordWorkEligibility
{
    private readonly Arrangement _arrangement;
    private readonly ChordNamingOptions _naming;
    private readonly DoubleStopNameOptions _doubleStops;
    private readonly HashSet<Chord> _looseNotes;
    private readonly Dictionary<int, ChordTemplate> _templates = [];
    private readonly Dictionary<Chord, ChordTemplate?> _synthesized = [];
    private readonly Dictionary<ChordTemplate, bool> _needsName = new(ReferenceEqualityComparer.Instance);

    public ChordWorkEligibility(Arrangement arrangement, ChartLevel level,
        ChordNamingOptions naming, DoubleStopNameOptions doubleStops)
    {
        _arrangement = arrangement;
        _naming = naming;
        _doubleStops = doubleStops;
        _looseNotes = SoundsWithoutTemplate(level);
        foreach (var template in arrangement.ChordTemplates) _templates.TryAdd(template.Id, template);
    }

    public bool NeedsFingering(Chord chord)
    {
        if (!UsesFingering(chord, _looseNotes)) return false;
        var template = ExportTemplateFor(chord);
        if (template == null) return false;
        foreach (var stringIndex in template.Frets.Keys)
        {
            if (IsMissingFinger(template, stringIndex, _arrangement.Capo)) return true;
        }

        return false;
    }

    public bool NeedsName(Chord chord)
    {
        if (_looseNotes.Contains(chord)) return false;
        var template = ExportTemplateFor(chord);
        if (template == null || template.Unnamed || !string.IsNullOrWhiteSpace(template.Name)) return false;
        if (_needsName.TryGetValue(template, out var needsName)) return needsName;
        needsName = !string.IsNullOrWhiteSpace(NewChordNames.Suggest(
            _arrangement, template.Frets, _naming, _doubleStops));
        _needsName[template] = needsName;
        return needsName;
    }

    public bool NeedsStoredTemplate(Chord chord) =>
        !_looseNotes.Contains(chord) && ExportTemplateFor(chord) == null;

    private ChordTemplate? ExportTemplateFor(Chord chord)
    {
        if (_templates.TryGetValue(chord.TemplateId, out var template)) return template;
        if (_synthesized.TryGetValue(chord, out var synthesized)) return synthesized;
        synthesized = SynthesizeTemplate(chord);
        _synthesized[chord] = synthesized;
        return synthesized;
    }

    /// <summary>Explicit splits and precise link landings, using the export's projection.</summary>
    public static HashSet<Chord> SoundsWithoutTemplate(ChartLevel level) =>
        LinkLandingBreakup.LandedChords(level.Notes, level.Chords);

    public static bool UsesFingering(Chord chord, IReadOnlySet<Chord> looseNotes) =>
        !chord.IsFullyMuted() && !looseNotes.Contains(chord);

    /// <summary>Thumb zero is a finger; absent or negative fingers are missing above the capo.</summary>
    public static bool IsMissingFinger(ChordTemplate template, int stringIndex, int capo) =>
        !template.Fingerless && template.GetFret(stringIndex) > capo && template.GetFinger(stringIndex) < 0;

    /// <summary>The blank grip the export can derive without a stored template.</summary>
    public static ChordTemplate? SynthesizeTemplate(Chord chord)
    {
        if (LinkLandingBreakup.IsSplit(chord) || chord.ChordNotes.Count == 0) return null;
        var template = new ChordTemplate();
        foreach (var note in chord.ChordNotes) template.Frets[note.StringIndex] = note.Fret;
        return template;
    }
}
