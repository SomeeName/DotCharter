namespace DotCharter.UI.ViewModels.PlayMode.ToneSections;

/// <summary>One song and arrangement identity, without retaining a closed project.</summary>
internal sealed class ToneSectionOwner
{
    private readonly WeakReference<object>? _project;
    private readonly WeakReference<object>? _arrangement;
    private readonly string? _projectId;
    private readonly bool _hasAudio;
    private readonly long _audioRevision;

    public ToneSectionOwner(object? project, string? audioPath, object? arrangement, long audioRevision)
    {
        _projectId = (project as ChartProject)?.ProjectId;
        _audioRevision = audioRevision;
        _hasAudio = !string.IsNullOrWhiteSpace(audioPath);
        if (project is not null) _project = new WeakReference<object>(project);
        if (arrangement is not null) _arrangement = new WeakReference<object>(arrangement);
    }

    public bool SameSong(ToneSectionOwner other)
    {
        var sameProject = SameTarget(_project, other._project) ||
            !string.IsNullOrEmpty(_projectId) && string.Equals(_projectId, other._projectId, StringComparison.Ordinal);
        // Saving may normalize or relocate every path without changing the song.
        // Actual replacements are committed explicitly, including same-path ones.
        return sameProject && (!_hasAudio || _audioRevision == other._audioRevision);
    }

    public bool SameArrangement(ToneSectionOwner other) => SameTarget(_arrangement, other._arrangement);

    private static bool SameTarget(WeakReference<object>? left, WeakReference<object>? right)
    {
        if (left is null || right is null) return left is null && right is null;
        return left.TryGetTarget(out var a) && right.TryGetTarget(out var b) &&
            ReferenceEquals(a, b);
    }
}
