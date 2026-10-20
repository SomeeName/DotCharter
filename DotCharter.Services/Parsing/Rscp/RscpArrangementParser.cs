using System.Collections.ObjectModel;
using System.Globalization;
using System.Xml.Linq;
using Arrangement = DotCharter.Core.Domain.Project.Arrangement;
using BendValue = DotCharter.Core.Domain.Guitar.BendValue;
using ChartLevel = DotCharter.Core.Domain.Project.ChartLevel;
using ChartProject = DotCharter.Core.Domain.Project.ChartProject;
using Chord = DotCharter.Core.Domain.Guitar.Chord;
using ChordNote = DotCharter.Core.Domain.Guitar.ChordNote;
using ChordTemplate = DotCharter.Core.Domain.Guitar.ChordTemplate;
using EventPoint = DotCharter.Core.Domain.Events.EventPoint;
using FretAnchor = DotCharter.Core.Domain.Events.FretAnchor;
using GuitarNote = DotCharter.Core.Domain.Guitar.GuitarNote;
using HandShape = DotCharter.Core.Domain.Guitar.HandShape;
using NotationVoice = DotCharter.Core.Domain.Project.NotationVoice;
using PhraseInfo = DotCharter.Core.Domain.Project.PhraseInfo;
using ToneChange = DotCharter.Core.Domain.Events.ToneChange;
using TuningOffset = DotCharter.Core.Domain.Project.TuningOffset;

namespace DotCharter.Services.Parsing.Rscp;

/// <summary>
///     Parses arrangement data including notes, chords, handshapes, and chord templates from RSCP chart files
/// </summary>
public class RscpArrangementParser
{
    /// <summary>What a string is called when the file did not say — never an
    /// invented "E". Matches <c>TimelineGutterControl.Unknown</c>, and is
    /// deliberately unreadable to <c>TuningOffsetResolution.DeriveFromNoteNames</c>
    /// so nothing downstream turns it back into an offset.</summary>
    internal const string UnknownStringName = "?";

    public void ParseArrangements(XDocument doc, ChartProject project)
    {
        var arrangementsRootNode = doc.Root?.Element("arrangements");
        if (arrangementsRootNode != null)
        {
            foreach (var arrNode in arrangementsRootNode.Elements("arrangement"))
            {
                var arr = ParseSingleArrangement(arrNode);
                project.Arrangements.Add(arr);
            }
        }

        if (project.Arrangements.Count == 0)
        {
            var dArr = new Arrangement { Name = "Lead Guitar" };
            for (var i = 0; i < 6; i++) dArr.TuningPitches.Add(new TuningOffset(0));
            var defaultLevel = new ChartLevel { DifficultyIndex = 0 };
            dArr.Levels.Add(defaultLevel);
            project.Arrangements.Add(dArr);
        }

        ParseNotationVoices(doc, project);
    }

    /// <summary>
    /// Reads the optional root-level <c>dcNotationVoices</c> extension node
    /// (notation-only voices 2–4 from multi-voice score imports). Absent in
    /// every pre-voice file — old projects load with empty
    /// <see cref="Arrangement.NotationVoices"/> exactly as before. Schema-
    /// tolerant: out-of-range arrangement indices and malformed voice entries
    /// are dropped, never thrown.
    /// </summary>
    private void ParseNotationVoices(XDocument doc, ChartProject project)
    {
        var node = doc.Root?.Element("dcNotationVoices");
        if (node == null) return;

        foreach (var arrVoices in node.Elements("arrangementVoices"))
        {
            if (!int.TryParse(arrVoices.Attribute("arrangement")?.Value, CultureInfo.InvariantCulture, out var arrIndex)) continue;
            if (arrIndex < 0 || arrIndex >= project.Arrangements.Count) continue;

            var arr = project.Arrangements[arrIndex];
            // A sidecar chord is NEVER templated — RscpTemplateManager
            // .RegenerateTemplates only renumbers its id, it never assigns one —
            // so its frets ride on its chord NOTES (RscpWriter passes
            // writeFret: true for them).
            // Resolving against the level templates instead would let a stale
            // TemplateId, inherited from a copied chord, hand this chord some
            // unrelated level shape's frets.
            var templates = new List<ChordTemplate>();
            foreach (var voiceNode in arrVoices.Elements("voice"))
            {
                if (!int.TryParse(voiceNode.Attribute("index")?.Value, CultureInfo.InvariantCulture, out var voiceIndex)) continue;

                var voice = new NotationVoice { VoiceIndex = Math.Clamp(voiceIndex, 1, 3) };
                var soundsNode = voiceNode.Element("sounds");
                if (soundsNode != null)
                {
                    foreach (var noteNode in soundsNode.Elements("soundNote"))
                        voice.Notes.Add(ParseGuitarNote(noteNode));
                    foreach (var chordNode in soundsNode.Elements("soundChord"))
                        voice.Chords.Add(ParseChord(chordNode, templates));
                }

                if (voice.Notes.Count > 0 || voice.Chords.Count > 0)
                    arr.NotationVoices.Add(voice);
            }
        }
    }

    private Arrangement ParseSingleArrangement(XElement arrNode)
    {
        var stc = int.TryParse(arrNode.Descendants("tuning").FirstOrDefault()?.Attribute("strings")?.Value, CultureInfo.InvariantCulture,
            out var sCount)
            ? sCount
            : 6;
        // Drives allocation loops below — clamp a corrupt attribute to a real
        // instrument range instead of hanging or OOMing on garbage.
        stc = Math.Clamp(stc, 1, 12);
        Enum.TryParse(arrNode.Attribute("type")?.Value ?? "Lead", true, out ArrangementType aType);

        Enum.TryParse(arrNode.Attribute("subtype")?.Value ?? "MAIN", true, out ArrangementSubtype aSub);

        var arr = new Arrangement
        {
            Name = arrNode.Attribute("name")?.Value ?? aType.ToString(),
            ArrangementType = aType,
            Subtype = aSub,
            Capo = int.TryParse(arrNode.Attribute("capo")?.Value, CultureInfo.InvariantCulture, out var capo) ? capo : 0,
            CentOffset = double.TryParse(arrNode.Attribute("centOffset")?.Value, NumberStyles.Any,
                CultureInfo.InvariantCulture, out var cent) ? cent : 0,
            // Absent (or unparseable) stays null = "the author said nothing", which
            // is deliberately distinct from a stored 1.3.
            ScrollSpeed = double.TryParse(arrNode.Attribute("scrollSpeed")?.Value, NumberStyles.Any,
                CultureInfo.InvariantCulture, out var scroll) ? scroll : null,
            StartingTone = arrNode.Attribute("startingTone")?.Value
                           ?? arrNode.Attribute("baseTone")?.Value
                           ?? "",
            PickedBass = arrNode.Attribute("pickedBass")?.Value == "true",
            StringCount = stc,
            // Additive DotCharter attributes; absent in every pre-0.35 file and
            // in anything rscharter wrote, which reads back as "unset".
            ToneBase = arrNode.Attribute("toneBase")?.Value ?? "",
            ToneA = arrNode.Attribute("toneA")?.Value ?? "",
            ToneB = arrNode.Attribute("toneB")?.Value ?? "",
            ToneC = arrNode.Attribute("toneC")?.Value ?? "",
            ToneD = arrNode.Attribute("toneD")?.Value ?? "",
            // Per-arrangement metadata overrides (RS-XML). Additive attributes,
            // absent = "no override", which is what every pre-0.35 file reads as.
            ArtistName = arrNode.Attribute("artistName")?.Value ?? "",
            ArtistNameSort = arrNode.Attribute("artistNameSort")?.Value ?? "",
            AlbumName = arrNode.Attribute("albumName")?.Value ?? "",
            AlbumYear = int.TryParse(arrNode.Attribute("albumYear")?.Value, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var arrAlbumYear)
                ? arrAlbumYear
                : null
        };

        ParseTuning(arrNode, arr, stc);
        ParseEvents(arrNode, arr);
        ParsePhraseIterations(arrNode, arr);
        ParseArrangementProperties(arrNode, arr);
        arr.Provenance = RscpProvenance.Parse(arrNode.Element(RscpProvenance.ArrangementElement)?.Value);

        var templates = ParseChordTemplates(arrNode);
        foreach (var t in templates) arr.ChordTemplates.Add(t);

        foreach (var t in ParseChordTemplateContainer(arrNode.Element("dcFretHandMuteTemplates")))
            arr.FretHandMuteTemplates.Add(t);

        var levelsNode = arrNode.Element("levels");
        if (levelsNode != null)
        {
            var i = 0;
            foreach (var levelNode in levelsNode.Elements("level"))
            {
                var level = new ChartLevel { DifficultyIndex = i };

                ParseLevelData(levelNode, level, templates);

                var sortedNotes = level.Notes.OrderBy(n => n.Position).ToList();
                level.Notes.Clear();
                foreach (var n in sortedNotes) level.Notes.Add(n);

                arr.Levels.Add(level);
                i++;
            }
        }

        if (arr.Levels.Count == 0) arr.Levels.Add(new ChartLevel { DifficultyIndex = 0 });

        return arr;
    }

    private void ParseTuning(XElement arrNode, Arrangement arr, int stc)
    {
        var tNode = arrNode.Descendants("tuning").FirstOrDefault();
        var tType = tNode?.Attribute("type")?.Value ?? "E_STANDARD";
        var tValues = tNode?.Attribute("tuningValues")?.Value ?? "";

        arr.TuningType = tType;
        if (tType == "CUSTOM" && !string.IsNullOrEmpty(tValues))
        {
            var parts = tValues.Split(',');
            for (var i = 0; i < stc; i++)
                arr.TuningPitches.Add(new TuningOffset(i < parts.Length && int.TryParse(parts[i], CultureInfo.InvariantCulture, out var v) ? v : 0));
        }
        else if (TuningUtilities.TryGetOffsetsForTuningType(tType, stc, out var offsets))
        {
            for (var i = 0; i < stc; i++)
                arr.TuningPitches.Add(new TuningOffset(i < offsets.Length ? offsets[i] : 0));
        }
        else
        {
            // A tuning NAME we cannot decode and no explicit values — most often
            // `type="CUSTOM"` written without a `tuningValues` vector, which older
            // builds produced and which reads back as "this chart does not say".
            // Filling zeros here is what made such a chart open as confident E
            // standard: leave the offsets EMPTY and name every string "?" so the
            // gutter, the tuning chip and TuningOffsetResolution all agree that
            // nothing is known. The lane COUNT still comes out right.
            arr.Tuning = Enumerable.Repeat(UnknownStringName, stc).ToArray();
            return;
        }

        arr.Tuning = TuningUtilities.GetTuningNames(stc, arr.TuningPitches.Select(t => t.Step).ToArray());
    }

    private void ParseEvents(XElement arrNode, Arrangement arr)
    {
        var phrasesNode = arrNode.Element("phrases");
        if (phrasesNode != null)
        {
            foreach (var pNode in phrasesNode.Elements("phrase"))
            {
                var pName = pNode.Attribute("name")?.Value;
                if (string.IsNullOrEmpty(pName)) continue;
                arr.Phrases.Add(pName);

                var hasMaxDiff = int.TryParse(pNode.Attribute("maxDifficulty")?.Value, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var maxDiff);
                var solo = pNode.Attribute("solo")?.Value == "T";
                if (hasMaxDiff || solo)
                    arr.PhraseInfos[pName] = new PhraseInfo(hasMaxDiff ? maxDiff : 0, solo);
            }
        }

        var epsNode = arrNode.Element("eventPoints");
        if (epsNode != null)
        {
            foreach (var epNode in epsNode.Elements("eventPoint"))
            {
                arr.EventPoints.Add(new EventPoint
                {
                    Position = RscpParsingUtilities.ParseFraction(epNode.Attribute("p")?.Value),
                    Section = epNode.Attribute("section")?.Value ?? "",
                    Phrase = epNode.Attribute("phrase")?.Value ?? "",
                    Events = epNode.Attribute("events")?.Value ?? "",
                    // v0.29 section status (`ss`, lowercase name). Tolerant BOTH
                    // ways: absent/unknown reads None (a pre-v0.29 file, or a
                    // future status this build doesn't know), and an old app
                    // simply ignores the attribute — attribute-based parsing
                    // never throws on extras.
                    Status = RscpParsingUtilities.ParseSectionStatus(epNode.Attribute("ss")?.Value)
                });
            }
        }

        var tonesNode = arrNode.Element("tones");
        if (tonesNode != null)
        {
            foreach (var tNode in tonesNode.Elements("string"))
                arr.ToneNames.Add(tNode.Value);
        }

        var tcsNode = arrNode.Element("toneChanges");
        if (tcsNode != null)
        {
            foreach (var toneNode in tcsNode.Elements("toneChange"))
            {
                arr.ToneChanges.Add(new ToneChange
                {
                    Position = RscpParsingUtilities.ParseFraction(toneNode.Attribute("p")?.Value),
                    ToneName = toneNode.Attribute("tone")?.Value
                               ?? toneNode.Attribute("toneName")?.Value
                               ?? "Default"
                });
            }
        }
    }

    /// <summary>Explicit phrase iterations (DotCharter extension node). Absent
    /// leaves the collection empty, which the writers treat as "derive from
    /// event points" exactly as before.</summary>
    private void ParsePhraseIterations(XElement arrNode, Arrangement arr)
    {
        var node = arrNode.Element("dcPhraseIterations");
        if (node == null) return;

        foreach (var piNode in node.Elements("phraseIteration"))
        {
            var pos = RscpParsingUtilities.ParseFraction(piNode.Attribute("p")?.Value);
            var id = ParseIntAttribute(piNode, "id", 0);
            arr.PhraseIterations.Add(new PhraseIteration(pos, id));
        }
    }

    /// <summary>RS-XML technique/path flags (DotCharter extension node). Every
    /// flag is optional and falls back to the freshly constructed default, so a
    /// partially written node — or none at all — is valid.
    /// <para><c>manual="T"</c> is the pin that stops the export deriving the
    /// technique flags from chart content. Absent (or anything but "T") reads as
    /// false = derive, which is what every file written by an older build says.</para>
    /// </summary>
    private void ParseArrangementProperties(XElement arrNode, Arrangement arr)
    {
        var node = arrNode.Element("dcArrangementProperties");
        if (node == null) return;

        arr.Properties.ManualOverride = node.Attribute("manual")?.Value == "T";

        foreach (var (name, _, set) in RscpArrangementPropertyMap.Flags)
        {
            var attr = node.Attribute(name);
            if (attr != null && int.TryParse(attr.Value, CultureInfo.InvariantCulture, out var value)) set(arr.Properties, value);
        }
    }

    private List<ChordTemplate> ParseChordTemplates(XElement arrNode)
        => ParseChordTemplateContainer(arrNode.Element("chordTemplates"));

    /// <summary>Parses every <c>chordTemplate</c> child of a container element.
    /// Shared by the regular template set and the fret-hand-mute set.</summary>
    private List<ChordTemplate> ParseChordTemplateContainer(XElement? templatesNode)
    {
        var templates = new List<ChordTemplate>();

        if (templatesNode != null)
        {
            var index = 0;
            foreach (var ctNode in templatesNode.Elements("chordTemplate"))
            {
                var template = new ChordTemplate
                {
                    Id = index++,
                    Name = ctNode.Attribute("chordName")?.Value ?? "",
                    Arpeggio = ctNode.Attribute("arpeggio")?.Value == "T",
                    ForceArpeggioInRs = ctNode.Attribute("forceRS")?.Value == "T",
                    NopInRs = ctNode.Attribute("nop")?.Value == "T",
                    // absent (every older project) reads as not minted, so
                    // a shifted fingering stays authored, as it always did.
                    FollowedFingering = ctNode.Attribute("followed")?.Value == "T",
                    // absent (every older project) reads as an ordinary
                    // template, whose missing fingers L07 still reports.
                    Fingerless = ctNode.Attribute("fingerless")?.Value == "T",
                    // absent (every older project) reads as no ghost gems and no pin.
                    GhostMask = RscpGhostStrings.Parse(ctNode.Attribute("ghost")?.Value),
                    Pinned = ctNode.Attribute("pinned")?.Value == "T"
                };

                var fretsAttr = ctNode.Attribute("frets")?.Value ?? "";
                foreach (var pair in fretsAttr.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = pair.Split('=');
                    if (parts.Length == 2 && int.TryParse(parts[0], CultureInfo.InvariantCulture, out var s) && int.TryParse(parts[1], CultureInfo.InvariantCulture, out var f))
                        template.Frets[s] = f;
                }

                var fingersAttr = ctNode.Attribute("fingers")?.Value ?? "";
                foreach (var pair in fingersAttr.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = pair.Split('=');
                    if (parts.Length == 2 &&
                        int.TryParse(parts[0], CultureInfo.InvariantCulture, out var s) &&
                        int.TryParse(parts[1], CultureInfo.InvariantCulture, out var f) &&
                        ChordTemplate.FingerNames.ContainsKey(f))
                    {
                        template.Fingers[s] = f;
                    }
                }

                templates.Add(template);
            }
        }

        return templates;
    }

    private void ParseLevelData(XElement levelNode, ChartLevel level, List<ChordTemplate> templates)
    {
        ParseAnchors(levelNode, level);
        ParseHandShapes(levelNode, level);
        ParseFretHandMutes(levelNode, level);
        ParseSounds(levelNode, level, templates);
    }

    /// <summary>Per-level fret-hand-mute positions (DotCharter extension node).
    /// Absent leaves the collection empty, exactly as before — the arrangement's
    /// FretHandMuteTemplates had nothing pointing at them until this existed.</summary>
    private void ParseFretHandMutes(XElement levelNode, ChartLevel level)
    {
        var node = levelNode.Element("dcFretHandMutes");
        if (node == null) return;

        foreach (var fhmNode in node.Elements("fretHandMute"))
        {
            level.FretHandMutes.Add(new FretHandMute(
                RscpParsingUtilities.ParseFraction(fhmNode.Attribute("p")?.Value)));
        }
    }

    private void ParseAnchors(XElement levelNode, ChartLevel level)
    {
        var anchorsNode = levelNode.Element("anchors");
        if (anchorsNode == null) return;

        foreach (var anchorNode in anchorsNode.Elements("anchor"))
        {
            level.Anchors.Add(new FretAnchor
            {
                Position = RscpParsingUtilities.ParseFraction(anchorNode.Attribute("p")?.Value),
                Fret = ParseIntAttribute(anchorNode, "fret", 1),
                Width = ParseIntAttribute(anchorNode, "width", 4)
            });
        }
    }

    private void ParseHandShapes(XElement levelNode, ChartLevel level)
    {
        var handShapesNode = levelNode.Element("handShapes");
        if (handShapesNode == null) return;

        foreach (var hsNode in handShapesNode.Elements("handShape"))
        {
            var pos = RscpParsingUtilities.ParseFraction(hsNode.Attribute("p")?.Value);
            var endPos = RscpParsingUtilities.ParseFraction(hsNode.Attribute("ep")?.Value);
            var tId = int.TryParse(hsNode.Attribute("templateId")?.Value, CultureInfo.InvariantCulture, out var id) ? id : -1;

            level.HandShapes.Add(new HandShape { Position = pos, EndPosition = endPos, Name = "Chord", TemplateId = tId });
        }
    }

    private void ParseSounds(XElement levelNode, ChartLevel level, List<ChordTemplate> templates)
    {
        var soundsNode = levelNode.Element("sounds");
        if (soundsNode == null) return;

        foreach (var noteNode in soundsNode.Elements("soundNote"))
        {
            var note = ParseGuitarNote(noteNode);
            level.Notes.Add(note);
        }

        foreach (var chordNode in soundsNode.Elements("soundChord"))
        {
            var chord = ParseChord(chordNode, templates);
            level.Chords.Add(chord);
        }
    }

    private GuitarNote ParseGuitarNote(XElement noteNode)
    {
        var pos = RscpParsingUtilities.ParseFraction(noteNode.Attribute("p")?.Value);
        var endPos = RscpParsingUtilities.ParseFraction(noteNode.Attribute("ep")?.Value);

        var note = new GuitarNote
        {
            Position = pos,
            Sustain = FractionalPosition.Max(FractionalPosition.Zero, endPos - pos),
            StringIndex = ParseIntAttribute(noteNode, "string", 0),
            Fret = ParseIntAttribute(noteNode, "fret", 0),
            Accent = noteNode.Attribute("accent")?.Value == "T",
            Ignore = noteNode.Attribute("ignore")?.Value == "T",
            IgnoreDerived = noteNode.Attribute("ignDer")?.Value == "T",
            PassOtherNotes = noteNode.Attribute("crazy")?.Value == "T"
        };

        // Optional authored notation duration (quarter-note units); absent or
        // malformed → null, notation falls back to derived durations.
        if (Fraction.TryParse(noteNode.Attribute("nd")?.Value, out var nd))
            note.NotationDuration = nd;

        // Optional before-beat grace-note hint; absent or malformed → null.
        if (int.TryParse(noteNode.Attribute("gf")?.Value, CultureInfo.InvariantCulture, out var gf))
            note.GraceFret = gf;

        note.LeftHandFinger = Math.Max(-1, ParseIntAttribute(noteNode, "leftHand", -1));
        note.PickDirection = ParsePickDirection(noteNode.Attribute("pickDirection")?.Value);

        note.BassPicking = ParseBassPicking(noteNode.Attribute("bassPicking")?.Value);
        note.Mute = ParseMute(noteNode.Attribute("mute")?.Value);
        note.Hopo = ParseHopo(noteNode.Attribute("hopo")?.Value);
        note.Harmonic = ParseHarmonic(noteNode.Attribute("harmonic")?.Value);

        note.Vibrato = noteNode.Attribute("vibrato")?.Value == "T";
        note.VibratoStrength = ParseIntAttribute(noteNode, "vibratoStrength", 0);
        note.Tremolo = noteNode.Attribute("tremolo")?.Value == "T";
        note.LinkNext = noteNode.Attribute("linkNext")?.Value == "T";
        note.UnpitchedSlide = noteNode.Attribute("unpitchedSlide")?.Value == "T";
        if (int.TryParse(noteNode.Attribute("slideTo")?.Value, CultureInfo.InvariantCulture, out var st)) note.SlideTo = st;

        var bendValuesStr = noteNode.Attribute("bendValues")?.Value;
        note.BendValues = ParseBendValues(bendValuesStr);

        return note;
    }

    private static PickDirectionType ParsePickDirection(string? value) => value switch
    {
        "DOWN" => PickDirectionType.Down,
        "UP" => PickDirectionType.Up,
        _ => PickDirectionType.None
    };

    private Chord ParseChord(XElement chordNode, List<ChordTemplate> templates)
    {
        var pos = RscpParsingUtilities.ParseFraction(chordNode.Attribute("p")?.Value);
        var templateId = ParseIntAttribute(chordNode, "templateId", -1);

        var chord = new Chord
        {
            Position = pos,
            TemplateId = templateId,
            SplitIntoNotes = chordNode.Attribute("splitIntoNotes")?.Value == "T",
            ForceNoNotes = chordNode.Attribute("forceNoNotes")?.Value == "T",
            Accent = chordNode.Attribute("accent")?.Value == "T",
            Ignore = chordNode.Attribute("ignore")?.Value == "T",
            IgnoreDerived = chordNode.Attribute("ignDer")?.Value == "T",
            PassOtherNotes = chordNode.Attribute("crazy")?.Value == "T"
        };

        if (Fraction.TryParse(chordNode.Attribute("nd")?.Value, out var nd))
            chord.NotationDuration = nd;
        chord.Strum = ParsePickDirection(chordNode.Attribute("strum")?.Value);

        var template = templateId >= 0 && templateId < templates.Count ? templates[templateId] : null;

        foreach (var chordNoteNode in chordNode.Elements("chordNote"))
        {
            var chordNote = ParseChordNote(chordNoteNode, pos, template);
            chord.ChordNotes.Add(chordNote);
        }

        return chord;
    }

    private ChordNote ParseChordNote(XElement chordNoteNode, FractionalPosition chordPos, ChordTemplate? template)
    {
        var stringIdx = ParseIntAttribute(chordNoteNode, "string", 0);
        var endPos = RscpParsingUtilities.ParseFraction(chordNoteNode.Attribute("ep")?.Value);

        var chordNote = new ChordNote
        {
            StringIndex = stringIdx,
            Sustain = FractionalPosition.Max(FractionalPosition.Zero, endPos - chordPos),
            // A templated chord's frets live in its template — that is the wire
            // shape, and it is why an edit that moves a fret has to repoint
            // TemplateId. A chord with NO template has its frets on the notes
            // instead (the writer's additive `fret`, see RscpWriter
            // .BuildChordElement); before that existed they all read back 0,
            // which is how every sidecar notation-voice chord lost its shape on
            // the first save. Absent (pre-0.41 files, and every templated
            // chord) still reads 0, exactly as before.
            Fret = template?.GetFret(stringIdx) ?? ParseIntAttribute(chordNoteNode, "fret", 0)
        };

        // Optional before-beat grace-note hint; absent or malformed → null.
        if (int.TryParse(chordNoteNode.Attribute("gf")?.Value, CultureInfo.InvariantCulture, out var cgf))
            chordNote.GraceFret = cgf;

        // Both are on ChordNote, in IGuitarSound, cloned, populated by importers
        // and exported by SNG — but neither was written or read here, so a bass
        // chord's slap/pop and per-string ignore flags died on the first save.
        chordNote.BassPicking = ParseBassPicking(chordNoteNode.Attribute("bassPicking")?.Value);
        chordNote.Ignore = chordNoteNode.Attribute("ignore")?.Value == "T";
        chordNote.Mute = ParseMute(chordNoteNode.Attribute("mute")?.Value);
        chordNote.Hopo = ParseHopo(chordNoteNode.Attribute("hopo")?.Value);
        chordNote.Harmonic = ParseHarmonic(chordNoteNode.Attribute("harmonic")?.Value);
        chordNote.Accent = chordNoteNode.Attribute("accent")?.Value == "T";
        chordNote.ExplicitSustain = chordNoteNode.Attribute("expSus")?.Value == "T";
        chordNote.Vibrato = chordNoteNode.Attribute("vibrato")?.Value == "T";
        chordNote.VibratoStrength = ParseIntAttribute(chordNoteNode, "vibratoStrength", 0);
        chordNote.Tremolo = chordNoteNode.Attribute("tremolo")?.Value == "T";
        chordNote.LinkNext = chordNoteNode.Attribute("linkNext")?.Value == "T";
        chordNote.UnpitchedSlide = chordNoteNode.Attribute("unpitchedSlide")?.Value == "T";
        if (int.TryParse(chordNoteNode.Attribute("slideTo")?.Value, CultureInfo.InvariantCulture, out var cst)) chordNote.SlideTo = cst;

        var bendValuesStr = chordNoteNode.Attribute("bendValues")?.Value;
        chordNote.BendValues = ParseBendValues(bendValuesStr);

        return chordNote;
    }

    private ObservableCollection<BendValue> ParseBendValues(string? bendValuesStr)
    {
        var result = new ObservableCollection<BendValue>();
        if (string.IsNullOrWhiteSpace(bendValuesStr)) return result;

        var parts = bendValuesStr.Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var pair = part.Split('=');
            if (pair.Length == 2)
            {
                var t = RscpParsingUtilities.ParseFraction(pair[0]);
                if (double.TryParse(pair[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var step))
                    result.Add(new BendValue(t, step));
            }
        }

        return result;
    }

    private static int ParseIntAttribute(XElement element, string name, int defaultValue)
    {
        return int.TryParse(element.Attribute(name)?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture,
            out var value)
            ? value
            : defaultValue;
    }

    private MuteType ParseMute(string? val)
    {
        return val switch
        {
            "FULL" => MuteType.Full,
            "PALM" => MuteType.Palm,
            _ => MuteType.None
        };
    }

    private HopoType ParseHopo(string? val)
    {
        return val switch
        {
            "HAMMER_ON" => HopoType.HammerOn,
            "PULL_OFF" => HopoType.PullOff,
            "TAP" => HopoType.Tap,
            _ => HopoType.None
        };
    }

    private HarmonicType ParseHarmonic(string? val)
    {
        return val switch
        {
            "NORMAL" => HarmonicType.Normal,
            "PINCH" => HarmonicType.Pinch,
            _ => HarmonicType.None
        };
    }

    private BassPickingType ParseBassPicking(string? val)
    {
        return val switch
        {
            "POP" => BassPickingType.Pop,
            "SLAP" => BassPickingType.Slap,
            _ => BassPickingType.None
        };
    }
}
