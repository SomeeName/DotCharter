using System.Collections.ObjectModel;
using System.Globalization;
using System.Xml.Linq;
using DotCharter.Services.Editing.ChordShapes;
using DotCharter.Services.Export.Rocksmith;

namespace DotCharter.Services.Parsing.Xml.Song;

/// <summary>
/// Reads an RS-XML <c>&lt;song&gt;</c> document into a <see cref="ChartProject"/>.
/// Mirrors rscharter's <c>RSXMLToArrangement</c> / <c>RSXMLLevelTransformer</c>.
/// </summary>
internal sealed class RsXmlSongReader
{
    /// <summary>
    ///     Give back the chords EOF wrote as single notes because they are marked
    ///     <see cref="Chord.SplitIntoNotes" /> — simultaneous notes inside no hand shape
    ///     and with no link landing on them (<see cref="RsXmlSplitChordMerge" />). On
    ///     for an IMPORT (<c>RsXmlImporter</c>, <c>XmlChartSerializer</c>); off for a
    ///     DotCharter v2 project's own arrangement files (<c>RscpMigrations</c>), whose
    ///     loose notes the charter wrote as notes.
    /// </summary>
    public bool MergeSimultaneousNotes { get; init; }

    public ChartProject Read(string filePath)
    {
        var doc = SafeXml.LoadFromFile(filePath);
        return ReadDocument(doc, filePath);
    }

    /// <summary>Widest instrument the model supports. For clamping a
    /// file-supplied string count before it drives an allocation loop — this
    /// reader parses untrusted downloads.</summary>
    private const int MaxStrings = 12;

    public ChartProject ReadDocument(XDocument doc, string filePath)
    {
        var project = new ChartProject();
        project.Chart.SongName = Path.GetFileNameWithoutExtension(filePath);

        var song = doc.Root;
        if (song == null) return project;

        // moveR<N> phrases move to their note BEFORE anything reads a time, exactly as
        // DLC Builder would build them, and lose the name that would move them twice.
        RsXmlPhraseMover.ResolveForImport(doc);

        // ── Title / artist / album ──────────────────────────────────────
        project.Chart.SongName = song.Element(E.Title)?.Value ?? project.Chart.SongName;

        var arr = new Arrangement();
        project.Arrangements.Add(arr);

        // ArrangementType
        var arrTypeName = song.Element(E.Arrangement)?.Value ?? "Lead";
        arr.ArrangementType = ParseArrangementType(arrTypeName);

        // Read metadata into both Arrangement fields and ChartData
        var artistName = song.Element(E.ArtistName)?.Value ?? "";
        var artistNameSort = song.Element(E.ArtistNameSort)?.Value ?? artistName;
        var albumName = song.Element(E.AlbumName)?.Value ?? "";
        var albumYearStr = song.Element(E.AlbumYear)?.Value;
        arr.ArtistName = artistName;
        arr.ArtistNameSort = artistNameSort;
        arr.AlbumName = albumName;
        if (int.TryParse(albumYearStr, CultureInfo.InvariantCulture, out var albumYear)) arr.AlbumYear = albumYear;

        project.Chart.Artist = artistName;
        project.Chart.ArtistSort = artistNameSort;
        project.Chart.AlbumName = albumName;
        if (albumYearStr != null) project.Chart.AlbumYear = albumYearStr;

        // Capo / centOffset
        arr.Capo = int.TryParse(song.Element(E.Capo)?.Value, CultureInfo.InvariantCulture, out var capo) ? capo : 0;
        if (int.TryParse(song.Element(E.CentOffset)?.Value, CultureInfo.InvariantCulture, out var cent)) arr.CentOffset = cent;

        // ── Tuning ──────────────────────────────────────────────────────
        ReadTuning(song, arr);

        // ── ArrangementProperties ───────────────────────────────────────
        var propsEl = song.Element(E.ArrangementProperties);
        if (propsEl != null)
        {
            ReadArrangementProperties(propsEl, arr.Properties);

            // represent/bonusArr fully encode Main/Bonus/Alternate and the WRITER
            // derives them from Subtype — but the reader left them in Properties
            // only, so Subtype came back as its default. That is not cosmetic:
            // ProjectDocumentService.BuildArrangementFileName bakes Subtype into the
            // exported filename, so a bonus arrangement re-imported as Main and the
            // next export OVERWROTE the main arrangement's XML file.
            arr.Subtype = arr.Properties.BonusArr != 0
                ? ArrangementSubtype.Bonus
                : arr.Properties.Represent != 0
                    ? ArrangementSubtype.Main
                    : ArrangementSubtype.Alternate;

            // Same class of bug as Subtype above: bassPick was left in Properties
            // only, so the field the writer and the manifest both read stayed false
            // and an imported picked-bass arrangement lost the flag on the way out.
            arr.PickedBass = arr.Properties.BassPick != 0;
        }

        // ── Tones ───────────────────────────────────────────────────────
        arr.ToneBase = song.Element(E.Tonebase)?.Value ?? "";
        arr.ToneA = song.Element(E.Tonea)?.Value ?? "";
        arr.ToneB = song.Element(E.Toneb)?.Value ?? "";
        arr.ToneC = song.Element(E.Tonec)?.Value ?? "";
        arr.ToneD = song.Element(E.Toned)?.Value ?? "";
        arr.StartingTone = arr.ToneBase;

        // ebeats → project.Beats
        ReadEbeats(song, project);
        // …and the meter, which lives in the EVENT stream rather than on the
        // ebeats. Must run before anything derives a position from the grid.
        ApplyTimeSignatureEvents(song, project.Beats);

        var beats = project.Beats;

        // Tone changes
        var tonesEl = song.Element(E.Tones);
        if (tonesEl != null)
        {
            foreach (var toneEl in tonesEl.Elements(E.Tone))
            {
                var toneMs = Fmt.ParseTime(toneEl.Attribute(E.Time)?.Value);
                var toneName = toneEl.Attribute(E.Name)?.Value ?? "";
                arr.ToneChanges.Add(new ToneChange
                {
                    Position = TimelineMath.PositionFromImportedMs(toneMs, beats),
                    ToneName = toneName
                });
            }
        }

        // ── Phrases ─────────────────────────────────────────────────────
        ReadPhrases(song, arr);

        // ── PhraseIterations ────────────────────────────────────────────
        ReadPhraseIterations(song, arr, beats);

        // ── ChordTemplates ──────────────────────────────────────────────
        ReadChordTemplates(song, E.ChordTemplates, arr.ChordTemplates);
        ReadChordTemplates(song, E.FretHandMuteTemplates, arr.FretHandMuteTemplates);

        // ── Sections → EventPoints ──────────────────────────────────────
        // One index shared by both readers: a
        // LINEAR scan of a growing list per <section> AND per <event> would cost an
        // EOF-exported chart with thousands of events millions of
        // comparisons on every load. Normalized FractionalPositions are
        // documented as safe dictionary keys.
        var eventPointsByPosition = new Dictionary<FractionalPosition, EventPoint>();
        ReadSections(song, arr, beats, eventPointsByPosition);

        // ── Events → EventPoints ────────────────────────────────────────
        ReadEvents(song, arr, beats, eventPointsByPosition);

        // ── Levels ──────────────────────────────────────────────────────
        ReadLevels(song, arr, beats);
        RsXmlSongStringOrder.ToChart(arr);

        if (MergeSimultaneousNotes) RsXmlSplitChordMerge.Apply(project);

        // A hand-shape template fret nothing under the shape plays is a GHOST gem —
        // RS XML has no other spelling for one, and EOF writes exactly this.
        GhostGems.MarkUnplayedHandShapeFrets(arr);

        return project;
    }

    // ── Parsing helpers ──────────────────────────────────────────────────

    private static ArrangementType ParseArrangementType(string name) => name.ToLowerInvariant() switch
    {
        "lead" => ArrangementType.Lead,
        "rhythm" => ArrangementType.Rhythm,
        "bass" => ArrangementType.Bass,
        "combo" => ArrangementType.Combo,
        "vocal" => ArrangementType.Vocal,
        _ => ArrangementType.Lead
    };

    private static void ReadTuning(XElement song, Arrangement arr)
    {
        var tuningEl = song.Element(E.Tuning);
        if (tuningEl == null) return;

        // Detect string count: explicit `strings` attr (rscharter / DotCharter legacy)
        // or fallback to counting present stringN attributes (EOF style).
        var strings = Fmt.AttrIntNullable(tuningEl, E.Strings);
        if (!strings.HasValue || strings.Value <= 0)
        {
            var max = -1;
            for (var i = 0; i < 16; i++)
                if (tuningEl.Attribute($"string{i}") != null) max = i;
            strings = max >= 0 ? max + 1 : 6;
        }

        // Clamp to a real instrument range before it drives an allocation loop.
        // This value comes straight out of an untrusted file (a downloaded CDLC,
        // a psarc-embedded arrangement), and `<tuning strings="2000000000"/>` is
        // 200 bytes that spun two billion iterations. The rscp parser has
        // clamped the same field for exactly this reason; this reader did not.
        arr.StringCount = Math.Clamp(strings.Value, 1, MaxStrings);
        arr.TuningPitches.Clear();
        var fileOffsets = new int[arr.StringCount];
        for (var i = 0; i < fileOffsets.Length; i++)
            fileOffsets[i] = Fmt.AttrInt(tuningEl, $"string{i}");
        // A 7-/8-string guitar's file lists the extra strings after the stock six.
        foreach (var offset in RsXmlSongStringOrder.TuningToChart(arr, fileOffsets))
            arr.TuningPitches.Add(new TuningOffset(offset));

        // A chart states its tuning TWICE — offsets AND note letters (see
        // TuningOffsetResolution's remarks) — and this reader filled only the
        // offsets, so an imported arrangement kept Arrangement's DEFAULT
        // {E,A,D,G,B,e} whatever the file said. Every other importer syncs both
        // (GpArrangementParser, GpLegacyImporter, SongsterrConverter,
        // RscpArrangementParser, the package reader's SNG mapper); this one was the outlier.
        // The letters drive the lane gutter labels, the tuner, the tuning chip,
        // ChordNameSuggester and the sheet export — and on a 4-string bass the
        // stale six-entry default also named two strings that do not exist.
        arr.Tuning = TuningUtilities.GetTuningNames(arr.StringCount,
            arr.TuningPitches.Select(t => t.Step).ToArray());
    }

    private static void ReadArrangementProperties(XElement el, ArrangementProperties p)
    {
        p.Represent = Fmt.AttrInt(el, E.Represent, 1);
        p.BonusArr = Fmt.AttrInt(el, E.BonusArr);
        p.StandardTuning = Fmt.AttrInt(el, E.StandardTuning);
        p.NonStandardChords = Fmt.AttrInt(el, E.NonStandardChords);
        p.BarreChords = Fmt.AttrInt(el, E.BarreChords);
        p.PowerChords = Fmt.AttrInt(el, E.PowerChords);
        p.DropDPower = Fmt.AttrInt(el, E.DropDPower);
        p.OpenChords = Fmt.AttrInt(el, E.OpenChords);
        p.FingerPicking = Fmt.AttrInt(el, E.FingerPicking);
        p.PickDirection = Fmt.AttrInt(el, E.PickDirection);
        p.DoubleStops = Fmt.AttrInt(el, E.DoubleStops);
        p.PalmMutes = Fmt.AttrInt(el, E.PalmMutes);
        p.Harmonics = Fmt.AttrInt(el, E.Harmonics);
        p.PinchHarmonics = Fmt.AttrInt(el, E.PinchHarmonics);
        p.Hopo = Fmt.AttrInt(el, E.Hopo);
        p.Tremolo = Fmt.AttrInt(el, E.Tremolo);
        p.Slides = Fmt.AttrInt(el, E.Slides);
        p.UnpitchedSlides = Fmt.AttrInt(el, E.UnpitchedSlides);
        p.Bends = Fmt.AttrInt(el, E.Bends);
        p.Tapping = Fmt.AttrInt(el, E.Tapping);
        p.Vibrato = Fmt.AttrInt(el, E.Vibrato);
        p.FretHandMutes = Fmt.AttrInt(el, E.FretHandMutes);
        p.SlapPop = Fmt.AttrInt(el, E.SlapPop);
        p.TwoFingerPicking = Fmt.AttrInt(el, E.TwoFingerPicking);
        p.FifthsAndOctaves = Fmt.AttrInt(el, E.FifthsAndOctaves);
        p.Syncopation = Fmt.AttrInt(el, E.Syncopation);
        p.BassPick = Fmt.AttrInt(el, E.BassPick);
        p.Sustain = Fmt.AttrInt(el, E.Sustain);
        p.PathLead = Fmt.AttrInt(el, E.PathLead);
        p.PathRhythm = Fmt.AttrInt(el, E.PathRhythm);
        p.PathBass = Fmt.AttrInt(el, E.PathBass);
    }

    private static void ReadEbeats(XElement song, ChartProject project)
    {
        var ebeatsEl = song.Element(E.Ebeats);
        if (ebeatsEl == null) return;

        var ebeatEls = ebeatsEl.Elements(E.Ebeat).ToList();
        for (var i = 0; i < ebeatEls.Count; i++)
        {
            var eb = ebeatEls[i];
            var timeMs = Fmt.ParseTime(eb.Attribute(E.Time)?.Value);
            var measureAttr = eb.Attribute(E.Measure)?.Value;
            var isMeasure = !string.IsNullOrEmpty(measureAttr);

            var bpm = 120.0;
            if (i + 1 < ebeatEls.Count)
            {
                var nextMs = Fmt.ParseTime(ebeatEls[i + 1].Attribute(E.Time)?.Value);
                var diff = nextMs - timeMs;
                if (diff > 0) bpm = 60000.0 / diff;
            }
            else if (i > 0)
            {
                var prevMs = Fmt.ParseTime(ebeatEls[i - 1].Attribute(E.Time)?.Value);
                var diff = timeMs - prevMs;
                if (diff > 0) bpm = 60000.0 / diff;
            }

            project.Beats.Add(new BeatMarker
            {
                Time = timeMs,
                IsMeasure = isMeasure,
                Bpm = bpm
            });
        }
    }

    /// <summary>
    ///     Fills each <see cref="BeatMarker" />'s meter from the <c>TS:n/d</c>
    ///     event stream — the only place RS-XML records a time signature.
    ///     <para>
    ///         Without this every <c>.xml</c> import was silently 4/4, whatever the
    ///         file said. <see cref="ReadEbeats" /> fills Time / IsMeasure / Bpm and
    ///         nothing read the events back, so
    ///         <see cref="BeatMarker.BeatsInMeasure" /> and
    ///         <see cref="BeatMarker.NoteDenominator" /> fell to their 4/4 defaults
    ///         — and they are load-bearing on the way OUT: <c>RsXmlSongWriter</c>
    ///         re-emits <c>TS:4/4</c>, <c>SngFromDomainMapper</c> ships 4/4 into the
    ///         psarc, <c>GpExporter</c> draws bar lines every 4 beats and
    ///         <c>ChartSpacingEnforcer.GetBeatsInMeasure</c> computes min-note
    ///         distance off 4. The SAME song imported from a psarc came in with the
    ///         real meter (the package reader's SNG mapper), so the two paths
    ///         disagreed. The corpus proves the events are really there: 468
    ///         <c>TS:</c> events across <c>TestData/EOF</c>, in eleven distinct
    ///         meters from 2/4 to 11/8.
    ///     </para>
    ///     <para>
    ///         A change applies from its own time onward, which is exactly what the
    ///         writer emits (one event per beat whose meter differs from the one
    ///         before it). Beats ahead of the first event keep the 4/4 default.
    ///     </para>
    /// </summary>
    private static void ApplyTimeSignatureEvents(XElement song, IList<BeatMarker> beats)
    {
        if (beats.Count == 0) return;
        var eventsEl = song.Element(E.Events);
        if (eventsEl == null) return;

        var changes = new List<(int TimeMs, int Numerator, int Denominator)>();
        foreach (var evEl in eventsEl.Elements(E.Event))
        {
            if (!TryParseTimeSignature(Fmt.AttrStr(evEl, E.Code), out var numerator, out var denominator))
                continue;
            changes.Add((Fmt.ParseTime(evEl.Attribute(E.Time)?.Value), numerator, denominator));
        }

        if (changes.Count == 0) return;
        changes.Sort(static (l, r) => l.TimeMs.CompareTo(r.TimeMs));

        var next = 0;
        var currentNumerator = DefaultBeatsInMeasure;
        var currentDenominator = DefaultNoteDenominator;
        foreach (var beat in beats)
        {
            // Beats are ascending, so one forward walk covers the whole stream.
            while (next < changes.Count && changes[next].TimeMs <= (int)beat.Time)
            {
                currentNumerator = changes[next].Numerator;
                currentDenominator = changes[next].Denominator;
                next++;
            }

            beat.BeatsInMeasure = currentNumerator;
            beat.NoteDenominator = currentDenominator;
        }
    }

    /// <summary>
    ///     <c>TS:n/d</c> → the pair, or false for any other event code. Bounded
    ///     because both halves size a bar and drive emit loops downstream, and this
    ///     reader parses untrusted downloads — the same reason
    ///     <see cref="ReadTuning" /> clamps its string count.
    /// </summary>
    private static bool TryParseTimeSignature(string code, out int numerator, out int denominator)
    {
        numerator = DefaultBeatsInMeasure;
        denominator = DefaultNoteDenominator;
        if (!code.StartsWith("TS:", StringComparison.OrdinalIgnoreCase)) return false;

        var parts = code[3..].Split('/');
        if (parts.Length != 2) return false;
        if (!int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ||
            !int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var d))
        {
            return false;
        }

        numerator = Math.Clamp(n, 1, MaxBeatsInMeasure);
        denominator = Math.Clamp(d, 1, MaxNoteDenominator);
        return true;
    }

    private const int DefaultBeatsInMeasure = 4;
    private const int DefaultNoteDenominator = 4;
    private const int MaxBeatsInMeasure = 32;
    private const int MaxNoteDenominator = 64;

    private static void ReadPhrases(XElement song, Arrangement arr)
    {
        var phrasesEl = song.Element(E.Phrases);
        if (phrasesEl == null) return;

        foreach (var phraseEl in phrasesEl.Elements(E.Phrase))
        {
            var name = Fmt.AttrStr(phraseEl, E.Name);
            var maxDiff = Fmt.AttrInt(phraseEl, E.MaxDifficulty);
            var solo = Fmt.AttrIntNullable(phraseEl, E.Solo) == 1;
            arr.Phrases.Add(name);
            // Every phrase, max 0 included: "no PhraseInfo" means FULLY levelled
            // (LiveNoteMatcher, LevelSquashService, RsLevelLadder), so dropping a
            // stated 0 turned a phrase that stops at level 0 into one that
            // reaches the top — and the ladder fill then left it out of every
            // level above 0, i.e. out of the hardest one.
            arr.PhraseInfos[name] = new PhraseInfo(maxDiff, solo);
        }
    }

    private static void ReadPhraseIterations(XElement song, Arrangement arr, IList<BeatMarker> beats)
    {
        var iterEl = song.Element(E.PhraseIterations);
        if (iterEl == null) return;

        foreach (var piEl in iterEl.Elements(E.PhraseIteration))
        {
            var timeMs = Fmt.ParseTime(piEl.Attribute(E.Time)?.Value);
            var phraseId = Fmt.AttrInt(piEl, E.PhraseId);
            var pos = TimelineMath.PositionFromImportedMs(timeMs, beats);
            arr.PhraseIterations.Add(new PhraseIteration(pos, phraseId));
        }
    }

    private static void ReadChordTemplates(XElement song, string listName,
        ObservableCollection<ChordTemplate> target)
    {
        var templatesEl = song.Element(listName);
        if (templatesEl == null) return;

        var id = 0;
        foreach (var ctEl in templatesEl.Elements(E.ChordTemplate))
        {
            var ct = new ChordTemplate
            {
                Id = id++,
                Name = Fmt.AttrStr(ctEl, E.ChordName)
            };

            var displayName = Fmt.AttrStr(ctEl, E.DisplayName, ct.Name);
            if (displayName.EndsWith("-arp", StringComparison.OrdinalIgnoreCase))
                ct.ForceArpeggioInRs = true;
            // "-nop" is the ChordMask.Nop flag: the field, never name text.
            // DLC Builder writes it on the displayName; a chordName carrying it
            // is folded too, or the suffix would show as part of the name.
            if (displayName.EndsWith(RsChordDisplaySuffix.Nop, StringComparison.Ordinal))
                ct.NopInRs = true;
            RsChordDisplaySuffix.FoldNop(ct);

            // Read finger{i} and fret{i} attributes
            foreach (var attr in ctEl.Attributes())
            {
                var attrName = attr.Name.LocalName;
                if (attrName.StartsWith("finger", StringComparison.Ordinal) &&
                    int.TryParse(attrName.Substring(6), CultureInfo.InvariantCulture, out var fingerStr))
                {
                    if (int.TryParse(attr.Value, CultureInfo.InvariantCulture, out var fv) &&
                        ChordTemplate.FingerNames.ContainsKey(fv))
                    {
                        ct.Fingers[fingerStr] = fv;
                    }
                }
                else if (attrName.StartsWith("fret", StringComparison.Ordinal) &&
                         int.TryParse(attrName.Substring(4), CultureInfo.InvariantCulture, out var fretStr))
                {
                    // `fret{i}="-1"` means "this string is UNUSED", and it is not
                    // an edge case: Rocksmith2014.XML's ChordTemplate writes one
                    // per unused string whenever UseAbridgedXml is false, which
                    // is DLC Builder's normal output. EOF omits the attribute
                    // entirely, which is the only reason no test has seen this.
                    //
                    // Stored verbatim it is a fret of -1 on a declared string,
                    // and everything downstream believes it: Frets.Count is 6 so
                    // "declares nothing" can never fire, GetLowestFret returns -1
                    // into the anchor and fingering paths, E05 reports "declares
                    // fret -1 there", and ChordTemplateResolver.Resolve's
                    // frets-keyed find-or-create can never match — so every edit
                    // mints a duplicate template. The Fingers branch above has
                    // always filtered its own sentinel; this is the same rule.
                    if (int.TryParse(attr.Value, CultureInfo.InvariantCulture, out var fv) && fv >= 0)
                        ct.Frets[fretStr] = fv;
                }
            }

            target.Add(ct);
        }
    }

    private static void ReadSections(XElement song, Arrangement arr, IList<BeatMarker> beats,
        Dictionary<FractionalPosition, EventPoint> index)
    {
        var sectionsEl = song.Element(E.Sections);
        if (sectionsEl == null) return;

        foreach (var sEl in sectionsEl.Elements(E.Section))
        {
            var name = Fmt.AttrStr(sEl, E.Name);
            var timeMs = Fmt.ParseTime(sEl.Attribute(E.StartTime)?.Value);
            var pos = TimelineMath.PositionFromImportedMs(timeMs, beats);
            var ep = FindOrCreateEventPoint(arr, pos, index);
            ep.Section = name;
        }
    }

    private static void ReadEvents(XElement song, Arrangement arr, IList<BeatMarker> beats,
        Dictionary<FractionalPosition, EventPoint> index)
    {
        var eventsEl = song.Element(E.Events);
        if (eventsEl == null) return;

        foreach (var evEl in eventsEl.Elements(E.Event))
        {
            var code = Fmt.AttrStr(evEl, E.Code);
            var timeMs = Fmt.ParseTime(evEl.Attribute(E.Time)?.Value);
            var pos = TimelineMath.PositionFromImportedMs(timeMs, beats);
            var ep = FindOrCreateEventPoint(arr, pos, index);
            ep.Events = string.IsNullOrEmpty(ep.Events) ? code : ep.Events + "," + code;
        }
    }

    private static EventPoint FindOrCreateEventPoint(Arrangement arr, FractionalPosition pos,
        Dictionary<FractionalPosition, EventPoint> index)
    {
        if (index.TryGetValue(pos, out var existing)) return existing;
        var newEp = new EventPoint { Position = pos };
        arr.EventPoints.Add(newEp);
        index[pos] = newEp;
        return newEp;
    }

    // ── Level reading ─────────────────────────────────────────────────────

    private static void ReadLevels(XElement song, Arrangement arr, IList<BeatMarker> beats)
    {
        var levelsEl = song.Element(E.Levels);
        if (levelsEl == null)
        {
            arr.Levels.Add(new ChartLevel { DifficultyIndex = 0 });
            return;
        }

        foreach (var lvlEl in levelsEl.Elements(E.Level))
        {
            var level = new ChartLevel
            {
                DifficultyIndex = Fmt.AttrInt(lvlEl, E.Difficulty)
            };

            ReadNotes(lvlEl, level, beats);
            ReadChords(lvlEl, level, beats, arr.ChordTemplates);
            ReadFretHandMutes(lvlEl, level, beats);
            ReadAnchors(lvlEl, level, beats);
            ReadHandShapes(lvlEl, level, beats);

            arr.Levels.Add(level);
        }

        if (arr.Levels.Count == 0)
            arr.Levels.Add(new ChartLevel { DifficultyIndex = 0 });

        // The file is sparse (a level carries only the phrases whose max
        // reaches it); the chart is dense, so its hardest level is the whole
        // song at its hardest — the shape the psarc import produces too.
        // RsXmlSongWriter takes the copies back out through the same helper.
        RsLevelLadder.FillInheritedPhrases(arr);
    }

    private static void ReadNotes(XElement lvlEl, ChartLevel level, IList<BeatMarker> beats)
    {
        var notesEl = lvlEl.Element(E.Notes);
        if (notesEl == null) return;

        foreach (var noteEl in notesEl.Elements(E.Note))
            level.Notes.Add(ReadNote(noteEl, beats));
    }

    private static GuitarNote ReadNote(XElement el, IList<BeatMarker> beats)
    {
        var timeMs = Fmt.ParseTime(el.Attribute(E.Time)?.Value);
        var sustainAttr = el.Attribute(E.Sustain)?.Value;
        var sustainMs = sustainAttr != null ? Fmt.ParseTime(sustainAttr) : 0;

        var pos = TimelineMath.PositionFromImportedMs(timeMs, beats);
        var endPos = TimelineMath.PositionFromImportedMs(timeMs + sustainMs, beats);

        var note = new GuitarNote
        {
            Position = pos,
            Sustain = endPos - pos,
            StringIndex = Fmt.AttrInt(el, E.String),
            Fret = Fmt.AttrInt(el, E.Fret),
            Vibrato = Fmt.AttrInt(el, E.VibratoAttr) != 0,
            // The attribute is a STRENGTH (the writer emits it back): keep it.
            VibratoStrength = Fmt.AttrInt(el, E.VibratoAttr),
            Tremolo = Fmt.AttrInt(el, E.TremoloAttr) != 0,
            Accent = Fmt.AttrInt(el, E.Accent) != 0,
            LinkNext = Fmt.AttrInt(el, E.LinkNext) != 0,
            Ignore = Fmt.AttrInt(el, E.Ignore) != 0,
            // Left-hand finger (-1 = none, 0 = thumb) and pick direction (0 down, 1 up).
            LeftHandFinger = Math.Max(-1, Fmt.AttrInt(el, E.LeftHand, -1)),
            PickDirection = ReadPickDirection(el)
        };

        // Mute
        if (Fmt.AttrInt(el, E.Mute) != 0) note.Mute = MuteType.Full;
        else if (Fmt.AttrInt(el, E.PalmMute) != 0) note.Mute = MuteType.Palm;

        // Bass picking
        if (Fmt.AttrInt(el, E.Pluck) != 0) note.BassPicking = BassPickingType.Pop;
        else if (Fmt.AttrInt(el, E.Slap) != 0) note.BassPicking = BassPickingType.Slap;

        // HOPO / tap
        if (Fmt.AttrInt(el, E.HammerOn) != 0) note.Hopo = HopoType.HammerOn;
        else if (Fmt.AttrInt(el, E.PullOff) != 0) note.Hopo = HopoType.PullOff;
        else if (Fmt.AttrInt(el, E.Tap) != 0) note.Hopo = HopoType.Tap;

        // Slide
        var slideToAttr = el.Attribute(E.SlideTo)?.Value;
        var slideUnpitchAttr = el.Attribute(E.SlideUnpitchTo)?.Value;
        if (slideUnpitchAttr != null && int.TryParse(slideUnpitchAttr, CultureInfo.InvariantCulture, out var su))
        { note.SlideTo = su; note.UnpitchedSlide = true; }
        else if (slideToAttr != null && int.TryParse(slideToAttr, CultureInfo.InvariantCulture, out var st))
        {
            note.SlideTo = st;
        }

        // Harmonic
        if (Fmt.AttrInt(el, E.Harmonic) != 0) note.Harmonic = HarmonicType.Normal;
        else if (Fmt.AttrInt(el, E.HarmonicPinch) != 0) note.Harmonic = HarmonicType.Pinch;

        // BendValues
        ReadBendValues(el, note.BendValues, beats);

        return note;
    }

    private static PickDirectionType ReadPickDirection(XElement el)
        => Fmt.AttrInt(el, E.PickDirection, -1) switch
        {
            0 => PickDirectionType.Down,
            1 => PickDirectionType.Up,
            _ => PickDirectionType.None
        };

    private static PickDirectionType ReadStrum(XElement el)
        => el.Attribute(E.Strum)?.Value?.Trim().ToLowerInvariant() switch
        {
            "down" => PickDirectionType.Down,
            "up" => PickDirectionType.Up,
            _ => PickDirectionType.None
        };

    private static void ReadBendValues(XElement noteEl, ObservableCollection<BendValue> target, IList<BeatMarker> beats)
    {
        var bvEl = noteEl.Element(E.BendValues);
        if (bvEl == null) return;

        foreach (var bv in bvEl.Elements(E.BendValue))
        {
            var timeMs = Fmt.ParseTime(bv.Attribute(E.Time)?.Value);
            var stepStr = bv.Attribute(E.Step)?.Value;
            var step = stepStr != null && double.TryParse(stepStr, NumberStyles.Float,
                CultureInfo.InvariantCulture, out var sv) ? sv : 0.0;
            target.Add(new BendValue(TimelineMath.PositionFromImportedMs(timeMs, beats), step));
        }
    }

    private static void ReadChords(XElement lvlEl, ChartLevel level, IList<BeatMarker> beats,
        IList<ChordTemplate> templates)
    {
        var chordsEl = lvlEl.Element(E.Chords);
        if (chordsEl == null) return;

        foreach (var chordEl in chordsEl.Elements(E.Chord))
            level.Chords.Add(ReadChord(chordEl, beats, templates));
    }

    private static Chord ReadChord(XElement el, IList<BeatMarker> beats, IList<ChordTemplate> templates)
    {
        var timeMs = Fmt.ParseTime(el.Attribute(E.Time)?.Value);
        var pos = TimelineMath.PositionFromImportedMs(timeMs, beats);

        var chord = new Chord
        {
            Position = pos,
            TemplateId = Fmt.AttrInt(el, E.ChordId, -1),
            Accent = Fmt.AttrInt(el, E.Accent) != 0,
            Ignore = Fmt.AttrInt(el, E.Ignore) != 0,
            Strum = ReadStrum(el)
        };

        var chordPalmMute = Fmt.AttrInt(el, E.PalmMute) != 0;
        var chordFretHandMute = Fmt.AttrInt(el, E.FretHandMuteAttr) != 0;
        var chordLinkNext = Fmt.AttrInt(el, E.LinkNext) != 0;

        // Read chord notes
        foreach (var cnEl in el.Elements("chordNote"))
        {
            var cn = ReadChordNote(cnEl, timeMs, beats);
            // Propagate chord-level mute / linkNext to each chord note when set.
            if (chordPalmMute && cn.Mute == MuteType.None) cn.Mute = MuteType.Palm;
            if (chordFretHandMute) cn.Mute = MuteType.Full;
            if (chordLinkNext) cn.LinkNext = true;
            chord.ChordNotes.Add(cn);
        }

        // A chord with NO <chordNote> children is Rocksmith's compact repeat
        // (see RsChordDensity) — and it is how every official file spells one,
        // so this is not an edge case. Its shape lives entirely in the template,
        // and reading it as a chord with zero notes gave the editor a strum that
        // draws nothing and exports as nothing.
        if (chord.ChordNotes.Count == 0 && chord.TemplateId >= 0 && chord.TemplateId < templates.Count)
        {
            var template = templates[chord.TemplateId];
            foreach (var (stringIndex, fret) in template.Frets.OrderBy(f => f.Key))
            {
                if (fret < 0) continue;
                var cn = new ChordNote { StringIndex = stringIndex, Fret = fret };
                if (chordPalmMute) cn.Mute = MuteType.Palm;
                if (chordFretHandMute) cn.Mute = MuteType.Full;
                if (chordLinkNext) cn.LinkNext = true;
                chord.ChordNotes.Add(cn);
            }
        }

        return chord;
    }

    private static ChordNote ReadChordNote(XElement el, int chordTimeMs, IList<BeatMarker> beats)
    {
        var sustainAttr = el.Attribute(E.Sustain)?.Value;
        var sustainMs = sustainAttr != null ? Fmt.ParseTime(sustainAttr) : 0;

        var startPos = TimelineMath.PositionFromImportedMs(chordTimeMs, beats);
        var endPos = TimelineMath.PositionFromImportedMs(chordTimeMs + sustainMs, beats);
        var sustainPos = endPos - startPos;
        if (sustainPos < FractionalPosition.Zero) sustainPos = FractionalPosition.Zero;

        var cn = new ChordNote
        {
            StringIndex = Fmt.AttrInt(el, E.String),
            Fret = Fmt.AttrInt(el, E.Fret),
            Sustain = sustainPos,
            Accent = Fmt.AttrInt(el, E.Accent) != 0,
            Vibrato = Fmt.AttrInt(el, E.VibratoAttr) != 0,
            VibratoStrength = Fmt.AttrInt(el, E.VibratoAttr),
            Tremolo = Fmt.AttrInt(el, E.TremoloAttr) != 0,
            LinkNext = Fmt.AttrInt(el, E.LinkNext) != 0,
            // RsXmlSongWriter.WriteChordNote writes it; the .dcproj and EOF readers keep it.
            Ignore = Fmt.AttrInt(el, E.Ignore) != 0,
        };

        if (Fmt.AttrInt(el, E.Mute) != 0) cn.Mute = MuteType.Full;
        else if (Fmt.AttrInt(el, E.PalmMute) != 0) cn.Mute = MuteType.Palm;

        if (Fmt.AttrInt(el, E.HammerOn) != 0) cn.Hopo = HopoType.HammerOn;
        else if (Fmt.AttrInt(el, E.PullOff) != 0) cn.Hopo = HopoType.PullOff;
        else if (Fmt.AttrInt(el, E.Tap) != 0) cn.Hopo = HopoType.Tap;

        if (Fmt.AttrInt(el, E.Harmonic) != 0) cn.Harmonic = HarmonicType.Normal;
        else if (Fmt.AttrInt(el, E.HarmonicPinch) != 0) cn.Harmonic = HarmonicType.Pinch;

        var slideToAttr = el.Attribute(E.SlideTo)?.Value;
        var slideUnpitchAttr = el.Attribute(E.SlideUnpitchTo)?.Value;
        if (slideUnpitchAttr != null && int.TryParse(slideUnpitchAttr, CultureInfo.InvariantCulture, out var su))
        { cn.SlideTo = su; cn.UnpitchedSlide = true; }
        else if (slideToAttr != null && int.TryParse(slideToAttr, CultureInfo.InvariantCulture, out var st))
        {
            cn.SlideTo = st;
        }

        if (Fmt.AttrInt(el, E.Pluck) != 0) cn.BassPicking = BassPickingType.Pop;
        else if (Fmt.AttrInt(el, E.Slap) != 0) cn.BassPicking = BassPickingType.Slap;

        ReadBendValues(el, cn.BendValues, beats);

        // EOF_SRC/rs_import.c:370-395 — a chord note that arrives from RS XML
        // carrying its own sustain, and none of the techniques that would keep
        // that sustain visible anyway, is marked with the explicit sustain
        // status so a re-export keeps the chord at its full box instead of
        // compacting it into a repeat. Charts authored here or imported from a
        // score have no such statement, and compact the way Rocksmith does.
        cn.ExplicitSustain = cn.Sustain > FractionalPosition.Zero &&
                             cn.BendValues.Count == 0 && !cn.SlideTo.HasValue &&
                             !cn.Vibrato && !cn.Tremolo;

        return cn;
    }

    private static void ReadFretHandMutes(XElement lvlEl, ChartLevel level, IList<BeatMarker> beats)
    {
        var fhmEl = lvlEl.Element(E.FretHandMutesEl);
        if (fhmEl == null) return;

        foreach (var m in fhmEl.Elements(E.FretHandMuteEl))
        {
            var timeMs = Fmt.ParseTime(m.Attribute(E.Time)?.Value);
            level.FretHandMutes.Add(new FretHandMute(TimelineMath.PositionFromImportedMs(timeMs, beats)));
        }
    }

    private static void ReadAnchors(XElement lvlEl, ChartLevel level, IList<BeatMarker> beats)
    {
        var anchorsEl = lvlEl.Element(E.Anchors);
        if (anchorsEl == null) return;

        foreach (var aEl in anchorsEl.Elements(E.Anchor))
        {
            var timeMs = Fmt.ParseTime(aEl.Attribute(E.Time)?.Value);
            var fret = Fmt.AttrInt(aEl, E.Fret, 1);
            var widthVal = aEl.Attribute(E.Width)?.Value;
            var width = widthVal != null && double.TryParse(widthVal,
                NumberStyles.Float,
                CultureInfo.InvariantCulture, out var wd) ? (int)wd : 4;
            level.Anchors.Add(new FretAnchor(TimelineMath.PositionFromImportedMs(timeMs, beats), fret, width));
        }
    }

    private static void ReadHandShapes(XElement lvlEl, ChartLevel level, IList<BeatMarker> beats)
    {
        var hsEl = lvlEl.Element(E.HandShapes);
        if (hsEl == null) return;

        foreach (var hEl in hsEl.Elements(E.HandShape))
        {
            var startMs = Fmt.ParseTime(hEl.Attribute(E.StartTime)?.Value);
            var endMs = Fmt.ParseTime(hEl.Attribute(E.EndTime)?.Value);
            var chordId = Fmt.AttrInt(hEl, E.ChordId, -1);
            level.HandShapes.Add(new HandShape
            {
                Position = TimelineMath.PositionFromImportedMs(startMs, beats),
                EndPosition = TimelineMath.PositionFromImportedMs(endMs, beats),
                TemplateId = chordId
            });
        }
    }
}
