using System.Globalization;
using System.Text;
using DotCharter.Services.Editing;
using DotCharter.Services.Export.Rocksmith;
using DotCharter.Services.Export.Sng;
using DotCharter.Services.Library;
using DotCharter.Services.Library.Index;
using DotCharter.Services.Parsing.Xml.Vocals;

namespace DotCharter.Services.Export.Validation;

public enum ExportFindingSeverity
{
    Warning,
    Error
}

/// <summary>A single pre-export validation finding. <see cref="MessageKey"/>
/// is a localization key ("Export.Validation.*"); <see cref="Detail"/> carries
/// the interpolation payload so the UI can format a localized message.</summary>
public sealed record ExportValidationFinding(
    string ArrangementName,
    ExportFindingSeverity Severity,
    string MessageKey,
    double? TimeMs = null,
    string Detail = "")
{
    public bool HasTime => TimeMs.HasValue;

    /// <summary>The verb that repairs this finding, or <see cref="ExportFixKind.None" />.</summary>
    public ExportFixKind Fix => ExportFindingFixes.For(MessageKey);
}

/// <summary>
/// Pure pre-export rule check for the PSARC pipeline (0.9.0
/// validation inspector). Complements the editing-time
/// <c>ArrangementValidationService</c> with the rules that decide whether an
/// export produces a playable package: FHP coverage, overlapping notes,
/// empty phrases, tone gaps, out-of-range frets, first-beat/silence rules
/// and vocals limits. UI hookup follows in the export dialog.
/// </summary>
public sealed class ChartExportValidator
{
    public const string KeyNoBeatMap = "Export.Validation.NoBeatMap";
    public const string KeyNoArrangements = "Export.Validation.NoArrangements";
    public const string KeyNoShowlights = "Export.Validation.NoShowlights";
    public const string KeyNoNotes = "Export.Validation.NoNotes";
    public const string KeyMissingFhpCoverage = "Export.Validation.MissingFhpCoverage";
    public const string KeyOverlappingNotes = "Export.Validation.OverlappingNotes";
    public const string KeyEmptyPhrase = "Export.Validation.EmptyPhrase";
    public const string KeyNoPhrases = "Export.Validation.NoPhrases";
    public const string KeyTooManyPhrases = "Export.Validation.TooManyPhrases";
    public const string KeyToneWithoutSlot = "Export.Validation.ToneWithoutSlot";
    public const string KeyMissingBaseTone = "Export.Validation.MissingBaseTone";
    public const string KeyTooManyTones = "Export.Validation.TooManyTones";
    public const string KeyToneNotDefined = "Export.Validation.ToneNotDefined";
    public const string KeyFretOutOfRange = "Export.Validation.FretOutOfRange";
    public const string KeyStringOutOfRange = "Export.Validation.StringOutOfRange";
    public const string KeyExtendedRangeGuitar = "Export.Validation.ExtendedRangeGuitar";
    public const string KeyExtendedRangeBass = "Export.Validation.ExtendedRangeBass";
    public const string KeyExtendedRangeTuning = "Export.Validation.ExtendedRangeTuning";
    public const string KeySixStringDrop = "Export.Validation.SixStringDrop";
    public const string KeyAudioShorterThanChart = "Export.Validation.AudioShorterThanChart";
    public const string KeyNoteBelowCapo = "Export.Validation.NoteBelowCapo";
    public const string KeyFirstNoteTooEarly = "Export.Validation.FirstNoteTooEarly";
    public const string KeyFirstPhraseNotAtStart = "Export.Validation.FirstPhraseNotAtStart";
    public const string KeyLyricTooLong = "Export.Validation.LyricTooLong";
    public const string KeyLyricsOutOfOrder = "Export.Validation.LyricsOutOfOrder";
    public const string KeyLyricMarkerText = "Export.Validation.LyricMarkerText";
    public const string KeyDisplayNameGlyphs = "Export.Validation.DisplayNameGlyphs";
    public const string KeyMetadataTitleCase = "Export.Validation.MetadataTitleCase";
    public const string KeyMetadataStraySpaces = "Export.Validation.MetadataStraySpaces";
    public const string KeyMetadataMissingTitle = "Export.Validation.MetadataMissingTitle";
    public const string KeyMetadataMissingArtist = "Export.Validation.MetadataMissingArtist";
    public const string KeyMetadataMissingAlbum = "Export.Validation.MetadataMissingAlbum";
    public const string KeyMetadataMissingAlbumYear = "Export.Validation.MetadataMissingAlbumYear";
    public const string KeyPackageKeyClash = "Export.Validation.PackageKeyClash";
    public const string KeyTooManyLevels = "Export.Validation.TooManyLevels";
    public const string KeyTooManyNotes = "Export.Validation.TooManyNotes";
    public const string KeyFingerOutOfRange = "Export.Validation.FingerOutOfRange";
    public const string KeyStringBeyondTuning = "Export.Validation.StringBeyondTuning";
    public const string KeyStringCountUnsupported = "Export.Validation.StringCountUnsupported";

    private const int MaxFret = FretLimits.MaxFret;

    /// <summary>
    ///     The companion XML and DLC Builder project are written as a SIX-string projection (the Files setting).
    ///     On: a 7-/8-string guitar gets one <see cref="KeySixStringDrop" /> finding with what that projection leaves out.
    /// </summary>
    public bool SixStringProjection { get; init; }

    /// <summary>
    ///     The Library's index, for the one check that needs it: a DLC key an indexed package already
    ///     uses is a <see cref="KeyPackageKeyClash" /> warning. Null (no Library, a headless caller) skips the check.
    /// </summary>
    public ILibraryIndex? LibraryIndex { get; init; }

    /// <summary>
    ///     The package files this export would write. A Library package at one of these paths is the project's
    ///     OWN earlier export, so it is no <see cref="KeyPackageKeyClash" />: re-exporting replaces it.
    /// </summary>
    public IReadOnlyCollection<string> OwnPackagePaths { get; init; } = [];

    private const int MaxPhrases = 100;
    private const double MinFirstNoteMs = 1000.0;
    private const int MaxLyricBytes = 47; // 48-byte fixed SNG field incl. terminator

    /// <param name="project">The chart to check.</param>
    /// <param name="audioLengthMs">The project's audio length when known (null or 0: no audio, no check).</param>
    public IReadOnlyList<ExportValidationFinding> Validate(ChartProject project, double? audioLengthMs = null)
    {
        var findings = new List<ExportValidationFinding>();

        if (project.Beats.Count == 0)
        {
            findings.Add(new ExportValidationFinding("", ExportFindingSeverity.Error, KeyNoBeatMap));
            return findings;
        }

        var instrumentals = project.Arrangements
            .Where(a => a.ArrangementType != ArrangementType.Vocal)
            .ToList();

        if (instrumentals.Count == 0)
            findings.Add(new ExportValidationFinding("", ExportFindingSeverity.Error, KeyNoArrangements));

        foreach (var arrangement in instrumentals)
            ValidateArrangement(project, arrangement, findings);

        foreach (var track in project.VocalTracks)
            ValidateVocals(project, track, findings);

        // The package writes a default set when there is none: say so, with the fix that stores it.
        if (project.Showlights.Count == 0 && instrumentals.Count > 0)
            findings.Add(new ExportValidationFinding("", ExportFindingSeverity.Warning, KeyNoShowlights));

        ValidateDisplayNames(project, findings);
        ValidateMetadata(project, findings);
        ValidatePackageKey(project, findings);
        ValidateAudioLength(project, audioLengthMs, findings);
        return findings;
    }

    /// <summary>
    ///     The game ends the song when its audio ends, so any note past the audio is never played. A
    ///     WARNING (the charter may mean it); the detail names both elapsed lengths, including hours when needed.
    /// </summary>
    private static void ValidateAudioLength(ChartProject project, double? audioLengthMs, List<ExportValidationFinding> findings)
    {
        if (audioLengthMs is not > 0) return;
        var chartEndMs = 0.0;
        foreach (var level in project.Arrangements.Where(a => a.ArrangementType != ArrangementType.Vocal).SelectMany(a => a.Levels))
        {
            foreach (var note in level.Notes) chartEndMs = Math.Max(chartEndMs, note.EndPosition.GetPositionMs(project.Beats));
            foreach (var chord in level.Chords) chartEndMs = Math.Max(chartEndMs, chord.EndPosition.GetPositionMs(project.Beats));
        }

        if (audioLengthMs.Value >= chartEndMs) return;
        static string Clock(double ms) => ElapsedTimeLabel.Format(ms);
        findings.Add(new ExportValidationFinding("", ExportFindingSeverity.Warning, KeyAudioShorterThanChart, null,
            $"{Clock(audioLengthMs.Value)} / {Clock(chartEndMs)}"));
    }

    /// <summary>
    ///     The song list's strings, resolved exactly as the package writes them, that hold a
    /// character the game's font most likely lacks. A WARNING, never a rewrite: the
    ///     charter types an English or romanised name and keeps the original script in the
    ///     Japanese title/artist fields. The sort field is checked too, so what sorts matches
    ///     what shows.
    /// </summary>
    private static void ValidateDisplayNames(ChartProject project, List<ExportValidationFinding> findings)
    {
        var meta = RsPackageMetadataFactory.Create(project, new RocksmithBuildOptions { OutputPath = "", AudioPath = "" });
        var reported = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in (string[])[meta.Title, meta.Artist, meta.ArtistSort, meta.Album])
        {
            if (RocksmithDlcKey.LacksGameGlyphs(value) && reported.Add(value))
                findings.Add(new ExportValidationFinding("", ExportFindingSeverity.Warning, KeyDisplayNameGlyphs, null, value));
        }
    }

    /// <summary>
    ///     Song-list hygiene, all WARNINGS. Stray spaces (leading, trailing, doubled) are read from the
    ///     chart's own Title/Artist/Album fields (the ones the Song metadata dialog edits) and each arrangement's
    ///     artist, artist-sort and album overrides; the fix trims all of them in one undo step.
    ///     Missing title, artist and album, and a missing album year, are read from the resolved package
    ///     metadata, so a value an arrangement carries counts. Title case is a gentle suggestion
    ///     (<see cref="LooksSentenceCased" />) and never a rewrite.
    /// </summary>
    private static void ValidateMetadata(ChartProject project, List<ExportValidationFinding> findings)
    {
        var chart = project.Chart;
        var spaced = new List<string?> { chart.SongName, chart.Artist, chart.AlbumName };
        foreach (var arrangement in project.Arrangements)
            spaced.AddRange([arrangement.ArtistName, arrangement.ArtistNameSort, arrangement.AlbumName]);
        foreach (var value in spaced)
        {
            if (!string.IsNullOrWhiteSpace(value) && value != SongMetadataSpaces.Tidy(value))
                findings.Add(new ExportValidationFinding("", ExportFindingSeverity.Warning, KeyMetadataStraySpaces, null, value));
        }

        var meta = RsPackageMetadataFactory.Create(project, new RocksmithBuildOptions { OutputPath = "", AudioPath = "" });
        if (string.IsNullOrWhiteSpace(chart.SongName))
            findings.Add(new ExportValidationFinding("", ExportFindingSeverity.Warning, KeyMetadataMissingTitle));
        if (meta.Artist == "Unknown Artist" && string.IsNullOrWhiteSpace(chart.Artist))
            findings.Add(new ExportValidationFinding("", ExportFindingSeverity.Warning, KeyMetadataMissingArtist));
        if (string.IsNullOrWhiteSpace(meta.Album))
            findings.Add(new ExportValidationFinding("", ExportFindingSeverity.Warning, KeyMetadataMissingAlbum));
        if (meta.Year is null)
            findings.Add(new ExportValidationFinding("", ExportFindingSeverity.Warning, KeyMetadataMissingAlbumYear));

        var reported = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in (string[])[meta.Title, meta.Artist, meta.Album])
        {
            if (LooksSentenceCased(value) && reported.Add(value))
                findings.Add(new ExportValidationFinding("", ExportFindingSeverity.Warning, KeyMetadataTitleCase, null, value));
        }
    }

    /// <summary>
    ///     The game lists one song per DLC key, so an export under the key of a package already in the
    ///     Library hides it, or is hidden by it. A WARNING carrying the clashing key and the key the fix
    ///     would assign (<see cref="OpenedPackageDlcKey.KeyFor" />), joined by a bar. A read-only index
    ///     lookup; with no index, or an index that cannot be read, the check says nothing.
    /// </summary>
    private void ValidatePackageKey(ChartProject project, List<ExportValidationFinding> findings)
    {
        if (LibraryIndex is not { } index) return;
        var meta = RsPackageMetadataFactory.Create(project, new RocksmithBuildOptions { OutputPath = "", AudioPath = "" });
        if (!index.HasSongKey(meta.DlcKey)) return;
        var holders = index.PackagePathsForSongKey(meta.DlcKey);
        if (holders.Count > 0 && holders.All(IsOwnPackage)) return;

        var proposed = OpenedPackageDlcKey.KeyFor(meta.Artist, meta.Title, meta.DlcKey, index.HasSongKey);
        findings.Add(new ExportValidationFinding("", ExportFindingSeverity.Warning, KeyPackageKeyClash, null, $"{meta.DlcKey}|{proposed}"));
    }

    private bool IsOwnPackage(string path)
    {
        var full = FullPathOrSelf(path);
        return OwnPackagePaths.Any(own => string.Equals(FullPathOrSelf(own), full, StringComparison.OrdinalIgnoreCase));

        static string FullPathOrSelf(string value)
        {
            try
            {
                return Path.GetFullPath(value);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return value;
            }
        }
    }

    /// <summary>
    ///     True only for a CLEARLY sentence-cased Latin name: three or more words, a capitalised first
    ///     word, at least two later words of four or more letters (the ones a title-cased name capitalises
    ///     whatever the style guide), and every one of those starts lower case. Never true for a name with a
    ///     letter past Latin Extended-B (Japanese, Cyrillic, ...), an all-lowercase or all-caps styling, a
    ///     lower-case first word, or an upper-case letter inside a later word (iPhone, DJ): those are
    ///     deliberate. Short names ("Love me do") have too few long words to judge.
    /// </summary>
    internal static bool LooksSentenceCased(string name)
    {
        if (name.Any(c => char.IsLetter(c) && c > 'ɏ')) return false;
        var words = name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Any(char.IsLetter))
            .ToArray();
        if (words.Length < 3 || !char.IsUpper(words[0].First(char.IsLetter))) return false;

        var later = words.Skip(1).ToArray();
        if (later.Any(w => w.Skip(1).Any(char.IsUpper))) return false;
        var longWords = later.Where(w => w.Count(char.IsLetter) >= 4).ToArray();
        return longWords.Length >= 2 && longWords.All(w => char.IsLower(w.First(char.IsLetter)));
    }

    private void ValidateArrangement(
        ChartProject project, Arrangement arrangement, List<ExportValidationFinding> findings)
    {
        var beats = project.Beats;
        var sink = new ArrangementFindings(arrangement.Name, findings);

        var hasNotes = arrangement.Levels.Any(l => l.Notes.Count > 0 || l.Chords.Count > 0);
        if (!hasNotes)
        {
            sink.Add(ExportFindingSeverity.Error, KeyNoNotes);
            return;
        }

        CheckFhpCoverage(arrangement, beats, sink);
        CheckOverlappingNotes(arrangement, beats, sink);
        CheckPhrases(arrangement, beats, sink);
        if (arrangement.ToneChanges.Count > 0)
            CheckTones(project, arrangement, beats, sink);
        CheckFretsAndStrings(arrangement, beats, sink, SixStringProjection);
        CheckFormatLimits(arrangement, sink);
        CheckChordTemplates(arrangement, sink);

        // ---- first-note silence rule -------------------------------------------
        var firstNoteMs = ContentTimesMs(arrangement, beats).DefaultIfEmpty(double.MaxValue).Min();
        if (firstNoteMs < MinFirstNoteMs)
            sink.Add(ExportFindingSeverity.Warning, KeyFirstNoteTooEarly, firstNoteMs);
    }

    /// <summary>Every finding of one arrangement, stamped with its name.</summary>
    private readonly record struct ArrangementFindings(string Arrangement, List<ExportValidationFinding> Findings)
    {
        public void Add(ExportFindingSeverity severity, string key, double? timeMs = null, string detail = "") =>
            Findings.Add(new ExportValidationFinding(Arrangement, severity, key, timeMs, detail));
    }

    private static void CheckFhpCoverage(Arrangement arrangement, IList<BeatMarker> beats, ArrangementFindings sink)
    {
        foreach (var level in arrangement.Levels)
        {
            if (level.Anchors.Count == 0)
            {
                // Warning, not Error: the SNG mapper synthesizes a covering
                // anchor for an anchor-less level, so the export is game-safe;
                // the charter should still author real FHPs.
                var firstMs = FirstContentTimeMs(level, beats);
                if (firstMs.HasValue)
                    sink.Add(ExportFindingSeverity.Warning, KeyMissingFhpCoverage, firstMs, "level has no anchors");
                continue;
            }

            var firstAnchorMs = level.Anchors.Min(a => a.Position).GetPositionMs(beats);
            var uncovered = level.Notes.Select(n => n.Position)
                .Concat(level.Chords.Select(c => c.Position))
                .Select(p => p.GetPositionMs(beats))
                .Where(ms => ms < firstAnchorMs - 0.5)
                .OrderBy(ms => ms)
                .ToList();

            if (uncovered.Count > 0)
            {
                sink.Add(ExportFindingSeverity.Error, KeyMissingFhpCoverage, uncovered[0],
                    $"{uncovered.Count} note(s) before the first anchor");
            }
        }
    }

    private static void CheckOverlappingNotes(Arrangement arrangement, IList<BeatMarker> beats, ArrangementFindings sink)
    {
        foreach (var level in arrangement.Levels)
        {
            foreach (var group in level.Notes.GroupBy(n => n.StringIndex))
            {
                var ordered = group.OrderBy(n => n.Position).ToList();
                for (var i = 1; i < ordered.Count; i++)
                {
                    var previous = ordered[i - 1];
                    var current = ordered[i];
                    var previousEndMs = previous.EndPosition.GetPositionMs(beats);
                    var currentMs = current.Position.GetPositionMs(beats);
                    var previousMs = previous.Position.GetPositionMs(beats);

                    if (currentMs - previousMs < 0.5 || currentMs < previousEndMs - 2.0)
                    {
                        sink.Add(ExportFindingSeverity.Error, KeyOverlappingNotes, currentMs,
                            $"string {current.StringIndex}");
                        break; // one finding per string per level is enough
                    }
                }
            }
        }
    }

    private static void CheckPhrases(Arrangement arrangement, IList<BeatMarker> beats, ArrangementFindings sink)
    {
        var iterations = arrangement.PhraseIterations;
        // BOTH phrase stores (PhraseMarkers): an editor-authored chart keeps its phrases on
        // EventPoint.Phrase, and reading only Phrases/PhraseIterations called it phrase-less.
        if (arrangement.Phrases.Count == 0 && PhraseMarkers.All(arrangement).Count == 0)
        {
            sink.Add(ExportFindingSeverity.Warning, KeyNoPhrases);
            return;
        }

        if (arrangement.Phrases.Count > MaxPhrases)
        {
            sink.Add(ExportFindingSeverity.Error, KeyTooManyPhrases, null,
                arrangement.Phrases.Count.ToString());
        }

        // A phrase is USED if an iteration references it — OR if an event
        // point on the timeline names it.
        //
        // Only the second half matches how this app is edited. Nothing in
        // the editor writes `PhraseIterations`: placing a phrase writes
        // `EventPoint.Phrase`, and the id/iteration pair is not built until
        // `PhraseSectionGenerator` runs during export. Meanwhile the GP and
        // EOF importers DO fill `Phrases` (GpArrangementParser:
        // "if (!arr.Phrases.Contains(...)) arr.Phrases.Add(...)") without
        // ever adding an iteration.
        //
        // So an imported chart arrives with a full name list and an empty
        // iteration list, and this loop called every phrase in it unused —
        // "Phrase 'intro' is never used", for phrases visibly placed all
        // over the timeline. It was comparing two lists the editor never
        // maintains while ignoring the one it does.
        var placed = new HashSet<string>(
            arrangement.EventPoints
                .Select(e => e.Phrase)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n.Trim()),
            StringComparer.OrdinalIgnoreCase);

        for (var phraseId = 0; phraseId < arrangement.Phrases.Count; phraseId++)
        {
            var phraseName = arrangement.Phrases[phraseId];
            if (IsStructuralPhrase(phraseName)) continue;
            var id = phraseId;
            if (iterations.Any(it => it.PhraseId == id)) continue;
            if (!string.IsNullOrWhiteSpace(phraseName) && placed.Contains(phraseName.Trim())) continue;

            sink.Add(ExportFindingSeverity.Warning, KeyEmptyPhrase, null, phraseName);
        }

        if (iterations.Count == 0) return;

        var firstIterationMs = iterations.Min(it => it.Position).GetPositionMs(beats);
        var firstContentMs = ContentTimesMs(arrangement, beats).DefaultIfEmpty(double.MaxValue).Min();
        if (firstContentMs < firstIterationMs - 0.5)
            sink.Add(ExportFindingSeverity.Error, KeyFirstPhraseNotAtStart, firstContentMs);
    }

    private static void CheckTones(ChartProject project, Arrangement arrangement, IList<BeatMarker> beats, ArrangementFindings sink)
    {
        var (toneBase, slots) = RsToneSlots.Resolve(arrangement);
        if (string.IsNullOrWhiteSpace(toneBase))
            sink.Add(ExportFindingSeverity.Warning, KeyMissingBaseTone);

        var distinctTones = arrangement.ToneChanges
            .Select(t => t.ToneName)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct()
            .ToList();

        if (distinctTones.Count > 4)
            sink.Add(ExportFindingSeverity.Error, KeyTooManyTones, null, distinctTones.Count.ToString());

        var unslotted = arrangement.ToneChanges.FirstOrDefault(change =>
            string.IsNullOrWhiteSpace(change.ToneName) || !slots.Contains(change.ToneName));
        if (unslotted != null)
        {
            sink.Add(ExportFindingSeverity.Warning, KeyToneWithoutSlot,
                unslotted.Position.GetPositionMs(beats), unslotted.ToneName);
        }

        // A declared tone without a project-owned DEFINITION still exports
        // (name only) but plays as the game's default tone — flag each one
        // so the charter knows to author or import the body.
        foreach (var missing in MissingToneDefinitions(project, toneBase, slots))
            sink.Add(ExportFindingSeverity.Warning, KeyToneNotDefined, null, missing);
    }

    // Stock content and RS Extended Range share one domain string-count gate.
    // Extended content carries a warning naming its package requirements. A sound past
    // the arrangement's own strings stays an Error (index <= 7 for 8 strings, <= 5 for a 6-string bass).
    private static void CheckFretsAndStrings(Arrangement arrangement, IList<BeatMarker> beats, ArrangementFindings sink, bool sixStringProjection)
    {
        var maxStringIndex = SngStringLimits.MaxNoteStringIndex(arrangement);
        var strings = arrangement.StringCount.ToString(CultureInfo.InvariantCulture);
        if (Rs7StringLayout.IsExtendedGuitar(arrangement))
            sink.Add(ExportFindingSeverity.Warning, KeyExtendedRangeGuitar, null, strings);
        else if (Rs7StringLayout.IsExtendedBass(arrangement))
            sink.Add(ExportFindingSeverity.Warning, KeyExtendedRangeBass, null, strings);
        if (RsxrStringLayout.IsExtended(arrangement) && RsxrStringLayout.ExportRefusal(arrangement) is { } refusal)
            sink.Add(ExportFindingSeverity.Warning, KeyExtendedRangeTuning, null, refusal);
        if (sixStringProjection && Rs7Projection.TryCreate(arrangement, sixStringOnly: true)?.Loss is { IsEmpty: false } loss)
            sink.Add(ExportFindingSeverity.Warning, KeySixStringDrop, null, loss.Detail(arrangement));

        foreach (var level in arrangement.Levels)
        {
            foreach (var note in level.Notes)
                CheckSound(arrangement, sink, note.Position.GetPositionMs(beats), note, maxStringIndex);

            foreach (var chord in level.Chords)
            {
                foreach (var chordNote in chord.ChordNotes)
                    CheckSound(arrangement, sink, chord.Position.GetPositionMs(beats), chordNote, maxStringIndex);
            }
        }
    }

    /// <summary>
    ///     The widths the song file fixes (<see cref="SngFormatLimits" />): the difficulty levels, the notes
    ///     and chords one level can number, and the strings the package can spell. The mapper refuses each
    ///     on its own; this makes it a finding the charter sees before the export starts.
    /// </summary>
    private static void CheckFormatLimits(Arrangement arrangement, ArrangementFindings sink)
    {
        if (arrangement.Levels.Count > SngFormatLimits.MaxLevels)
        {
            sink.Add(ExportFindingSeverity.Error, KeyTooManyLevels, null,
                arrangement.Levels.Count.ToString(CultureInfo.InvariantCulture));
        }

        var busiest = arrangement.Levels.Select(l => l.Notes.Count + l.Chords.Count).DefaultIfEmpty(0).Max();
        if (busiest > SngFormatLimits.MaxSoundsPerLevel)
            sink.Add(ExportFindingSeverity.Error, KeyTooManyNotes, null, busiest.ToString(CultureInfo.InvariantCulture));

        if (Rs7StringLayout.HasUnsupportedStringCount(arrangement))
        {
            sink.Add(ExportFindingSeverity.Error, KeyStringCountUnsupported, null,
                arrangement.StringCount.ToString(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>A chord template's frets and fingers are written into signed bytes: a value outside
    /// the playable range would wrap there, so it is an error rather than a clamp nobody sees.</summary>
    private static void CheckChordTemplates(Arrangement arrangement, ArrangementFindings sink)
    {
        foreach (var template in arrangement.ChordTemplates)
        {
            foreach (var fret in template.Frets.Values.Where(f => f is < -1 or > MaxFret).Distinct())
                sink.Add(ExportFindingSeverity.Error, KeyFretOutOfRange, null, fret.ToString(CultureInfo.InvariantCulture));

            foreach (var finger in template.Fingers.Values.Where(f => f is < -1 or > SngFormatLimits.MaxFinger).Distinct())
                sink.Add(ExportFindingSeverity.Error, KeyFingerOutOfRange, null, finger.ToString(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    ///     What a Guitar Pro export of one level needs of the chart: every sound on a string the
    ///     arrangement's tuning has. Narrower than <see cref="Validate" /> on purpose: Guitar Pro has
    ///     no phrase, anchor or difficulty-ladder rules, and frets above the Rocksmith 24 are legal there.
    /// </summary>
    public IReadOnlyList<ExportValidationFinding> ValidateGuitarPro(
        Arrangement arrangement, ChartLevel level, IList<BeatMarker> beats)
    {
        var findings = new List<ExportValidationFinding>();
        var sink = new ArrangementFindings(arrangement.Name, findings);

        foreach (var note in level.Notes)
            CheckTuningString(arrangement, sink, note.Position.GetPositionMs(beats), note);

        foreach (var chord in level.Chords)
        {
            foreach (var chordNote in chord.ChordNotes)
                CheckTuningString(arrangement, sink, chord.Position.GetPositionMs(beats), chordNote);
        }

        return findings;
    }

    private static void CheckTuningString(Arrangement arrangement, ArrangementFindings sink, double timeMs, IGuitarSound sound)
    {
        if (sound.StringIndex < 0 || (arrangement.StringCount > 0 && sound.StringIndex >= arrangement.StringCount))
        {
            sink.Add(ExportFindingSeverity.Error, KeyStringBeyondTuning, timeMs,
                sound.StringIndex.ToString(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    ///     One note or chord note's fret and string. A NON-ZERO fret below the capo
    ///     is physically unplayable (the capo already stops the string higher up), and
    ///     a sound's pitch derives from max(fret, capo), so the chart says one thing and
    ///     the game plays another; the inspector flags it per sound as L01, so a chord
    ///     note gets the same rule as a single note.
    ///     <para>
    ///         A sound past the strings the package can carry is not merely dropped by
    ///         the SNG mapper — it is FOLDED onto the last one (Math.Clamp), where it
    ///         overwrites that string's real data. Refuse the export instead: an Error
    ///         blocks the dialog (CanExport) and makes the save companion skip, so the
    ///         fold never reaches a package.
    ///     </para>
    /// </summary>
    private static void CheckSound(Arrangement arrangement, ArrangementFindings sink, double timeMs, IGuitarSound sound,
        int maxStringIndex)
    {
        if (sound.Fret is < 0 or > MaxFret)
            sink.Add(ExportFindingSeverity.Error, KeyFretOutOfRange, timeMs, sound.Fret.ToString());
        else if (arrangement.Capo > 0 && sound.Fret != 0 && sound.Fret < arrangement.Capo)
            sink.Add(ExportFindingSeverity.Error, KeyNoteBelowCapo, timeMs, sound.Fret.ToString());

        if (sound.StringIndex < 0 || sound.StringIndex > maxStringIndex)
            sink.Add(ExportFindingSeverity.Error, KeyStringOutOfRange, timeMs, sound.StringIndex.ToString());
        else
            CheckTuningString(arrangement, sink, timeMs, sound);
    }

    private void ValidateVocals(ChartProject project, VocalTrack track, List<ExportValidationFinding> findings)
    {
        CheckLineOverlap(project, track, findings);
        foreach (var phrase in track.Phrases)
        {
            CheckLyricOrder(project, track, phrase, findings);
            for (var i = 0; i < phrase.Lyrics.Count; i++)
            {
                var lyric = phrase.Lyrics[i];
                // "-"/"+" markers are appended on export, reserve one byte.
                var byteCount = Encoding.UTF8.GetByteCount(lyric.Text.TrimEnd(' ')) + 1;
                if (byteCount > MaxLyricBytes)
                {
                    findings.Add(new ExportValidationFinding(
                    track.Name,
                    ExportFindingSeverity.Error,
                    KeyLyricTooLong,
                    lyric.Position.GetPositionMs(project.Beats),
                    lyric.Text));
                }

                CheckMarkerText(project, track, phrase, i, findings);
            }
        }
    }

    /// <summary>
    ///     Text that literally ends in a structure marker without the structure it stands for
    ///     cannot survive the vocals XML: RS2014 has no escape, so the reader takes a trailing
    ///     <c>+</c> as a line end and a trailing <c>-</c> as a word part. "a+" inside a line
    ///     comes back as a new line break; "a-" that joins no word comes back joined to the next
    ///     syllable, and a word part "a-" comes back as "a". A line end is safe — the writer
    ///     appends its own <c>+</c> after the text, by the writer's own line rule (the flag or the
    ///     last syllable of the phrase). The check asks the encoder itself whether the written
    ///     text reads back, so the two cannot drift. An ERROR, as the game would show other lines
    ///     or other text than the editor.
    /// </summary>
    private static void CheckMarkerText(ChartProject project, VocalTrack track, LyricPhrase phrase, int index,
        List<ExportValidationFinding> findings)
    {
        if (!RsVocalLyricEncoding.LosesText(phrase, index)) return;
        var lyric = phrase.Lyrics[index];
        findings.Add(new ExportValidationFinding(track.Name, ExportFindingSeverity.Error, KeyLyricMarkerText,
            lyric.Position.GetPositionMs(project.Beats), lyric.Text));
    }

    /// <summary>
    ///     A line whose syllables are not in time order cannot ship: the package sorts every
    ///     vocal by time, so the game would show different lines than the editor. An ERROR.
    /// </summary>
    private static void CheckLyricOrder(ChartProject project, VocalTrack track, LyricPhrase phrase,
        List<ExportValidationFinding> findings)
    {
        for (var i = 1; i < phrase.Lyrics.Count; i++)
        {
            if (phrase.Lyrics[i].Position >= phrase.Lyrics[i - 1].Position) continue;
            findings.Add(new ExportValidationFinding(track.Name, ExportFindingSeverity.Error, KeyLyricsOutOfOrder,
                phrase.Lyrics[i].Position.GetPositionMs(project.Beats),
                string.Join(" ", phrase.Lyrics.Select(l => l.Text))));
            return;
        }
    }

    /// <summary>
    ///     Two lines that overlap in time cannot ship either: the package carries one vocal track
    ///     and sorts its vocals by time, so the syllables of the two lines would interleave, while
    ///     the vocals XML keeps each line together in the order of its first syllable. The same
    ///     ERROR, reported on the later line.
    /// </summary>
    private static void CheckLineOverlap(ChartProject project, VocalTrack track, List<ExportValidationFinding> findings)
    {
        FractionalPosition? latest = null;
        foreach (var phrase in track.Phrases.Where(p => p.Lyrics.Count > 0).OrderBy(p => p.Lyrics.Min(l => l.Position)))
        {
            var first = phrase.Lyrics.Min(l => l.Position);
            if (latest.HasValue && first < latest.Value)
            {
                findings.Add(new ExportValidationFinding(track.Name, ExportFindingSeverity.Error, KeyLyricsOutOfOrder,
                    first.GetPositionMs(project.Beats), string.Join(" ", phrase.Lyrics.Select(l => l.Text))));
            }

            var last = phrase.Lyrics.Max(l => l.Position);
            if (!latest.HasValue || last > latest.Value) latest = last;
        }
    }

    // ---- tone definitions ----------------------------------------------------

    /// <summary>Referenced tone keys (base + slots) that have no matching
    /// <see cref="ChartProject.ToneDefinitions"/> entry. Matching is trim- and
    /// case-tolerant like the in-game-tone coordinator's key normalization.</summary>
    internal static IEnumerable<string> MissingToneDefinitions(
        ChartProject project, string toneBase, string[] slots)
    {
        var defined = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tone in project.ToneDefinitions)
        {
            if (!string.IsNullOrWhiteSpace(tone.Key)) defined.Add(tone.Key.Trim());
            if (!string.IsNullOrWhiteSpace(tone.Name)) defined.Add(tone.Name.Trim());
        }

        return slots.Prepend(toneBase)
            .Where(tone => !string.IsNullOrWhiteSpace(tone))
            .Select(tone => tone.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(tone => !defined.Contains(tone));
    }

    /// <summary>Counts the referenced tone keys across every instrumental
    /// arrangement and how many have a project-owned definition body — the
    /// "defined vs missing" summary shown next to the tone tooling.</summary>
    public static (int Referenced, int Defined) CountToneDefinitions(ChartProject project)
    {
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var arrangement in project.Arrangements)
        {
            if (arrangement.ArrangementType == ArrangementType.Vocal) continue;
            if (arrangement.ToneChanges.Count == 0 &&
                string.IsNullOrWhiteSpace(arrangement.ToneBase) &&
                string.IsNullOrWhiteSpace(arrangement.StartingTone))
            {
                continue;
            }

            var (toneBase, slots) = RsToneSlots.Resolve(arrangement);
            foreach (var tone in slots.Prepend(toneBase))
            {
                if (!string.IsNullOrWhiteSpace(tone))
                    referenced.Add(tone.Trim());
            }

            foreach (var tone in MissingToneDefinitions(project, toneBase, slots))
                missing.Add(tone);
        }

        return (referenced.Count, referenced.Count - missing.Count);
    }

    private static bool IsStructuralPhrase(string name)
    {
        return name.Equals("COUNT", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("END", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("mover", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<double> ContentTimesMs(Arrangement arrangement, IList<BeatMarker> beats)
    {
        return arrangement.Levels.SelectMany(level =>
            level.Notes.Select(n => n.Position.GetPositionMs(beats))
                .Concat(level.Chords.Select(c => c.Position.GetPositionMs(beats))));
    }

    private static double? FirstContentTimeMs(ChartLevel level, IList<BeatMarker> beats)
    {
        var times = level.Notes.Select(n => n.Position.GetPositionMs(beats))
            .Concat(level.Chords.Select(c => c.Position.GetPositionMs(beats)))
            .ToList();
        return times.Count > 0 ? times.Min() : null;
    }
}
