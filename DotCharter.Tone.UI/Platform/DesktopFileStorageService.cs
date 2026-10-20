using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;

namespace DotCharter.Tone.UI.Platform;

public class DesktopFileStorageService : IFileStorageService
{
    public async Task<string?> OpenFilePickerAsync(string title, IReadOnlyList<FilePickerFilter>? filters = null,
        string? startingDirectory = null)
    {
        var window = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (window == null) return null;

        var options = new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false
        };

        if (filters is { Count: > 0 })
        {
            options.FileTypeFilter = filters
                .Select(f => new FilePickerFileType(f.Name) { Patterns = f.Patterns.ToList() })
                .ToList();
        }

        options.SuggestedStartLocation = await TryResolveFolderAsync(window, startingDirectory);

        // A picked entry that vanished between selection and result construction
        // (e.g. the user typed / picked a just-deleted folder) makes Avalonia's
        // BclStorage throw "Directory must exist" from a background continuation,
        // which crashes the app. Degrade to "nothing selected" instead.
        try
        {
            var result = await window.StorageProvider.OpenFilePickerAsync(options);
            return result.Count > 0 ? result[0].Path.LocalPath : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public async Task<string?> OpenFolderPickerAsync(string title, string? startingDirectory = null)
    {
        var window = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (window == null) return null;

        var startLocation = await TryResolveFolderAsync(window, startingDirectory);
        try
        {
            var result = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = title,
                SuggestedStartLocation = startLocation
            });

            return result.Count > 0 ? result[0].Path.LocalPath : null;
        }
        catch (ArgumentException)
        {
            // Deleted / non-existent folder selected — see OpenFilePickerAsync.
            return null;
        }
    }

    public async Task<string?> SaveFilePickerAsync(string title, string suggestedFileName,
        IReadOnlyList<FilePickerFilter>? filters = null, string? startingDirectory = null)
    {
        var window = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (window == null) return null;

        var options = new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedFileName,
            ShowOverwritePrompt = true
        };

        if (filters is { Count: > 0 })
        {
            options.FileTypeChoices = filters
                .Select(f => new FilePickerFileType(f.Name) { Patterns = f.Patterns.ToList() })
                .ToList();
        }

        options.SuggestedStartLocation = await TryResolveFolderAsync(window, startingDirectory);

        try
        {
            var result = await window.StorageProvider.SaveFilePickerAsync(options);
            return result?.Path.LocalPath;
        }
        catch (ArgumentException)
        {
            // Deleted / non-existent target folder — see OpenFilePickerAsync.
            return null;
        }
    }

    private static async Task<IStorageFolder?> TryResolveFolderAsync(TopLevel topLevel, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var dir = path;
            if (File.Exists(dir)) dir = Path.GetDirectoryName(dir);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;
            return await topLevel.StorageProvider.TryGetFolderFromPathAsync(dir);
        }
        catch
        {
            return null;
        }
    }
}
