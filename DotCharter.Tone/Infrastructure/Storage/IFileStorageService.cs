namespace DotCharter.Tone.Infrastructure.Storage;

public record FilePickerFilter(string Name, IReadOnlyList<string> Patterns);

public interface IFileStorageService
{
    Task<string?> OpenFilePickerAsync(string title, IReadOnlyList<FilePickerFilter>? filters = null,
        string? startingDirectory = null);
    Task<string?> OpenFolderPickerAsync(string title, string? startingDirectory = null);

    /// <summary>Shows a save-file dialog. Returns the chosen full path or
    /// null when the user cancels.</summary>
    Task<string?> SaveFilePickerAsync(string title, string suggestedFileName,
        IReadOnlyList<FilePickerFilter>? filters = null, string? startingDirectory = null);
}
