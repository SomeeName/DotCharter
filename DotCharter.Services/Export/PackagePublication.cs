namespace DotCharter.Services.Export;

/// <summary>Stages a package set and restores prior destinations if any publication fails.</summary>
internal sealed class PackagePublication : IDisposable
{
    private readonly List<Target> _targets = [];
    private readonly Action<string, string> _publish;
    private readonly IAppLogger? _logger;
    private bool _settled;

    public PackagePublication(Action<string, string>? publish = null, IAppLogger? logger = null)
    {
        _publish = publish ?? ((stage, path) => AtomicFile.Publish(stage, path));
        _logger = logger;
    }

    public string Stage(string path)
    {
        var destination = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var stage = destination + "." + Guid.NewGuid().ToString("N") + ".stage";
        _targets.Add(new Target(stage, destination));
        return stage;
    }

    public void Commit(CancellationToken cancellationToken)
    {
        try
        {
            foreach (var target in _targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AtomicFile.FlushFile(target.Stage);
                if (!File.Exists(target.Path)) continue;
                target.Backup = target.Path + "." + Guid.NewGuid().ToString("N") + ".previous";
                File.Copy(target.Path, target.Backup, overwrite: false);
                AtomicFile.FlushFile(target.Backup);
            }

            cancellationToken.ThrowIfCancellationRequested();
            // Once publication starts, finish or roll back the entire set.
            foreach (var target in _targets)
            {
                _publish(target.Stage, target.Path);
                target.Published = true;
            }
            _settled = true;
        }
        catch (Exception failure)
        {
            var failures = new List<Exception> { failure };
            foreach (var target in _targets.AsEnumerable().Reverse().Where(t => t.Published))
            {
                try
                {
                    Restore(target);
                }
                catch (Exception rollbackFailure)
                {
                    failures.Add(rollbackFailure);
                    _logger?.Error(AppLogCategory.Project,
                        $"Package rollback could not restore '{target.Path}'; original retained at '{target.Backup}'.", rollbackFailure);
                }
            }
            if (failures.Count > 1)
                throw new AggregateException(failures);
            _settled = true;
            throw;
        }
    }

    private static void Restore(Target target)
    {
        var backupPath = target.Backup;
        if (backupPath == null)
        {
            File.Delete(target.Path);
            return;
        }

        AtomicFile.Write(target.Path, output =>
        {
            using var backup = File.OpenRead(backupPath);
            backup.CopyTo(output);
        });
    }

    public void Dispose()
    {
        foreach (var target in _targets)
        {
            BestEffortDelete.File(target.Stage);
            if (_settled && target.Backup != null) BestEffortDelete.File(target.Backup);
        }
    }

    private sealed class Target(string stage, string path)
    {
        public string Stage { get; } = stage;
        public string Path { get; } = path;
        public string? Backup { get; set; }
        public bool Published { get; set; }
    }
}
