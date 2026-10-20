using System.Text;

namespace DotCharter.Tone.Audio.Tonelab;

/// <summary>
///     The temp files <c>tone apply-file</c> points the game at. A tone body can be far
///     longer than a comfortable pipe line, so it travels as a path. The files live in
///     <c>&lt;LocalAppData&gt;\DotCharter\tonelab</c> (through <see cref="UserDataFolders" />,
///     so a <c>DOTCHARTER_DATA_ROOT</c> launch keeps them in its own root); each write
///     deletes the file before it once the game has answered, <see cref="Dispose" />
///     deletes the last, and opening the folder sweeps what a crashed run left behind.
/// </summary>
public sealed class TonelabToneFiles : IDisposable
{
    private readonly object _sync = new();
    private readonly List<string> _live = [];
    private int _counter;

    public TonelabToneFiles()
        : this(Path.Combine(UserDataFolders.LocalAppData, "DotCharter", "tonelab"))
    {
    }

    internal TonelabToneFiles(string directory) => Directory = directory;

    public string Directory { get; }

    /// <summary>Writes <paramref name="toneJson" /> (UTF-8, no BOM) to a fresh file and returns its full path.</summary>
    public string Write(string toneJson)
    {
        lock (_sync)
        {
            if (_counter == 0) SweepStale();
            System.IO.Directory.CreateDirectory(Directory);
            var path = Path.Combine(Directory, $"tone-{Environment.ProcessId}-{++_counter}.json");
            File.WriteAllText(path, toneJson, new UTF8Encoding(false));
            _live.Add(path);
            return path;
        }
    }

    /// <summary>Deletes every file this instance wrote except <paramref name="keep" /> (the one the game last loaded).</summary>
    public void ReleaseAllBut(string? keep)
    {
        lock (_sync)
        {
            for (var i = _live.Count - 1; i >= 0; i--)
            {
                if (string.Equals(_live[i], keep, StringComparison.OrdinalIgnoreCase)) continue;
                BestEffortDelete.File(_live[i]);
                _live.RemoveAt(i);
            }
        }
    }

    public void Dispose() => ReleaseAllBut(null);

    /// <summary>Files from a run that did not get to clean up: anything not written by this process.</summary>
    private void SweepStale()
    {
        if (!System.IO.Directory.Exists(Directory)) return;
        var mine = $"tone-{Environment.ProcessId}-";
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "tone-*.json"))
            if (!Path.GetFileName(file).StartsWith(mine, StringComparison.Ordinal)) BestEffortDelete.File(file);
    }
}
