using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DotCharter.Tests.Localization;

/// <summary>German labels name chart objects consistently and address the user informally.</summary>
public sealed class GermanTerminologyTests
{
    private static readonly Lazy<Dictionary<string, string>> GermanValues = new(ReadGermanValues);

    public static TheoryData<string, string> Labels => new()
    {
        { "Export_FixTip_FillFhp", "Ergänzt in „{0}“ fehlende Greifhandpositionen, wo keine die Noten abdeckt. Vorhandene Positionen bleiben. Ein Rückgängig-Schritt." },
        { "Rig_In", "In" },
        { "Rig_Out", "Out" },
        { "Rig_Editor_FilterKey", "Hoch- / Tiefpass" },
        { "Settings_KeybindsTab_Desc", "Tastenkürzel und Tastenkombinationen" },
        { "PhraseCatalog_Entry_ShapesFmt", "{0} Griffbilder" },
        { "PhraseCatalog_Entry_ShapesOne", "{0} Griffbild" },
        { "Settings_PasteIncludeHandShapes", "Griffbilder einfügen" },
        { "Menu_ToggleGhostGems_Tip", "Macht die ausgewählten Noten zu Geisternoten ihres Griffbilds: gegriffen und im Akkordkasten gezeigt, nie als Noten gespielt, gewertet oder exportiert. Mit ausgewähltem Griffbild werden seine Geisternoten wieder normale Bünde." },
        { "Menu_TappedHarmonic_Tip", "Macht jede ausgewählte Note zu einem getappten Flageolett und legt den Greiffinger, die eingestellte Zahl Bünde tiefer (standardmäßig 12, Einstellungen ▸ Editor), als Geisternote in ihr Griffbild." },
        { "Settings_Tip_TappedHarmonicFretOffset", "Wie viele Bünde unter der getappten Note die Hand greift, wenn die Tastenkombination für getappte Flageoletts das Griffbild anlegt. 12 ist die Oktave." },
        { "Status_GhostGems_None", "Nichts zu ghosten: Noten oder Akkordsaiten auswählen (oder ein Griffbild, um seine Geisternoten zurückzuwandeln)." },
        { "Picker_SelectGpFile", "Guitar-Pro-Datei auswählen (.gp, .gpx, .gp7, .gp5, .gp4, .gp3, .gpa)" },
        { "Keybind_InsertVocalAtPlayhead", "Gesangssilbe an Abspielposition einfügen" },
        { "Menu_InsertVocalAtPlayhead", "Platzhalter-Silbe an Abspielposition einfügen" },
        { "Status_VocalInserted", "Platzhalter-Silbe bei {0} eingefügt." },
        { "Status_InsertVocal_NoTrack", "Keine Gesangsspur zum Einfügen vorhanden." },
        { "Settings_Graphics_PresentMode_Desc", "FIFO verhindert Bildrisse; Mailbox senkt die Latenz; Immediate ist am schnellsten, kann aber Bildrisse verursachen." },
        { "Inspector_Issues_AutoFixTip", "Strukturfehler in allen Arrangements gefahrlos beheben (rückgängig machbar), dann neu prüfen." },
        { "LatencyWizard_Tap", "Tippen" },
        { "Rig_Mic_Tube", "Röhren-Kondensatormikrofon" },
        { "Home_Empty_Body", "Füge der Bibliothek einen Song-Ordner hinzu und wähle dann einen Song zum Spielen oder zum Charten. Dein letzter Song wartet dann hier." },
        { "Dialog_LyricColor", "Liedtextfarbe" },
        { "Inspector_EqNoGain", "keine Pegelanpassung bei diesem Typ" },
        { "Inspector_EqPreset_NotchHum", "50/60-Hz-Brummen ausfiltern" },
        { "History_TappedHarmonic", "Getapptes Flageolett" },
        { "Dialog_TempoMap_AddMeasuresAtStart_Subtitle", "Fügt leere Takte vor dem ersten Schlag ein; alle Chart-Inhalte verschieben sich um denselben Betrag nach hinten." },
        { "LivePlay_NoInput", "Audio-Engine läuft nicht — zuerst ein Eingabegerät konfigurieren" },
        { "PlayMode_CqtVerifier", "CQT-Prüfer (v8)" },
        { "Settings_StemModels_CustomNeedsUrl", "Füge zuerst eine direkte Modelldatei-URL ein (zum Beispiel https://huggingface.co/owner/repo/resolve/main/model.onnx)." },
        { "Settings_StemModels_CustomConverted", "„{0}“ ({1} Stems) wurde nach ONNX konvertiert. Setze es in der Liste unten aktiv." },
        { "Settings_StemModels_PickleConfirmTitle", "Diese Datei kann Code auf deinem PC ausführen" },
        { "Settings_StemModels_PickleConfirmMessage", "„{0}“ ist ein PyTorch-Checkpoint (.ckpt / .pt / .pth). Eine solche Datei kann beim Laden Code auf deinem PC ausführen. DotCharter liest nach Möglichkeit nur die Modellgewichte und lädt die ganze Datei nur, wenn das fehlschlägt. Importiere sie nur, wenn du der Quelle vertraust: {1}" },
        { "Ai_Provider_ClaudeCliMissing", "Claude Code wurde auf diesem Rechner nicht gefunden. Installiere es, melde dich an und öffne die Einstellungen erneut." },
        { "Ai_Provider_CodexCliMissing", "Codex wurde auf diesem Rechner nicht gefunden. Installiere es, führe „codex login“ aus und öffne diesen Tab erneut." },
        { "ShapeEdit_OpenedFromChordNoBar", "Der Akkord, über den du diesen Dialog geöffnet hast" },
        { "ShapeEdit_Result_None", "Noch ändert sich nichts: ändere einen Bund, einen Finger oder den Namen." },
        { "ShapeCleanup_ReviewDesc", "Fast gleich. Nichts ändert sich, bis du wählst." },
        { "Dialog_AiToneGenerate_Title", "Sounds mit KI erzeugen" },
        { "Inspector_Tones", "Sounds" },
        { "Dialog_Tone", "Sound" },
        { "Rig_ToneImported", "„{0}“ zu Meine Sounds hinzugefügt." },
        { "ChordTemplate_Scope_ThisHandShape", "dieses Griffbild" },
        { "ArrangementSync_Preview", "Gemeinsame Klänge: {0}, davon würden {1} geändert. Griffbilder: {2}. Greifhandpositionen: {3}." },
        { "Dialog_Fhp_Title", "Greifhandposition setzen" },
        { "Inspector_Harmonic", "Flageolett" },
        { "Keybind_ToggleHarmonic", "Flageolett ein/aus" },
        { "Nav_Settings", "Einstellungen" },
        { "Nav_Settings_Rail", "Optionen" },
        { "Keybind_Redo", "Wiederholen" },
        { "PlayMode_BackingTrack_Title", "Begleitspur" },
        { "SongBrowser_Col_Paths", "Parts" },
        { "SongBrowser_Detail_Paths", "Welchen Part laden" },
        { "Dialog_Showlights_UsePlayhead", "Abspielposition verwenden" },
        { "Settings_ResetDefaultKeybinds", "Alle Kürzel zurücksetzen" },
        { "PresetBrowser_SaveAsNew", "Neu speichern…" }
    };

    private static readonly (string Name, Regex Pattern)[] Policies =
    [
        ("Handform", new(@"(?i)\bHandform\w*", RegexOptions.CultureInvariant)),
        ("Handposition", new(@"(?i)\bHandposition\w*", RegexOptions.CultureInvariant)),
        ("Griffhandposition", new(@"(?i)\bGriffhand-?position\w*", RegexOptions.CultureInvariant)),
        ("Griffbrettposition", new(@"(?i)\bGriffbrettposition\w*", RegexOptions.CultureInvariant)),
        ("Harmonic", new(@"(?i)\bHarmonic\w*", RegexOptions.CultureInvariant)),
        ("Playhead", new(@"(?i)\bPlayhead\w*", RegexOptions.CultureInvariant)),
        ("Abspielkopf", new(@"(?i)\bAbspielkopf\w*", RegexOptions.CultureInvariant)),
        ("Wiedergabeposition", new(@"(?i)\bWiedergabeposition\w*", RegexOptions.CultureInvariant)),
        ("Klänge", new(@"(?i)\bKlänge\w*", RegexOptions.CultureInvariant)),
        ("Töne", new(@"(?i)\bTöne\w*", RegexOptions.CultureInvariant)),
        ("Tone", new(@"(?i)\bTones?\b", RegexOptions.CultureInvariant)),
        ("Sound-Rig", new(@"(?i)\bSound-Rigs?\b", RegexOptions.CultureInvariant)),
        ("Optionen", new(@"(?i)Optionen", RegexOptions.CultureInvariant)),
        ("Wiederherstellen", new(@"(?i)Wiederherstellen", RegexOptions.CultureInvariant)),
        ("Begleitband", new(@"(?i)\bBegleitband\w*", RegexOptions.CultureInvariant)),
        ("Begleit-Track", new(@"(?i)\bBegleit-Track\w*", RegexOptions.CultureInvariant)),
        ("Pfad", new(@"(?i)pfad(?:e[nrs]?|[es])?", RegexOptions.CultureInvariant)),
        ("Formelle Anrede", new(@"\b(?:Sie|Ihnen|Ihr(?:e[mnrs]?|[emnrs])?)\b", RegexOptions.CultureInvariant)),
        ("Chart-Genus", new(@"(?i)\b(?:der|den|dieser|diesen|einen|die|diese|eine|einer)\s+(?:(?:[\w-]+en|bereits)\s+){0,3}(?:[\w-]+-)?Chart\b(?!-)|\bChart, den du\b|\bEigen(?:er|en) Chart\b", RegexOptions.CultureInvariant))
    ];

    private static readonly (string Policy, string Key, string Text)[] Exceptions =
    [
        // These Klänge are chart notes or chords, including their shared edits and export findings.
        ("Klänge", "ArrangementSync_NothingShared", "Klänge"),
        ("Klänge", "ArrangementSync_Preview", "Klänge"),
        ("Klänge", "Status_ArrangementSyncApplied", "Klänge"),
        ("Klänge", "Status_ArrangementSyncNothingToDo", "Klänge"),
        ("Klänge", "Inspector_Scope_Sounds", "Klänge"),
        ("Klänge", "Inspector_Staged_Elsewhere", "Klängen"),
        ("Klänge", "Inspector_BendSummary", "Klängen"),
        ("Klänge", "Inspector_Span_Encloses", "Klänge"),
        ("Klänge", "Inspector_Queue_FilterTip", "Klänge"),
        ("Klänge", "Inspector_Empty_Tile_Blockers", "Klänge"),
        ("Klänge", "Inspector_Span_Fit", "Klänge"),
        ("Klänge", "Inspector_Span_FitTip", "Klänge"),
        ("Klänge", "Inspector_Empty_Tile_BlockersTipFmt", "Klänge"),
        ("Klänge", "ChordShapes_Place_Tip", "Klängen"),
        ("Klänge", "ChordShapes_Status_Reshaped", "Klänge"),
        ("Klänge", "Inspector_Task_Blockers", "Klänge"),
        ("Klänge", "Validation_SongEndUnverifiedNoAudio_One", "Klänge"),
        ("Klänge", "Validation_SongEndUnverifiedNoAudio_Many", "Klänge"),
        // These Töne are musical pitches, chart notes or chords, rather than rig presets.
        ("Töne", "Inspector_IssueFix_RefingerTip", "Töne"),
        ("Töne", "Inspector_IssueFix_MoveAnchorTip", "Töne"),
        ("Töne", "ChordTemplate_GenerateAllFingeringsTip", "Tönen"),
        ("Töne", "Settings_Tip_SuppressDoubleStopNames", "Töne"),
        ("Töne", "Dialog_GenerateLevels_Hint", "Töne"),
        ("Töne", "PatternSummary_CompanionSounds", "Töne"),
        ("Töne", "Pattern_Companion_Sounds", "Töne"),
        // Tone-Mapping describes the graphics operation.
        ("Tone", "Settings_Graphics_FilmicTonemap", "Tone-Mapping"),
        // Pane and chord options describe choices within controls.
        ("Optionen", "Dock_Pane_Menu", "optionen"),
        // The rail caption slot is 63 px; Einstellungen needs about 75 px.
        ("Optionen", "Nav_Settings_Rail", "Optionen"),
        // These actions restore files, snapshots, stems, profiles or built-in patterns.
        ("Wiederherstellen", "Dialog_StemRegenerate_Message", "wiederherstellen"),
        ("Wiederherstellen", "Profile_Import_Tip", "wiederherstellen"),
        ("Wiederherstellen", "Recovery_Title", "wiederherstellen"),
        ("Wiederherstellen", "Recovery_Notice", "Wiederherstellen"),
        ("Wiederherstellen", "Recovery_RestoreSelected", "wiederherstellen"),
        ("Wiederherstellen", "Checkpoints_Subtitle", "wiederherstellen"),
        ("Wiederherstellen", "Checkpoints_Restore", "Wiederherstellen"),
        ("Wiederherstellen", "Checkpoints_RestoreDiscardsHistoryTitle", "Wiederherstellen"),
        ("Wiederherstellen", "Checkpoints_RestoreDiscardsHistoryMessage", "Wiederherstellen"),
        ("Wiederherstellen", "Pattern_RestoreBuiltIns", "wiederherstellen"),
        // These paths locate files, model inference, API endpoints or branches of editing history.
        ("Pfad", "Diagnostics_Section_Paths", "Pfade"),
        ("Pfad", "Settings_LogFilePath", "Pfad"),
        ("Pfad", "Settings_Tip_LogFilePath", "Pfad"),
        ("Pfad", "Settings_StemModels_Desc_BsRoformerFp16", "pfad"),
        ("Pfad", "Settings_StemModels_Desc_HtDemucs6sOnnx", "pfad"),
        ("Pfad", "History_Note", "Pfad"),
        ("Pfad", "Settings_CrashReportOptIn_Hint", "pfade"),
        ("Pfad", "Ai_CodexPathLabel", "Pfad"),
        ("Pfad", "Ai_CliPathLabel", "Pfad"),
        ("Pfad", "Ai_BaseUrlDesc", "Pfad"),
        ("Pfad", "DcSong_ReadFailure_UnsafePath", "pfad"),
        // These pronouns refer to models, runs, chart objects, files or messages in the third person.
        ("Formelle Anrede", "Settings_StemModels_Subtitle", "Sie werden"),
        ("Formelle Anrede", "PlayMode_Results_Leaderboard_Detector_Tip", "Sie stehen"),
        ("Formelle Anrede", "EofLoss_UnplacedPhrase", "Sie ist"),
        ("Formelle Anrede", "EofLoss_LyricPitch", "Sie liegt"),
        ("Formelle Anrede", "EofLoss_AnchorOnCapo", "Sie liegt"),
        ("Formelle Anrede", "EofLoss_HandShapeShape", "Sie weicht"),
        ("Formelle Anrede", "EofLoss_NoteStringOutOfRange", "Sie liegt"),
        ("Formelle Anrede", "EofLoss_ChordStringOutOfRange", "Sie liegen"),
        ("Formelle Anrede", "EofLoss_GraceNote", "Sie lässt"),
        ("Formelle Anrede", "EofLoss_ChordGraceNote", "Sie lassen"),
        ("Formelle Anrede", "PhraseCatalog_Issue_StringOutOfRange", "Sie spielt"),
        ("Formelle Anrede", "PhraseCatalog_Issue_FretOutOfRange", "Sie spielt"),
        ("Formelle Anrede", "PhraseCatalog_Issue_FretBelowCapo", "Sie spielt"),
        ("Formelle Anrede", "PhraseCatalog_Issue_CapoDiffers", "Sie wurde"),
        ("Formelle Anrede", "ChordTemplate_Apply_SelectedTip", "Sie wechseln"),
        ("Formelle Anrede", "Dialog_Event_RsEvents_Desc", "Sie liegen"),
        ("Formelle Anrede", "AiChat_ReviewHint", "Sie sind"),
        ("Formelle Anrede", "AiChat_Arrangements_Tip", "Sie läuft"),
        ("Formelle Anrede", "DcSong_ReadFailure_Unreadable", "Sie fehlt"),
        ("Formelle Anrede", "DcSong_ReadFailure_ManifestMissing", "Ihr fehlt"),
        ("Formelle Anrede", "PlayMode_Setlist_Corrupt", "Sie wurde"),
        ("Formelle Anrede", "Status_GhostGems_Ghosted", "Sie bleiben"),
        ("Formelle Anrede", "AiChat_Leftovers_HeldHint", "Sie bleiben"),
        ("Formelle Anrede", "Status_LinkSuggestionsNone", "Sie wurden"),
        ("Formelle Anrede", "Shapes_Pending_DeleteMany", "Ihre Verwendungen")
    ];

    [Theory]
    [MemberData(nameof(Labels))]
    public void Labels_NameTheObjectAndAction(string key, string expected) =>
        Assert.Equal(expected, GermanValues.Value[key]);

    [Fact]
    public void Values_UseConsistentTerminologyAndInformalAddress()
    {
        var values = GermanValues.Value;
        foreach (var (policy, key, text) in Exceptions)
        {
            Assert.True(values.ContainsKey(key), $"Exception key {key} is missing.");
            Assert.Contains(text, values[key], StringComparison.Ordinal);
            Assert.Matches(Policies.Single(p => p.Name == policy).Pattern, text);
        }

        var exceptions = Exceptions.ToLookup(e => (e.Policy, e.Key));
        var offenders = new List<string>();
        foreach (var (key, value) in values)
        {
            foreach (var (name, pattern) in Policies)
            {
                var checkedValue = value;
                foreach (var exception in exceptions[(name, key)])
                    checkedValue = checkedValue.Replace(exception.Text, "", StringComparison.Ordinal);

                if (pattern.IsMatch(checkedValue))
                    offenders.Add($"{key}: {name}: {value}");
            }
        }

        Assert.True(offenders.Count == 0, "German terminology or address differs from the shared policy:\n" + string.Join("\n", offenders));
    }

    private static Dictionary<string, string> ReadGermanValues() =>
        XDocument.Load(Path.Combine(TestPaths.RepoRoot(), "DotCharter.Localization", "Resources.de-DE.resx"))
            .Root!.Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => d.Element("value")!.Value, StringComparer.Ordinal);
}
