using System.Security.Cryptography;
using DotCharter.Services.Export;
using DotCharter.Services.State;

namespace DotCharter.Services.Session;

/// <summary>Prepares a save snapshot's media references and settles them only after the project is written.</summary>
internal sealed class SaveAsMediaReferences(string? sourceProjectPath, string destinationProjectPath,
    bool deterministicCover = false) : IDisposable
{
    private readonly Dictionary<string, string> _stems = new(StringComparer.Ordinal);
    private string _sourceAudio = "";
    private string _audio = "";
    private string _sourceCover = "";
    private string? _copiedCover;
    private bool _committed;

    public void Rebase(ChartProject snapshot)
    {
        _sourceAudio = snapshot.AudioFileName;
        _audio = RebaseAudio(_sourceAudio, allowAlternate: true);
        snapshot.AudioFileName = _audio;
        foreach (var stem in snapshot.Stems)
        {
            var rebased = RebaseAudio(stem.Path);
            _stems[stem.Path] = rebased;
            stem.Path = rebased;
        }

        _sourceCover = snapshot.Chart.CoverArtPath;
        var sourceDirectory = Path.GetDirectoryName(sourceProjectPath);
        var destinationDirectory = Path.GetDirectoryName(destinationProjectPath);
        if (string.IsNullOrEmpty(sourceDirectory) || string.IsNullOrEmpty(destinationDirectory) ||
            ProjectStoreKeys.SamePath(sourceDirectory, destinationDirectory))
        {
            return;
        }

        var cover = ProjectRelativePath.TryResolve(sourceDirectory, _sourceCover);
        if (cover == null || !File.Exists(cover)) return;

        // Cover readers require a safe project-relative name. Use a fresh name so
        // Save As cannot overwrite another project's artwork in the target folder.
        Directory.CreateDirectory(destinationDirectory);
        var key = deterministicCover ? FileHash(cover) : Guid.NewGuid().ToString("N");
        var name = $"cover-{key}{Path.GetExtension(cover)}";
        var copiedCover = Path.Combine(destinationDirectory, name);
        if (deterministicCover && File.Exists(copiedCover))
        {
            if (!string.Equals(FileHash(copiedCover), key, StringComparison.Ordinal))
                throw new IOException("A checkpoint cover name is occupied by different content.");
            snapshot.Chart.CoverArtPath = name;
            return;
        }
        using (var source = File.OpenRead(cover))
        using (var target = new FileStream(copiedCover, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            _copiedCover = copiedCover;
            source.CopyTo(target);
            target.Flush(true);
        }
        // Content-addressed covers can be shared by several carried checkpoints.
        if (deterministicCover) _committed = true;
        snapshot.Chart.CoverArtPath = name;
    }

    private static string FileHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private string RebaseAudio(string reference, bool allowAlternate = false)
    {
        if (string.IsNullOrEmpty(sourceProjectPath) || string.IsNullOrWhiteSpace(reference) || Path.IsPathRooted(reference))
            return reference;
        var resolved = allowAlternate
            ? SaveCompanionService.ResolveAudio(Path.GetDirectoryName(Path.GetFullPath(sourceProjectPath))!, reference)
            : ProjectAudioLocator.TryResolve(sourceProjectPath, reference);
        return resolved == null || ProjectStoreKeys.SamePath(resolved, ProjectAudioLocator.TryResolve(destinationProjectPath, reference))
            ? reference
            : resolved;
    }

    public void Commit() => _committed = true;

    public void Settle(ProjectState state, ChartProject project)
    {
        if (state.CurrentAudioFileName == _sourceAudio) state.CurrentAudioFileName = _audio;
        if (project.AudioFileName == _sourceAudio) project.AudioFileName = _audio;
        foreach (var stem in project.Stems)
            if (_stems.TryGetValue(stem.Path, out var rebased)) stem.Path = rebased;
        if (_copiedCover != null && project.Chart.CoverArtPath == _sourceCover)
            project.Chart.CoverArtPath = Path.GetFileName(_copiedCover);
    }

    public void Dispose()
    {
        if (!_committed && _copiedCover != null) BestEffortDelete.File(_copiedCover);
    }
}
