namespace DotCharter.Services.Audio.StemSeparation.Components;

/// <summary>
/// Pure scanner over a set of <see cref="ComponentDefinition"/>s. It resolves
/// each definition's file against an injected <see cref="IComponentFileSystem"/>
/// and reports installed / missing / active status plus size. It performs NO
/// I/O of its own and holds no configuration — the caller supplies the
/// definitions (see <see cref="ComponentCatalog"/>) and the filesystem seam,
/// which makes the whole thing trivially unit-testable with a fake filesystem.
/// </summary>
public sealed class ComponentInventory
{
    /// <summary>Fractional tolerance for the advisory size check (±25%).</summary>
    private const double SizeTolerance = 0.25;

    private readonly IComponentFileSystem _fs;

    public ComponentInventory(IComponentFileSystem fs) => _fs = fs ?? throw new ArgumentNullException(nameof(fs));

    /// <summary>Scan every definition and return a status for each (order preserved).</summary>
    public IReadOnlyList<ComponentStatus> Scan(IEnumerable<ComponentDefinition> definitions)
    {
        if (definitions == null) throw new ArgumentNullException(nameof(definitions));
        var results = new List<ComponentStatus>();
        foreach (var def in definitions)
            results.Add(ScanOne(def));
        return results;
    }

    /// <summary>Scan a single definition.</summary>
    public ComponentStatus ScanOne(ComponentDefinition def)
    {
        if (def == null) throw new ArgumentNullException(nameof(def));

        var path = Resolve(def);
        if (path == null)
        {
            return new ComponentStatus { Definition = def, State = ComponentState.Missing };
        }

        var size = _fs.GetFileSize(path);
        var state = def.IsActiveSelection ? ComponentState.Active : ComponentState.Installed;
        return new ComponentStatus
        {
            Definition = def,
            State = state,
            ResolvedPath = path,
            SizeBytes = size < 0 ? 0 : size,
        };
    }

    /// <summary>
    /// Verify a component's file: presence, size (vs. the approximate expected)
    /// and, when <paramref name="computeHash"/> is set, its SHA-256.
    /// </summary>
    public ComponentVerification Verify(ComponentDefinition def, bool computeHash = false)
    {
        if (def == null) throw new ArgumentNullException(nameof(def));

        var path = Resolve(def);
        if (path == null)
        {
            return new ComponentVerification
            {
                ComponentId = def.Id,
                Exists = false,
                ExpectedBytes = def.ExpectedBytes,
                SizeLooksRight = false,
            };
        }

        var size = _fs.GetFileSize(path);
        if (size < 0) size = 0;
        var sizeOk = SizeLooksRight(size, def.ExpectedBytes);

        return new ComponentVerification
        {
            ComponentId = def.Id,
            Exists = true,
            Path = path,
            SizeBytes = size,
            ExpectedBytes = def.ExpectedBytes,
            SizeLooksRight = sizeOk,
            Sha256 = computeHash ? _fs.ComputeSha256(path) : null,
        };
    }

    /// <summary>
    /// First existing file for the definition, or null. Search order:
    /// each <see cref="ComponentDefinition.SearchDirectories"/> (× each file
    /// name), then — when enabled — each PATH directory.
    /// </summary>
    private string? Resolve(ComponentDefinition def)
    {
        foreach (var dir in def.SearchDirectories)
        {
            var hit = ProbeDirectory(dir, def.FileNames);
            if (hit != null) return hit;
        }

        if (def.SearchPath)
        {
            foreach (var dir in _fs.PathDirectories)
            {
                var hit = ProbeDirectory(dir, def.FileNames);
                if (hit != null) return hit;
            }
        }

        return null;
    }

    private string? ProbeDirectory(string? dir, IReadOnlyList<string> fileNames)
    {
        if (string.IsNullOrWhiteSpace(dir)) return null;
        foreach (var name in fileNames)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            string candidate;
            try { candidate = Path.Combine(dir, name); }
            catch { continue; } // malformed dir/name
            if (_fs.FileExists(candidate)) return candidate;
        }
        return null;
    }

    internal static bool SizeLooksRight(long actual, long expected)
    {
        if (expected <= 0) return actual > 0;   // no expectation → just non-empty
        if (actual <= 0) return false;
        var low = expected * (1 - SizeTolerance);
        var high = expected * (1 + SizeTolerance);
        return actual >= low && actual <= high;
    }
}
