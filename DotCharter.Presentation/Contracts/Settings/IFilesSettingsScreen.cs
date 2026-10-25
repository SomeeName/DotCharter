using System.ComponentModel;
using System.Windows.Input;

namespace DotCharter.Presentation.Contracts.Settings;

/// <summary>
///     Settings ▸ Import &amp; export: the files a save writes next to the
///     project, the timed project backup, moving the whole setup to another
///     machine, and the decoded-audio cache that opening a Rocksmith archive
///     fills. The cache readout and the backup status are status, never
///     settings.
/// </summary>
public interface IFilesSettingsScreen : INotifyPropertyChanged
{
    // ── save companions ──────────────────────────────────────────────────

    bool SaveCompanionEof { get; set; }

    bool SaveCompanionEofAudio { get; set; }

    bool SaveCompanionPsarc { get; set; }

    /// <summary>0 PCM, 1 IMA ADPCM.</summary>
    int SaveCompanionPsarcCodecIndex { get; set; }

    bool SaveCompanionRs2Dlc { get; set; }

    /// <summary>Save a 7-/8-string guitar's <c>.xml</c> as a six-string arrangement for EOF and DLC Builder.</summary>
    bool RsXmlSixStringProjection { get; set; }

    /// <summary>Write a phrase that starts on a note between two beats as moveR&lt;N&gt; on the beat before it, in the saved <c>.xml</c> and the DTRS XML bundle.</summary>
    bool RsXmlMoverPhrases { get; set; }

    /// <summary>Generate dynamic difficulty, on a copy, for every single-level arrangement a Rocksmith export writes.</summary>
    bool ExportGenerateDynamicDifficulty { get; set; }

    /// <summary>Count each phrase's levels with DLC Builder's trained model instead of its Simple count.</summary>
    bool ExportDdUseMlModel { get; set; }

    /// <summary>Apply DLC Builder's arrangement fixes, on a copy, to every arrangement a Rocksmith export writes.</summary>
    bool ExportApplyDlcBuilderFixes { get; set; }

    // ── opening Rocksmith songs ──────────────────────────────────────────

    /// <summary>Open a .psarc with only its hardest difficulty level.</summary>
    bool PsarcOpenHardestLevelOnly { get; set; }

    // ── Guitar Pro import ────────────────────────────────────────────────

    bool GpImportTruncateShortNotes { get; set; }

    bool GpImportTruncateShortChords { get; set; }

    bool GpImportPreserveShortRests { get; set; }

    bool OnlineLatinNameLookup { get; set; }

    bool GpImportBeatTextTechniques { get; set; }

    bool GpImportMarkersToSections { get; set; }

    bool GpImportTimeSignatures { get; set; }

    bool GpImportDropMidBeatTempoChanges { get; set; }

    /// <summary>Frets below or above the target a slide-in starts from, 1 to 3.</summary>
    int GpImportSlideInFretDistance { get; set; }

    // ── backups ──────────────────────────────────────────────────────────

    bool AutoBackupEnabled { get; set; }

    int AutoBackupIntervalMinutes { get; set; }

    /// <summary>Auto-backups kept per project, newest first; 0 keeps every one.</summary>
    int AutoBackupKeepCount { get; set; }

    IBackupMigratePart Backup { get; }

    // ── decoded song audio ───────────────────────────────────────────────

    /// <summary>"412 songs, 6.1 GB of 8.0 GB", or that the cache is empty; blank before the first measure.</summary>
    string LibraryAudioCacheUsage { get; }

    bool LibraryAudioCacheHasEntries { get; }

    /// <summary>What the last clear did, or why it failed.</summary>
    string LibraryAudioCacheDetail { get; }

    bool HasLibraryAudioCacheDetail { get; }

    ICommand ClearLibraryAudioCacheCommand { get; }

    ICommand RefreshLibraryAudioCacheCommand { get; }

    // ── search ───────────────────────────────────────────────────────────

    /// <summary>Row and group keys the current search filters OUT. Empty when there is no query.</summary>
    IReadOnlySet<string> HiddenKeys { get; }
}

/// <summary>
///     Export the user's whole setup to one zip, or import one back. An import
///     replaces files behind the services that cache them, so the page reloads
///     every tab afterwards.
/// </summary>
public interface IBackupMigratePart : INotifyPropertyChanged
{
    ICommand ExportCommand { get; }

    ICommand ImportCommand { get; }

    bool IsBusy { get; }

    /// <summary>What the last export or import did, or why it failed; empty before one.</summary>
    string StatusText { get; }

    bool HasStatus { get; }
}
