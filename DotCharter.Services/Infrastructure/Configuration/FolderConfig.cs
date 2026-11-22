namespace DotCharter.Services.Infrastructure.Configuration;

/// <summary>
///     The folder each app-level file dialog opens in (the folder of the last pick of that
///     kind; empty = none yet). Read through <c>PickerDirectoryMemory.StartDirectory</c>, which
///     falls back to the dialog's own default when the folder is missing. The older
///     per-purpose folders (<c>LastAudioDir</c> and the like) stay on <see cref="AppConfig" />.
/// </summary>
public class FolderConfig : RigConfig
{
    public string LastProfileDir { get; set; } = string.Empty;
    public string LastProfilePictureDir { get; set; } = string.Empty;
    public string LastDiagnosticsDir { get; set; } = string.Empty;
    public string LastBackupDir { get; set; } = string.Empty;
    public string LastLyricsDir { get; set; } = string.Empty;
    public string LastChartDiffDir { get; set; } = string.Empty;
    public string LastConversionOutputDir { get; set; } = string.Empty;
    public string LastDcSongDir { get; set; } = string.Empty;
}
