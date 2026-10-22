using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Infrastructure;

/// <summary>In-memory <see cref="IFileStorageService"/>: every picker returns
/// the preconfigured path (or null to simulate cancel) and records the
/// requested titles — and the start directories — for assertions.</summary>
public sealed class FakeFileStorageService : IFileStorageService
{
    public string? NextOpenFile { get; set; }
    public string? NextFolder { get; set; }

    /// <summary>What the multi-select picker returns; null falls back to <see cref="NextOpenFile" /> alone.</summary>
    public IReadOnlyList<string>? NextOpenFiles { get; set; }
    public string? NextSaveFile { get; set; }
    public List<string> RequestedTitles { get; } = [];

    /// <summary>The <c>startingDirectory</c> each picker was opened at, in call
    /// order (null when the caller passed none). A picker that starts nowhere
    /// drops the user wherever the shell last was, which is the whole of the
    /// "why does this dialog forget my folder" class of complaint — and it was
    /// invisible to tests until this recorded it.</summary>
    public List<string?> RequestedStartDirectories { get; } = [];

    /// <summary>The filters each file picker offered, in call order (null when
    /// the caller passed none — an unfiltered folder listing to hunt in).</summary>
    public List<IReadOnlyList<FilePickerFilter>?> RequestedFilters { get; } = [];

    public Task<string?> OpenFilePickerAsync(string title, IReadOnlyList<FilePickerFilter>? filters = null,
        string? startingDirectory = null)
    {
        RequestedTitles.Add(title);
        RequestedStartDirectories.Add(startingDirectory);
        RequestedFilters.Add(filters);
        return Task.FromResult(NextOpenFile);
    }

    public async Task<IReadOnlyList<string>> OpenFilesPickerAsync(string title, IReadOnlyList<FilePickerFilter>? filters = null,
        string? startingDirectory = null)
    {
        if (NextOpenFiles is null)
        {
            var single = await OpenFilePickerAsync(title, filters, startingDirectory);
            return single is null ? [] : [single];
        }

        RequestedTitles.Add(title);
        RequestedStartDirectories.Add(startingDirectory);
        RequestedFilters.Add(filters);
        return NextOpenFiles;
    }

    public Task<string?> OpenFolderPickerAsync(string title, string? startingDirectory = null)
    {
        RequestedTitles.Add(title);
        RequestedStartDirectories.Add(startingDirectory);
        return Task.FromResult(NextFolder);
    }

    public Task<string?> SaveFilePickerAsync(string title, string suggestedFileName,
        IReadOnlyList<FilePickerFilter>? filters = null, string? startingDirectory = null)
    {
        RequestedTitles.Add(title);
        RequestedStartDirectories.Add(startingDirectory);
        return Task.FromResult(NextSaveFile);
    }
}
