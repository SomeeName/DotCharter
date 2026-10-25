using System.Globalization;
using System.Xml.Linq;
using Arrangement = DotCharter.Core.Domain.Project.Arrangement;
using BendValue = DotCharter.Core.Domain.Guitar.BendValue;
using ChartLevel = DotCharter.Core.Domain.Project.ChartLevel;
using ChartProject = DotCharter.Core.Domain.Project.ChartProject;
using ChartProvenance = DotCharter.Core.Domain.Project.ChartProvenance;
using Chord = DotCharter.Core.Domain.Guitar.Chord;
using ChordTemplate = DotCharter.Core.Domain.Guitar.ChordTemplate;
using ChordNote = DotCharter.Core.Domain.Guitar.ChordNote;
using GuitarNote = DotCharter.Core.Domain.Guitar.GuitarNote;
using ProjectIdentity = DotCharter.Core.Domain.Project.ProjectIdentity;
using SectionStatus = DotCharter.Core.Domain.Events.SectionStatus;
using VocalTrack = DotCharter.Core.Domain.Vocals.VocalTrack;

namespace DotCharter.Services.Parsing.Rscp;

/// <summary>
///     Writes RSCP chart files from ChartProject data.
///     Output is intended to be byte-compatible with rscharter's XStream wire format.
/// </summary>
public class RscpWriter
{
    private readonly RscpTemplateManager _templateManager = new();

    public void WriteDocument(XDocument doc, ChartProject project)
    {
        var root = new XElement("project");

        BuildChartHeader(root, project);
        BuildBeatsNode(root, project);
        BuildVocalsNode(root, project);
        // Both nodes read the SAME regenerated copies: regeneration
        // renumbers the templates to positions, and a notation-voice chord's
        // templateId has to follow its template there too.
        var arrangements = RegeneratedArrangements(project);
        BuildArrangementsNode(root, arrangements);
        BuildBookmarksNode(root, project);
        BuildNotationVoicesNode(root, arrangements);
        RscpLinkGroups.Write(root, project);

        root.Add(new XElement("text", project.TextContent));

        // LAST, deliberately — the top-level nodes a newer build wrote that this
        // build could not read. Appending keeps every Descendants() lookup above
        // resolving to the real node first; the contract type explains both that
        // and why a name the writer already used is dropped rather than
        // duplicated. See docs/DCPROJ-FORMAT-GUARANTEES.md.
        RscpFormatContract.EmitUnknownRootNodes(root, project.UnknownRscpNodes);

        doc.Add(root);
    }

    private static string FormatDouble(double v)
    {
        // RsCharter's XStream serializes Java doubles via Double.toString(),
        // which always emits a decimal point (e.g. "9216.0"). Mirror that so
        // round-tripped files diff cleanly against RsCharter output.
        var s = v.ToString("R", CultureInfo.InvariantCulture);
        return s.Contains('.') || s.Contains('E') || s.Contains('e') || s.Contains("Infinity") || s.Contains("NaN")
            ? s
            : s + ".0";
    }

    private void BuildChartHeader(XElement root, ChartProject project)
    {
        // Always pin written files to chart format version 3. RsCharter
        // re-runs v1/v2 → v3 migration on every load when the version is < 3,
        // which would corrupt data we already normalized into the v3 layout
        // (FractionalPosition everywhere). See ChartProjectVersion3Updater.java.
        root.Add(new XElement("chartFormatVersion", 3));

        // The soft build stamp, and the ONE dc* element written unconditionally.
        // Every other one is absent at its default because absence and the
        // default mean the same thing; here they do not — absence has to keep
        // meaning "written before the stamp existed", so a version-shaped answer
        // is only available if it is always present. It is what lets a reader
        // say "this file came from a build after me, and I will drop what I
        // cannot re-attach" instead of downgrading it in silence
        // (RscpChartSerializer.Load). Additive: no format-version bump, and a
        // reader that predates it ignores it like any other unknown element.
        if (RscpFormatContract.CurrentWriterVersion.Length > 0)
        {
            root.Add(new XElement(RscpFormatContract.WriterVersionElement,
                RscpFormatContract.CurrentWriterVersion));
        }

        // The stable project identity (ChartProject.ProjectId), written on
        // every save for the same reason as the stamp: absence has to keep meaning
        // "written before the id existed — derive one on open". It is what the AI
        // chat history keys a conversation by. Never written malformed: the reader
        // refuses one, and the chat store names a directory after it.
        if (ProjectIdentity.IsWellFormed(project.ProjectId))
            root.Add(new XElement(RscpFormatContract.ProjectIdElement, project.ProjectId));

        root.Add(new XElement("editMode", project.Chart.EditMode));
        root.Add(new XElement("arrangement", project.Chart.ArrangementId));
        root.Add(new XElement("level", project.Chart.Level));
        root.Add(new XElement("time", FormatDouble(project.Chart.Time)));
        root.Add(new XElement("artistName", project.Chart.Artist));
        root.Add(new XElement("artistNameSort",
            string.IsNullOrWhiteSpace(project.Chart.ArtistSort) ? project.Chart.Artist : project.Chart.ArtistSort));
        root.Add(new XElement("title", project.Chart.SongName));
        root.Add(new XElement("albumName", project.Chart.AlbumName));
        // DotCharter extensions, written only when non-empty so a plain
        // rscharter file stays byte-identical (same rule as every other dc*).
        // ELEMENTS, not attributes: AddIfNotEmpty writes an ATTRIBUTE, which is
        // right for the arrangement nodes it serves and wrong here — the header
        // parser reads Descendants(), so an attribute round-tripped as empty.
        if (!string.IsNullOrWhiteSpace(project.Chart.JapaneseTitle))
            root.Add(new XElement("dcJapaneseTitle", project.Chart.JapaneseTitle.Trim()));
        if (!string.IsNullOrWhiteSpace(project.Chart.JapaneseArtist))
            root.Add(new XElement("dcJapaneseArtist", project.Chart.JapaneseArtist.Trim()));

        // rscharter uses Integer (nullable). When null/blank we omit the element so XStream maps null on read.
        if (!string.IsNullOrWhiteSpace(project.Chart.AlbumYear) &&
            int.TryParse(project.Chart.AlbumYear.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var year))
        {
            root.Add(new XElement("albumYear", year));
        }

        // DotCharter extension (dc*): only written when non-empty so plain
        // rscharter files stay byte-identical. (Additive elements — no format
        // version bump; readers that predate them ignore them.)
        if (!string.IsNullOrWhiteSpace(project.Chart.Charter))
            root.Add(new XElement("dcCharter", project.Chart.Charter.Trim()));
        if (!string.IsNullOrWhiteSpace(project.Chart.Genre))
            root.Add(new XElement("dcGenre", project.Chart.Genre.Trim()));
        if (!string.IsNullOrWhiteSpace(project.Chart.ReleaseDate))
            root.Add(new XElement("dcReleaseDate", project.Chart.ReleaseDate.Trim()));
        if (!string.IsNullOrWhiteSpace(project.Chart.CoverArtPath))
            root.Add(new XElement("dcCoverArt", project.Chart.CoverArtPath.Trim()));
        // The package version; absent at the default "1" so a chart that never set one is byte-identical.
        if (!string.IsNullOrWhiteSpace(project.Chart.Version) && project.Chart.Version.Trim() != "1")
            root.Add(new XElement("dcVersion", project.Chart.Version.Trim()));

        // Project state with no rscharter field. Same additive dc* contract:
        // absent at the default, so a chart that never carried them is
        // byte-identical.
        //   dcBeatMapLocked  — the deliberate user toggle guarding eight
        //                      destructive tempo-map operations. It reset to
        //                      false on every reload, so a chart locked after a
        //                      painstaking manual sync reopened unlocked.
        //   dcKeyAccidentals — the key signature, authored through two UI
        //   dcKeyIsMinor      surfaces with an undo action and imported from
        //                     Guitar Pro; the sheet strip fell back to C major
        //                     on every reopen.
        if (project.IsBeatMapLocked) root.Add(new XElement("dcBeatMapLocked", "true"));
        //   dcProvenance     — play-only content stays play-only across a save;
        // absent when unrestricted. Written BEFORE
        //                      <arrangements>: the header index answers the FIRST
        //                      element by name in document order.
        if (project.Provenance != ChartProvenance.Unrestricted)
            root.Add(new XElement(RscpProvenance.ProjectElement, RscpProvenance.Format(project.Provenance)));
        if (project.Chart.KeyAccidentals != 0)
            root.Add(new XElement("dcKeyAccidentals", project.Chart.KeyAccidentals));
        if (project.Chart.KeyIsMinor) root.Add(new XElement("dcKeyIsMinor", "true"));
        // dcSynthPlayStyle — how the chart synth plays this song: a
        //                      guitar and a bass rule set; absent at the defaults.
        if (!project.SynthPlayStyle.IsDefault)
        {
            root.Add(new XElement("dcSynthPlayStyle",
                SynthPlayRules("guitar", project.SynthPlayStyle.Guitar),
                SynthPlayRules("bass", project.SynthPlayStyle.Bass)));
        }

        root.Add(new XElement("musicFileName",
            string.IsNullOrEmpty(project.AudioFileName) ? "song.wav" : project.AudioFileName));

        BuildStemsNode(root, project);

        root.Add(new XElement("selectedStem", project.Chart.SelectedStem));

        BuildArrangementFilesNode(root, project);
    }

    private void BuildStemsNode(XElement root, ChartProject project)
    {
        var stemsNode = new XElement("stems");
        foreach (var stem in project.Stems)
        {
            var stemNode = new XElement("stem",
                new XAttribute("name", stem.Name),
                new XAttribute("path", stem.Path),
                new XAttribute("local", stem.Local ? "true" : "false"),
                new XAttribute("volume", FormatDouble(stem.Volume)),
                new XAttribute("offset", FormatDouble(stem.Offset)));

            // Additive, absent when false. Muted/Solo are on the model, cloned,
            // and written by the stem mixer, but nothing persisted them — while
            // StemTrackViewModel's whole _runScopedMute machinery exists to keep
            // a gameplay-run mute OUT of the saved project, i.e. to protect a
            // field that was not being saved at all.
            if (stem.Muted) stemNode.Add(new XAttribute("muted", "true"));
            if (stem.Solo) stemNode.Add(new XAttribute("solo", "true"));

            stemsNode.Add(stemNode);
        }

        // The OTHER half of the mixer state, and useless without the stem half.
        // Written unconditionally rather than "only when true", because absent has
        // to keep meaning "derive it" for files that predate the attribute — see
        // ChartProject.OriginalMixMuted.
        if (project.Stems.Count > 0)
            stemsNode.Add(new XAttribute("originalMuted", project.OriginalMixMuted ? "true" : "false"));
        // Additive and absent when off: absent and false mean the same thing here.
        if (project.GuitarRolesSplit) stemsNode.Add(new XAttribute("guitarRoles", "true"));

        root.Add(stemsNode);
    }

    private void BuildArrangementFilesNode(XElement root, ChartProject project)
    {
        var arrFilesNode = new XElement("arrangementFiles");
        foreach (var af in project.ArrangementFiles) arrFilesNode.Add(new XElement("string", af));
        root.Add(arrFilesNode);
    }

    private void BuildBeatsNode(XElement root, ChartProject project)
    {
        var beatsNode = new XElement("beats");
        foreach (var b in project.Beats)
        {
            var beatNode = new XElement("beat2",
                new XAttribute("t", FormatDouble(b.Time)));
            beatNode.Add(new XAttribute("sig", $"{b.BeatsInMeasure}/{b.NoteDenominator}"));
            if (b.IsMeasure) beatNode.Add(new XAttribute("bar", "T"));
            if (b.IsAnchor) beatNode.Add(new XAttribute("anchor", "T"));
            beatsNode.Add(beatNode);
        }

        root.Add(beatsNode);

        root.Add(new XElement("mainAudioOffsetAdded", FormatDouble(project.Chart.MainAudioOffsetAdded)));
        root.Add(new XElement("barsAdded", project.Chart.BarsAdded));

        BuildShowlightsNode(root, project);
    }

    private void BuildShowlightsNode(XElement root, ChartProject project)
    {
        var slNode = new XElement("showlights");
        // Group showlights at identical positions into a single comma-separated
        // <showlight t="A,B"/> element to mirror RsCharter's List<ShowlightType>
        // serialization (ShowlightConverter.java).
        var grouped = project.Showlights
            .GroupBy(sl => sl.Position)
            .OrderBy(g => g.Key);
        foreach (var group in grouped)
        {
            var typesStr = string.Join(",", group
                .OrderBy(sl => ShowlightCatalog.GetSortOrder(sl.Type, sl.Note))
                .Select(sl => ShowlightCatalog.NormalizeType(sl.Type, sl.Note)));
            slNode.Add(new XElement("showlight",
                new XAttribute("p", group.Key.ToString()),
                new XAttribute("t", typesStr)));
        }
        root.Add(slNode);
    }

    private void BuildVocalsNode(XElement root, ChartProject project)
    {
        var vocalPathsNode = new XElement("vocalPaths");
        foreach (var vt in project.VocalTracks)
        {
            var vocalsOuter = new XElement("vocals");
            vocalsOuter.Add(new XElement("name", vt.Name));

            BuildVocalColorNode(vocalsOuter, vt);
            BuildVocalLyricsNode(vocalsOuter, vt);
            BuildVocalPhrasesNode(vocalsOuter, vt);

            vocalPathsNode.Add(vocalsOuter);
        }

        root.Add(vocalPathsNode);
    }

    private void BuildVocalColorNode(XElement vocalsOuter, VocalTrack vt)
    {
        var colorNode = new XElement("color");
        colorNode.Add(new XElement("red", vt.ColorR));
        colorNode.Add(new XElement("green", vt.ColorG));
        colorNode.Add(new XElement("blue", vt.ColorB));
        colorNode.Add(new XElement("alpha", vt.ColorA));
        vocalsOuter.Add(colorNode);
    }

    private void BuildVocalLyricsNode(XElement vocalsOuter, VocalTrack vt)
    {
        var vocalsInner = new XElement("vocals");
        foreach (var phrase in vt.Phrases)
        {
            foreach (var l in phrase.Lyrics)
            {
                var vNode = new XElement("vocal",
                    new XAttribute("p", l.Position.ToString()),
                    new XAttribute("ep", l.EndPosition.ToString()),
                    new XAttribute("text", l.Text));

                var closesPhrase = l.IsPhraseEnd || l == phrase.Lyrics.Last();
                if (l.IsWordPart) vNode.Add(new XAttribute("flag", "WORD_PART"));
                else if (closesPhrase) vNode.Add(new XAttribute("flag", "PHRASE_END"));

                vNode.Add(new XAttribute("note", l.Note));

                // There is exactly ONE legacy `flag` slot and WORD_PART takes it, so a
                // syllable that is BOTH a word part and the last of its phrase would
                // write no terminator at all — and RscpVocalsParser, which rebuilds every
                // phrase boundary from PHRASE_END alone, would then never close that phrase and
                // swallow the next one: the phrase COUNT would drop and the swallowed
                // phrase's own Position/EndPosition would be gone, on every save. Additive and
                // written ONLY when the legacy flag could not carry the phrase end, so a
                // chart without that combination stays byte-identical.
                if (closesPhrase && l.IsWordPart) vNode.Add(new XAttribute("dcPhraseEnd", "true"));
                vocalsInner.Add(vNode);
            }
        }

        vocalsOuter.Add(vocalsInner);
    }

    /// <summary>
    ///     DotCharter extension node (like <c>dcAnnotations</c>). The rscharter wire
    ///     has no phrase element at all — a phrase exists only as the run of
    ///     <c>&lt;vocal&gt;</c> entries up to its terminator — so
    ///     <c>RscpVocalsParser.ParseLyricEvents</c> re-DERIVES every phrase's span
    ///     from its first and last lyric, and a phrase with NO lyrics emits nothing
    ///     and is simply gone on reload. Two things the derivation destroys on every
    ///     save: a phrase authored to lead in before its first syllable or to run past
    ///     its last (it snaps back to the lyrics), and an empty phrase (deleted).
    ///     <c>ChartProjectRestorer</c> carries both, so undo preserved what a save did
    ///     not.
    ///     <para>
    ///         Written only when at least one phrase in the track cannot survive the
    ///         derivation, so a plain rscharter chart stays byte-identical. Every
    ///         phrase of the track is then listed IN ORDER with its lyric count, which
    ///         is what lets the parser put the empty ones back in the right place.
    ///     </para>
    /// </summary>
    private void BuildVocalPhrasesNode(XElement vocalsOuter, VocalTrack vt)
    {
        if (!vt.Phrases.Any(NeedsExplicitSpan)) return;

        var node = new XElement("dcPhrases");
        foreach (var phrase in vt.Phrases)
        {
            node.Add(new XElement("phrase",
                new XAttribute("p", phrase.Position.ToString()),
                new XAttribute("ep", phrase.EndPosition.ToString()),
                new XAttribute("n", phrase.Lyrics.Count.ToString(CultureInfo.InvariantCulture))));
        }

        vocalsOuter.Add(node);
    }

    /// <summary>
    ///     True when the parser's derivation would NOT reproduce this phrase's span:
    ///     no lyrics at all (the phrase would not survive the save), or a start/end
    ///     that is not exactly the first lyric's start and the terminating lyric's
    ///     end, or a lyric flagged <c>IsPhraseEnd</c> before the last one (the parser
    ///     would split the phrase there). The terminator is the first lyric flagged
    ///     <c>IsPhraseEnd</c>, falling back to the last — the same rule the writer
    ///     emits and the parser reads.
    /// </summary>
    private static bool NeedsExplicitSpan(LyricPhrase phrase)
    {
        if (phrase.Lyrics.Count == 0) return true;

        // A phrase end BEFORE the last lyric (a line break inside the phrase) is written
        // as PHRASE_END, which the parser reads as a phrase boundary; only the lyric
        // counts in this node put the lyrics back into one phrase.
        for (var i = 0; i < phrase.Lyrics.Count - 1; i++)
            if (phrase.Lyrics[i].IsPhraseEnd) return true;

        var terminator = phrase.Lyrics.FirstOrDefault(l => l.IsPhraseEnd) ?? phrase.Lyrics[^1];
        return phrase.Position != phrase.Lyrics[0].Position ||
               phrase.EndPosition != terminator.EndPosition;
    }

    private void BuildBookmarksNode(XElement root, ChartProject project)
    {
        // rscharter's MapConverter writes <entry key="..." value="..."/> with attributes.
        var bookmarksNode = new XElement("bookmarks");
        foreach (var bm in project.Bookmarks)
        {
            bookmarksNode.Add(new XElement("entry",
                new XAttribute("key", bm.Index.ToString(CultureInfo.InvariantCulture)),
                new XAttribute("value", FormatDouble(bm.Time))));
        }

        root.Add(bookmarksNode);

        BuildAnnotationsNode(root, project);
        BuildToneDefinitionsNode(root, project);
    }

    private void BuildAnnotationsNode(XElement root, ChartProject project)
    {
        // DotCharter extension node. Only written when annotations exist so
        // charts without notes stay byte-compatible with rscharter's XStream
        // reader (which may reject unknown elements).
        if (project.Annotations.Count == 0) return;

        var node = new XElement("dcAnnotations");
        foreach (var a in project.Annotations)
        {
            node.Add(new XElement("annotation",
                new XAttribute("time", FormatDouble(a.TimeMs)),
                new XAttribute("kind", a.Kind.ToString()),
                new XAttribute("resolved", a.Resolved ? "true" : "false"),
                new XText(a.Text)));
        }

        root.Add(node);
    }

    /// <summary>One <c>dcSynthPlayStyle</c> rule set.</summary>
    private static XElement SynthPlayRules(string name, DotCharter.Core.Domain.Project.ChartSynthPlayRules rules) =>
        new(name,
            new XAttribute("handShapesRing", rules.HandShapesRing ? "true" : "false"),
            new XAttribute("holdNotes", rules.HoldSingleNoteGaps ? "true" : "false"),
            new XAttribute("noteGap", rules.SingleNoteGapDivisor.ToString(CultureInfo.InvariantCulture)),
            new XAttribute("holdChords", rules.HoldChordGaps ? "true" : "false"),
            new XAttribute("chordGap", rules.ChordGapDivisor.ToString(CultureInfo.InvariantCulture)));

    private void BuildToneDefinitionsNode(XElement root, ChartProject project)
    {
        // DotCharter extension node (like dcAnnotations): the project-owned
        // Rocksmith tone definition bodies. Only written when non-empty so
        // charts without authored tones stay byte-compatible with rscharter.
        // The JSON body rides as escaped element text — XML-safe and opaque.
        if (project.ToneDefinitions.Count == 0) return;

        var node = new XElement("dcToneDefinitions");
        foreach (var tone in project.ToneDefinitions)
        {
            node.Add(new XElement("toneDefinition",
                new XAttribute("key", tone.Key),
                new XAttribute("name", tone.Name),
                new XText(tone.Json)));
        }

        root.Add(node);
    }

    /// <summary>
    /// DotCharter extension node (like <c>dcAnnotations</c>): the notation-only
    /// voices 2–4 of multi-voice score imports, keyed by arrangement index.
    /// Only written when at least one arrangement carries voices, so every
    /// single-voice chart stays byte-identical with previous output. Reuses the
    /// level <c>sounds</c> wire shape (soundNote/soundChord interleaved by
    /// position) so note attributes round-trip with the same code paths.
    /// </summary>
    private void BuildNotationVoicesNode(XElement root, List<Arrangement> arrangements)
    {
        XElement? node = null;
        for (var i = 0; i < arrangements.Count; i++)
        {
            var arr = arrangements[i];
            if (arr.NotationVoices.Count == 0) continue;

            node ??= new XElement("dcNotationVoices");
            var arrNode = new XElement("arrangementVoices", new XAttribute("arrangement", i));
            foreach (var voice in arr.NotationVoices.OrderBy(v => v.VoiceIndex))
            {
                var vNode = new XElement("voice", new XAttribute("index", voice.VoiceIndex));
                var soundsNode = new XElement("sounds");
                var notes = voice.Notes.Select(n => (Pos: n.Position, Element: BuildNoteElement(n)));
                // writeFret: a sidecar chord is NEVER templated (the save-time
                // regeneration only renumbers its id, it never assigns one), so
                // its frets have to ride on the chord notes or they are lost —
                // see BuildChordElement.
                var chords = voice.Chords.Select(c =>
                    (Pos: c.Position, Element: BuildChordElement(c, writeFret: true)));
                foreach (var (_, el) in notes.Concat(chords).OrderBy(x => x.Pos))
                    soundsNode.Add(el);
                vNode.Add(soundsNode);
                arrNode.Add(vNode);
            }

            node.Add(arrNode);
        }

        if (node != null) root.Add(node);
    }

    /// <summary>
    ///     Every arrangement, regenerated on a COPY: <c>RegenerateTemplates</c>
    ///     renumbers every template and repoints its users, and a serializer must
    /// not change the project it is handed. The app hands us a
    ///     <c>ProjectSaver</c> snapshot, but a tool or test passing the live
    ///     project got a different project back, and load → save → SNG then
    ///     differed from load → SNG.
    /// </summary>
    private List<Arrangement> RegeneratedArrangements(ChartProject project)
    {
        var arrangements = new List<Arrangement>(project.Arrangements.Count);
        foreach (var source in project.Arrangements)
        {
            var arr = ChartProjectCloner.CloneArrangement(source);
            _templateManager.RegenerateTemplates(arr);
            arrangements.Add(arr);
        }

        return arrangements;
    }

    private void BuildArrangementsNode(XElement root, List<Arrangement> arrangements)
    {
        var arrangementsNode = new XElement("arrangements");
        foreach (var arr in arrangements)
        {

            // Field-order on read is: type, subtype, capo, centOffset, startingTone, pickedBass.
            var arrNode = new XElement("arrangement",
                new XAttribute("type", arr.ArrangementType.ToString()),
                new XAttribute("subtype", arr.Subtype.ToString().ToUpperInvariant()),
                new XAttribute("capo", arr.Capo),
                new XAttribute("centOffset", arr.CentOffset.ToString("0.00", CultureInfo.InvariantCulture)),
                new XAttribute("startingTone", arr.StartingTone),
                new XAttribute("pickedBass", arr.PickedBass ? "true" : "false"));

            // Written only when the chart actually carries one: an ABSENT attribute
            // means "the author expressed no preference", which the player's "use
            // the chart's scroll speed" setting must be able to distinguish from a
            // deliberate 1.3. Writing a default would erase that distinction on the
            // first save.
            if (arr.ScrollSpeed is { } scrollSpeed)
            {
                arrNode.Add(new XAttribute("scrollSpeed",
                    scrollSpeed.ToString("0.0#", CultureInfo.InvariantCulture)));
            }

            // RS-XML tone slots. rscharter has no equivalent, so these are
            // additive DotCharter attributes, absent when unset — a chart that
            // never carried tone slots stays byte-identical. Without them an
            // imported CDLC lost its whole tone assignment on the first save
            // and exported with the default tone.
            AddIfNotEmpty(arrNode, "toneBase", arr.ToneBase);
            AddIfNotEmpty(arrNode, "toneA", arr.ToneA);
            AddIfNotEmpty(arrNode, "toneB", arr.ToneB);
            AddIfNotEmpty(arrNode, "toneC", arr.ToneC);
            AddIfNotEmpty(arrNode, "toneD", arr.ToneD);

            // The arrangement NAME. The parser has always READ this attribute;
            // the writer refused it because rscharter has no such field — but so
            // does every other additive attribute on this element. The cost of
            // the refusal: every name collapsed to the ArrangementType on
            // reload, which is user-visible in the arrangement picker and the
            // multi-track overlay, and silently breaks
            // RocksmithBuildOptions.ArrangementFilter (it matches on Name, so
            // two Leads both reading "Lead" became indistinguishable).
            // Written only when it differs from the type the parser falls back
            // to, so a chart that never carried a name stays byte-identical.
            if (arr.Name != arr.ArrangementType.ToString()) AddIfNotEmpty(arrNode, "name", arr.Name);

            // Per-arrangement metadata overrides (RS-XML artistName /
            // artistNameSort / albumName / albumYear). Populated by
            // RsXmlSongReader and consumed by RsXmlSongWriter, but never
            // serialized — so an override imported from RS-XML/PSARC lived
            // exactly one session. Additive and absent when unset.
            AddIfNotEmpty(arrNode, "artistName", arr.ArtistName);
            AddIfNotEmpty(arrNode, "artistNameSort", arr.ArtistNameSort);
            AddIfNotEmpty(arrNode, "albumName", arr.AlbumName);
            if (arr.AlbumYear is { } albumYear) arrNode.Add(new XAttribute("albumYear", albumYear));

            // Child element order matches rscharter Arrangement field order:
            //   tuning, eventPoints, phrases, tones, toneChanges, chordTemplates, levels.
            BuildTuningNode(arrNode, arr);
            BuildEventPointsNode(arrNode, arr);
            BuildPhrasesNode(arrNode, arr);
            BuildTonesNode(arrNode, arr);
            BuildToneChangesNode(arrNode, arr);
            BuildChordTemplatesNode(arrNode, arr);
            BuildLevelsNode(arrNode, arr);

            // DotCharter-only trailing extension nodes (dcNotationVoices
            // precedent): appended AFTER the rscharter field order so a reader
            // that doesn't know them simply ignores the tail.
            BuildPhraseIterationsNode(arrNode, arr);
            BuildArrangementPropertiesNode(arrNode, arr);
            BuildFretHandMuteTemplatesNode(arrNode, arr);
            if (arr.Provenance != ChartProvenance.Unrestricted)
                arrNode.Add(new XElement(RscpProvenance.ArrangementElement, RscpProvenance.Format(arr.Provenance)));

            arrangementsNode.Add(arrNode);
        }

        root.Add(arrangementsNode);
    }

    private void BuildTuningNode(XElement arrNode, Arrangement arr)
    {
        // The type NAME is only a valid shorthand while the pitches still match
        // what that name means. Five of the six importers fill TuningPitches and
        // leave TuningType at its "E_STANDARD" class default, so writing the name
        // alone shipped a Drop-D / Eb / DADGAD / 7-string import as E standard --
        // the reader re-derives offsets from the name, and EVERY note in the
        // arrangement came back at a different sounding pitch, silently, on the
        // first save+reload. Same for the dialog's "Drop D" preset, which maps to
        // a name TuningUtilities does not know.
        //
        // So the pitches decide: when they disagree with the name, the shape is
        // CUSTOM and the values are written out. Doing it here rather than at each
        // importer also fixes charts already on disk the next time they are saved,
        // and keeps OLD builds reading the file correctly -- they understand
        // CUSTOM + tuningValues, which is why this is not a new attribute.
        //
        // The pitches come through TuningOffsetResolution, not TuningPitches:
        // an arrangement that states its tuning only as note LETTERS has an
        // EMPTY offsets vector, which would write `type="CUSTOM"
        // tuningValues=""` -- an empty vector the parser then fills with zeros.
        // That is the same "exported as E standard" loss the four exporters were
        // put behind the resolution for, in the one format that IS the project.
        //
        // A name the tuning table does not KNOW can never be honest: resolving it to
        // zeros would let an unknown name sitting on an all-standard
        // arrangement pass this check and be written out as a shorthand no
        // reader can expand. TryGet says so instead.
        var known = TuningUtilities.TryGetOffsetsForTuningType(arr.TuningType, arr.StringCount, out var canonical);
        var actual = TuningOffsetResolution.Resolve(arr).ToArray();
        // A DotCharter-only name (ExtendedRangeTunings: 7_STRING_DROP_A, the B E A D G bass)
        // is honest but rscharter cannot read it, so the .rscp says CUSTOM beside the offsets.
        var nameIsHonest = known &&
                           arr.TuningType != "CUSTOM" &&
                           !ExtendedRangeTunings.IsExtendedName(arr.TuningType) &&
                           actual.Length == arr.StringCount &&
                           canonical.Length >= arr.StringCount &&
                           !actual.Where((step, i) => step != canonical[i]).Any();

        var tuningNode = new XElement("tuning",
            new XAttribute("type", nameIsHonest ? arr.TuningType : "CUSTOM"),
            new XAttribute("strings", arr.StringCount));
        // INVARIANT: string.Join formats each int with the AMBIENT culture, and
        // a tuning offset is the one number in this file that is routinely
        // NEGATIVE (Drop D is -2). Under sv-SE / fi-FI / nb-NO / lt-LT that
        // rendered "−2" with U+2212, which ParseTuning rejects on every other
        // machine and substitutes 0 for — the arrangement came back E standard
        // with every note at a different sounding pitch, silently.
        if (!nameIsHonest)
        {
            tuningNode.Add(new XAttribute("tuningValues",
                string.Join(",", actual.Select(step => step.ToString(CultureInfo.InvariantCulture)))));
        }

        arrNode.Add(tuningNode);
    }

    private void BuildEventPointsNode(XElement arrNode, Arrangement arr)
    {
        var epsNode = new XElement("eventPoints");
        var currentPhrasesSet = new HashSet<string>(arr.Phrases);

        foreach (var ep in arr.EventPoints)
        {
            var epNode = new XElement("eventPoint", new XAttribute("p", ep.Position.ToString()));
            if (!string.IsNullOrEmpty(ep.Section)) epNode.Add(new XAttribute("section", ep.Section));
            // v0.29 section-status tracker: DotCharter-only additive attribute
            // (lowercase enum NAME, tolerant parse), absent when None so
            // untracked charts stay byte-identical (nd/gf precedent).
            if (ep.Status != SectionStatus.None)
                epNode.Add(new XAttribute("ss", ep.Status.ToString().ToLowerInvariant()));
            if (!string.IsNullOrEmpty(ep.Phrase))
            {
                epNode.Add(new XAttribute("phrase", ep.Phrase));
                if (!currentPhrasesSet.Contains(ep.Phrase))
                {
                    arr.Phrases.Add(ep.Phrase);
                    currentPhrasesSet.Add(ep.Phrase);
                }
            }
            if (!string.IsNullOrEmpty(ep.Events))
                epNode.Add(new XAttribute("events", ep.Events));

            epsNode.Add(epNode);
        }

        arrNode.Add(epsNode);
    }

    private void BuildPhrasesNode(XElement arrNode, Arrangement arr)
    {
        var phrasesNode = new XElement("phrases");
        foreach (var p in arr.Phrases)
        {
            var pNode = new XElement("phrase", new XAttribute("name", p));
            if (arr.PhraseInfos.TryGetValue(p, out var info))
            {
                // Written even at 0: the parser reads an ABSENT max as "no
                // PhraseInfo", which every consumer treats as FULLY levelled — a
                // phrase that stops at level 0 must not come back reaching the top.
                pNode.Add(new XAttribute("maxDifficulty", info.MaxDifficulty));
                if (info.Solo)
                    pNode.Add(new XAttribute("solo", "T"));
            }
            phrasesNode.Add(pNode);
        }
        arrNode.Add(phrasesNode);
    }

    private void BuildTonesNode(XElement arrNode, Arrangement arr)
    {
        var tonesNode = new XElement("tones");
        var distinct = arr.ToneNames.Distinct().ToList();
        // Java field is Set<String> backed by HashSet (Arrangement.java:71).
        // XStream needs a `class` attribute to reconstruct the concrete Set
        // type when reading; without it some XStream versions reject the
        // element. Only emit when non-empty - empty <tones/> roundtrips fine.
        if (distinct.Count > 0)
            tonesNode.Add(new XAttribute("class", "java.util.HashSet"));
        foreach (var t in distinct) tonesNode.Add(new XElement("string", t));
        arrNode.Add(tonesNode);
    }

    private void BuildToneChangesNode(XElement arrNode, Arrangement arr)
    {
        var toneChangesNode = new XElement("toneChanges");
        foreach (var tc in arr.ToneChanges)
        {
            var tcNode = new XElement("toneChange", new XAttribute("p", tc.Position.ToString()));
            if (!string.IsNullOrEmpty(tc.ToneName))
                tcNode.Add(new XAttribute("tone", tc.ToneName));
            toneChangesNode.Add(tcNode);
        }
        arrNode.Add(toneChangesNode);
    }

    private void BuildLevelsNode(XElement arrNode, Arrangement arr)
    {
        var levelsNode = new XElement("levels");
        foreach (var level in arr.Levels)
        {
            var levelNode = new XElement("level");

            // Order inside <level>: anchors, sounds, handShapes (Java field declaration order in Level).
            var anchorsNode = new XElement("anchors");
            foreach (var a in level.Anchors)
            {
                var aNode = new XElement("anchor",
                    new XAttribute("p", a.Position.ToString()),
                    new XAttribute("fret", a.Fret));
                if (a.Width != 4) aNode.Add(new XAttribute("width", a.Width));
                anchorsNode.Add(aNode);
            }
            levelNode.Add(anchorsNode);

            var soundsNode = new XElement("sounds");
            BuildInterleavedSounds(soundsNode, level);
            levelNode.Add(soundsNode);

            BuildHandShapesNode(levelNode, level);
            BuildFretHandMutesNode(levelNode, level);

            levelsNode.Add(levelNode);
        }
        arrNode.Add(levelsNode);
    }

    /// <summary>
    /// Rscharter stores chords and notes in a single ChordOrNote list ordered by position.
    /// We interleave them on write so the wire order matches.
    /// </summary>
    private void BuildInterleavedSounds(XElement soundsNode, ChartLevel level)
    {
        var notes = level.Notes.Select(n => (Pos: n.Position, Element: BuildNoteElement(n)));
        var chords = level.Chords.Select(c => (Pos: c.Position, Element: BuildChordElement(c)));
        foreach (var (_, el) in notes.Concat(chords).OrderBy(x => x.Pos))
            soundsNode.Add(el);
    }

    private XElement BuildNoteElement(GuitarNote n)
    {
        var sn = new XElement("soundNote",
            new XAttribute("p", n.Position.ToString()),
            new XAttribute("ep", n.EndPosition.ToString()),
            new XAttribute("string", n.StringIndex),
            new XAttribute("fret", n.Fret));

        // Optional authored notation duration (quarter-note units) — absent for
        // charts without score-import provenance; readers that don't know the
        // attribute ignore it (backward-tolerant v3 addition).
        if (n.NotationDuration is { } nd) sn.Add(new XAttribute("nd", nd.ToString()));

        // Optional before-beat grace-note hint (fret, same string) — notation
        // ornament from score imports; same backward-tolerance contract as nd.
        if (n.GraceFret is { } gf) sn.Add(new XAttribute("gf", gf));

        // Additive like nd/gf: absent at the default, so existing files stay byte-identical.
        if (n.LeftHandFinger >= 0) sn.Add(new XAttribute("leftHand", n.LeftHandFinger));
        if (n.PickDirection != PickDirectionType.None)
            sn.Add(new XAttribute("pickDirection", n.PickDirection == PickDirectionType.Up ? "UP" : "DOWN"));

        AddNoteAttributes(sn, n);
        AddNoteTechniques(sn, n);

        if (n.BendValues.Count > 0) sn.Add(new XAttribute("bendValues", BuildBendValuesString(n.BendValues)));
        return sn;
    }

    private void AddNoteAttributes(XElement sn, GuitarNote n)
    {
        if (n.Accent) sn.Add(new XAttribute("accent", "T"));
        if (n.Ignore) sn.Add(new XAttribute("ignore", "T"));
        // Provenance for the flag above: absent means "the user set it", so a
        // pre-0.35 file (and any hand-authored ignore) is never retracted by
        // the enforcer's two-way derivation. Additive, absent at default.
        if (n.IgnoreDerived) sn.Add(new XAttribute("ignDer", "T"));
        if (n.PassOtherNotes) sn.Add(new XAttribute("crazy", "T"));
    }

    private void AddNoteTechniques(XElement sn, GuitarNote n)
    {
        if (n.BassPicking == BassPickingType.Slap) sn.Add(new XAttribute("bassPicking", "SLAP"));
        if (n.BassPicking == BassPickingType.Pop) sn.Add(new XAttribute("bassPicking", "POP"));
        if (n.Mute == MuteType.Full) sn.Add(new XAttribute("mute", "FULL"));
        if (n.Mute == MuteType.Palm) sn.Add(new XAttribute("mute", "PALM"));
        if (n.Hopo == HopoType.HammerOn) sn.Add(new XAttribute("hopo", "HAMMER_ON"));
        if (n.Hopo == HopoType.PullOff) sn.Add(new XAttribute("hopo", "PULL_OFF"));
        if (n.Hopo == HopoType.Tap) sn.Add(new XAttribute("hopo", "TAP"));
        if (n.Harmonic == HarmonicType.Normal) sn.Add(new XAttribute("harmonic", "NORMAL"));
        if (n.Harmonic == HarmonicType.Pinch) sn.Add(new XAttribute("harmonic", "PINCH"));

        if (n.Vibrato) sn.Add(new XAttribute("vibrato", "T"));
        // The parser has ALWAYS read vibratoStrength; nothing ever wrote it, so
        // an authored strength collapsed to 0 on the first save. Additive like
        // nd/gf/accent: absent at 0, so strength-free charts stay byte-identical.
        if (n.VibratoStrength > 0) sn.Add(new XAttribute("vibratoStrength", n.VibratoStrength));
        if (n.Tremolo) sn.Add(new XAttribute("tremolo", "T"));
        if (n.LinkNext) sn.Add(new XAttribute("linkNext", "T"));
        if (n.SlideTo.HasValue)
        {
            sn.Add(new XAttribute("slideTo", n.SlideTo.Value));
            if (n.UnpitchedSlide) sn.Add(new XAttribute("unpitchedSlide", "T"));
        }
    }

    /// <summary>
    ///     One <c>soundChord</c> element. <c>writeFret</c> forces the per-note
    ///     fret attribute on regardless of <see cref="Chord.TemplateId" />, which
    ///     is what the notation-voice sidecar passes: those chords are never
    ///     templated, and a stale id inherited from a copied chord would
    ///     otherwise read as "the template has the frets" when no such template
    ///     describes the shape.
    /// </summary>
    private XElement BuildChordElement(Chord c, bool writeFret = false)
    {
        var sc = new XElement("soundChord",
            new XAttribute("p", c.Position.ToString()),
            new XAttribute("templateId", c.TemplateId));

        if (c.NotationDuration is { } nd) sc.Add(new XAttribute("nd", nd.ToString()));
        if (c.Strum != PickDirectionType.None)
            sc.Add(new XAttribute("strum", c.Strum == PickDirectionType.Up ? "UP" : "DOWN"));

        AddChordAttributes(sc, c);

        // An rscp <chordNote> carries no fret: a chord's frets live in its
        // ChordTemplate, which is why an edit that moves a fret has to repoint
        // TemplateId. A chord with NO template had nowhere to put them, so every
        // one of its frets came back 0.
        //
        // That is not hypothetical. RscpTemplateManager.RegenerateTemplates
        // assigns templates on arr.Levels only (a voice chord's id is merely
        // renumbered), so the sidecar NOTATION VOICES (GP voices 2-4) are
        // never templated — a multi-voice score import lost every voice-2/3/4
        // chord shape on the FIRST save and the sheet strip redrew it as an open
        // strum. Minting them real templates is the wrong repair: RsXmlSongWriter
        // and SngFromDomainMapper both write arr.ChordTemplates wholesale, so
        // notation-only shapes would ship into the artifact the GAME reads.
        //
        // Additive and absent at its default: after RegenerateTemplates every
        // LEVEL chord that has chord notes has a template that reproduces it
        // exactly, so no level chordNote grows the attribute and a chart without
        // notation voices stays byte-identical.
        foreach (var n in c.ChordNotes)
        {
            var cn = BuildChordNoteElement(n, c.Position, writeFret || c.TemplateId < 0);
            sc.Add(cn);
        }
        return sc;
    }

    private void AddChordAttributes(XElement sc, Chord c)
    {
        if (c.SplitIntoNotes) sc.Add(new XAttribute("splitIntoNotes", "T"));
        if (c.ForceNoNotes) sc.Add(new XAttribute("forceNoNotes", "T"));
        if (c.Accent) sc.Add(new XAttribute("accent", "T"));
        if (c.Ignore) sc.Add(new XAttribute("ignore", "T"));
        if (c.IgnoreDerived) sc.Add(new XAttribute("ignDer", "T"));
        if (c.PassOtherNotes) sc.Add(new XAttribute("crazy", "T"));
    }

    /// <summary>
    ///     One <c>chordNote</c> element. <c>writeFret</c> is true only when the
    ///     parent chord has no template to hold this note's fret
    ///     (<see cref="Chord.TemplateId" /> is the <c>-1</c> sentinel), so the
    ///     fret has to be written on the note itself or it is lost — see
    ///     <see cref="BuildChordElement" />.
    /// </summary>
    private XElement BuildChordNoteElement(ChordNote n, FractionalPosition chordPos, bool writeFret)
    {
        var cn = new XElement("chordNote",
            new XAttribute("string", n.StringIndex),
            new XAttribute("ep", (chordPos + n.Sustain).ToString()));

        // Optional before-beat grace-note hint for this string of the strum —
        // same backward-tolerant additive contract as the single note's gf.
        if (n.GraceFret is { } gf) cn.Add(new XAttribute("gf", gf));

        // The untemplated chord's fret (see BuildChordElement). Same additive
        // contract: written only where nothing else can carry it.
        if (writeFret) cn.Add(new XAttribute("fret", n.Fret));

        // The single-note element has always written both of these; chordNote
        // wrote neither and the parser read neither, so slap/pop on a bass CHORD
        // and per-string "don't score this" flags survived an import and an
        // export but not a save/reload. Same spelling as soundNote.
        if (n.BassPicking == BassPickingType.Slap) cn.Add(new XAttribute("bassPicking", "SLAP"));
        if (n.BassPicking == BassPickingType.Pop) cn.Add(new XAttribute("bassPicking", "POP"));
        if (n.Ignore) cn.Add(new XAttribute("ignore", "T"));

        if (n.Mute == MuteType.Full) cn.Add(new XAttribute("mute", "FULL"));
        if (n.Mute == MuteType.Palm) cn.Add(new XAttribute("mute", "PALM"));
        if (n.Hopo == HopoType.HammerOn) cn.Add(new XAttribute("hopo", "HAMMER_ON"));
        if (n.Hopo == HopoType.PullOff) cn.Add(new XAttribute("hopo", "PULL_OFF"));
        if (n.Hopo == HopoType.Tap) cn.Add(new XAttribute("hopo", "TAP"));
        if (n.Harmonic == HarmonicType.Normal) cn.Add(new XAttribute("harmonic", "NORMAL"));
        if (n.Harmonic == HarmonicType.Pinch) cn.Add(new XAttribute("harmonic", "PINCH"));

        if (n.Vibrato) cn.Add(new XAttribute("vibrato", "T"));
        // Same read-but-never-written gap as the single note above.
        if (n.VibratoStrength > 0) cn.Add(new XAttribute("vibratoStrength", n.VibratoStrength));
        if (n.Tremolo) cn.Add(new XAttribute("tremolo", "T"));
        // Backward-tolerant extension (like nd/gf): rscharter has no per-string
        // accent; absent when false so accent-free charts stay byte-identical.
        if (n.Accent) cn.Add(new XAttribute("accent", "T"));
        // Backward-tolerant too. Without it, importing an RS chart and saving
        // it would silently compact every sustained repeated chord on the next
        // export — see ChordNote.ExplicitSustain.
        if (n.ExplicitSustain) cn.Add(new XAttribute("expSus", "T"));
        if (n.LinkNext) cn.Add(new XAttribute("linkNext", "T"));
        if (n.SlideTo.HasValue)
        {
            cn.Add(new XAttribute("slideTo", n.SlideTo.Value));
            if (n.UnpitchedSlide) cn.Add(new XAttribute("unpitchedSlide", "T"));
        }

        if (n.BendValues.Count > 0) cn.Add(new XAttribute("bendValues", BuildBendValuesString(n.BendValues)));

        return cn;
    }

    private void BuildHandShapesNode(XElement levelNode, ChartLevel level)
    {
        var handShapesNode = new XElement("handShapes");
        foreach (var hs in level.HandShapes)
        {
            var node = new XElement("handShape",
                new XAttribute("p", hs.Position.ToString()),
                new XAttribute("ep", hs.EndPosition.ToString()));

            // The UNTEMPLATED sentinel is written by OMITTING the attribute — it
            // must not be coerced to `0`, which is a real template id: the
            // shape would silently adopt an unrelated chord's frets, fingering and
            // name on the first save. Reachable from the primary authoring
            // gesture: right-clicking an empty hand-shape bar leaves
            // HandShapeLaneSpan.BuildBar with nothing to
            // resolve, so the new shape keeps -1.
            //
            // Omitting is the right spelling for BOTH readers.
            // RscpArrangementParser already defaults an absent/unparseable
            // attribute to -1, and rscharter's own HandShapeConverter reads a
            // missing attribute as its null Integer — the same "no template"
            // state. The alternatives are worse there: its marshal writes
            // `templateId + ""`, i.e. the literal "null", which its own
            // `Integer.valueOf` then throws on, and a written `-1` would blow up
            // `HandShape.likeGenerator`'s `chordTemplates.get(-1)`.
            if (hs.TemplateId >= 0) node.Add(new XAttribute("templateId", hs.TemplateId));

            handShapesNode.Add(node);
        }
        levelNode.Add(handShapesNode);
    }

    /// <summary>
    ///     Per-level fret-hand-mute POSITIONS (RS-XML <c>level/fretHandMutes</c>),
    ///     appended after the rscharter level field order. The matching
    ///     <c>FretHandMuteTemplates</c> were added to the arrangement in the
    ///     previous pass, so without this the templates round-tripped with
    ///     nothing pointing at them. Populated by
    ///     <c>RsXmlSongReader.ReadFretHandMutes</c> on every RS-XML/PSARC import
    ///     and carried through DD generation.
    /// </summary>
    private void BuildFretHandMutesNode(XElement levelNode, ChartLevel level)
    {
        if (level.FretHandMutes.Count == 0) return;

        var node = new XElement("dcFretHandMutes");
        foreach (var fhm in level.FretHandMutes)
            node.Add(new XElement("fretHandMute", new XAttribute("p", fhm.Position.ToString())));
        levelNode.Add(node);
    }

    private static void AddIfNotEmpty(XElement node, string name, string value)
    {
        if (!string.IsNullOrEmpty(value)) node.Add(new XAttribute(name, value));
    }

    /// <summary>
    ///     Explicit phrase iterations. Optional by contract — both the rscp and
    ///     XML writers fall back to deriving iterations from
    ///     <see cref="Arrangement.EventPoints" /> when empty — but an
    ///     arrangement that CARRIES them (every RS-XML/PSARC import does) used
    ///     to lose them on the first save and silently fall back to the derived
    ///     set, which is not the same list.
    /// </summary>
    private void BuildPhraseIterationsNode(XElement arrNode, Arrangement arr)
    {
        if (arr.PhraseIterations.Count == 0) return;

        var node = new XElement("dcPhraseIterations");
        foreach (var pi in arr.PhraseIterations)
        {
            node.Add(new XElement("phraseIteration",
                new XAttribute("p", pi.Position.ToString()),
                new XAttribute("id", pi.PhraseId)));
        }

        arrNode.Add(node);
    }

    /// <summary>
    ///     The RS-XML <c>arrangementProperties</c> technique/path flags. Written
    ///     only when at least one flag differs from a freshly constructed
    ///     <see cref="ArrangementProperties" />, so charts with no RS provenance
    ///     stay byte-identical. Each flag is emitted as its own 0/1 attribute
    ///     and only when non-default, keeping the node small.
    ///     <para>
    ///     <c>manual="T"</c> is the additive pin
    ///     (<see cref="ArrangementProperties.ManualOverride" />) — absent when the
    ///     flags are derived, which is every chart that has never opted in.
    ///     </para>
    /// </summary>
    private void BuildArrangementPropertiesNode(XElement arrNode, Arrangement arr)
    {
        var p = arr.Properties;
        var defaults = new ArrangementProperties();
        var node = new XElement("dcArrangementProperties");

        if (p.ManualOverride) node.Add(new XAttribute("manual", "T"));

        foreach (var (name, get, _) in RscpArrangementPropertyMap.Flags)
        {
            var value = get(p);
            if (value != get(defaults)) node.Add(new XAttribute(name, value));
        }

        if (node.HasAttributes) arrNode.Add(node);
    }

    /// <summary>
    ///     Fret-hand-mute chord templates (RS-XML <c>fretHandMuteTemplates</c>).
    ///     Kept out of the regular <c>chordTemplates</c> set on purpose, so they
    ///     need their own node — without it they vanished on save and the
    ///     exported psarc lost every fret-hand-mute shape.
    /// </summary>
    private void BuildFretHandMuteTemplatesNode(XElement arrNode, Arrangement arr)
    {
        if (arr.FretHandMuteTemplates.Count == 0) return;

        var node = new XElement("dcFretHandMuteTemplates");
        foreach (var tmpl in arr.FretHandMuteTemplates) node.Add(BuildChordTemplateElement(tmpl));
        arrNode.Add(node);
    }

    private void BuildChordTemplatesNode(XElement arrNode, Arrangement arr)
    {
        var chordTemplatesNode = new XElement("chordTemplates");
        foreach (var tmpl in arr.ChordTemplates) chordTemplatesNode.Add(BuildChordTemplateElement(tmpl));
        arrNode.Add(chordTemplatesNode);
    }

    /// <summary>One <c>chordTemplate</c> element. Shared by the regular template
    /// set and the fret-hand-mute set so the two can never diverge.</summary>
    private XElement BuildChordTemplateElement(ChordTemplate tmpl)
    {
        var ct = new XElement("chordTemplate",
            new XAttribute("chordName", tmpl.Name));

        if (tmpl.Arpeggio) ct.Add(new XAttribute("arpeggio", "T"));

        // INDEPENDENT of arpeggio, exactly as the parser reads them. Nesting this
        // inside `if (tmpl.Arpeggio)` dropped it on save for the one case that
        // actually produces it: ChartSpacingEnforcer.StripRsDisplaySuffixes runs
        // on EVERY save and folds a "-arp" NAME suffix into this flag —
        // Arpeggio itself stays false, because it is a separate user-settable
        // property. So the marker was created and destroyed by the same save,
        // and RsXmlSongWriter (which re-appends "-arp" from `Arpeggio ||
        // ForceArpeggioInRs`) plus SngFromDomainMapper (ChordMask.Arpeggio) both
        // lost it. Still additive: absent when false.
        if (tmpl.ForceArpeggioInRs) ct.Add(new XAttribute("forceRS", "T"));
        // ChordMask.Nop. Additive like forceRS: absent when false.
        if (tmpl.NopInRs) ct.Add(new XAttribute("nop", "T"));
        // The FHP generator minted this fingering. Additive: absent when false.
        if (tmpl.FollowedFingering) ct.Add(new XAttribute("followed", "T"));
        // Deliberately fingerless — EOF's eflag. Additive: absent when false.
        if (tmpl.Fingerless) ct.Add(new XAttribute("fingerless", "T"));
        // Ghost gems, the string indices EOF's ghost bitmask holds, and the
        // authored pin. Additive: both absent at their defaults, so an old reader and an
        // old project are untouched.
        if (tmpl.GhostMask != 0) ct.Add(new XAttribute("ghost", RscpGhostStrings.Format(tmpl.GhostMask)));
        if (tmpl.Pinned) ct.Add(new XAttribute("pinned", "T"));
        // No name on purpose. Additive: absent when false.
        if (tmpl.Unnamed) ct.Add(new XAttribute("unnamed", "T"));

        var fingersStr = string.Join(",",
            tmpl.Fingers
                .Where(kvp => ChordTemplate.FingerNames.ContainsKey(kvp.Value))
                .OrderBy(kvp => kvp.Key)
                .Select(kvp => $"{kvp.Key}={kvp.Value}"));
        var fretsStr = string.Join(",",
            tmpl.Frets.OrderBy(kvp => kvp.Key).Select(kvp => $"{kvp.Key}={kvp.Value}"));

        if (!string.IsNullOrEmpty(fingersStr)) ct.Add(new XAttribute("fingers", fingersStr));
        if (!string.IsNullOrEmpty(fretsStr)) ct.Add(new XAttribute("frets", fretsStr));

        return ct;
    }

    private string BuildBendValuesString(IEnumerable<BendValue> bendValues)
    {
        // RsCharter stores bend step as BigDecimal scale=3 (BendValuesConverter.java).
        // Mirror the formatting so written files diff cleanly.
        return string.Join(";",
            bendValues.Select(b => $"{b.Position}={b.Step.ToString("0.000", CultureInfo.InvariantCulture)}"));
    }
}
