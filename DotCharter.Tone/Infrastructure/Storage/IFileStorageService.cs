namespace DotCharter.Tone.Infrastructure.Storage;

public record FilePickerFilter(string Name, IReadOnlyList<string> Patterns);

public interface IFileStorageService
{
    Task<string?> OpenFilePickerAsync(string title, IReadOnlyList<FilePickerFilter>? filters = null,
        string? startingDirectory = null);

    /// <summary>Shows an open-file dialog that accepts several files: the chosen full paths, empty
    /// when the user cancels. A storage service without a multi-select picker offers one file.</summary>
    async Task<IReadOnlyList<string>> OpenFilesPickerAsync(string title, IReadOnlyList<FilePickerFilter>? filters = null,
        string? startingDirectory = null)
    {
        var path = await OpenFilePickerAsync(title, filters, startingDirectory);
        return path is null ? [] : [path];
    }

    Task<string?> OpenFolderPickerAsync(string title, string? startingDirectory = null);

    /// <summary>Shows a save-file dialog. Returns the chosen full path or
    /// null when the user cancels.</summary>
    Task<string?> SaveFilePickerAsync(string title, string suggestedFileName,
        IReadOnlyList<FilePickerFilter>? filters = null, string? startingDirectory = null);
}
