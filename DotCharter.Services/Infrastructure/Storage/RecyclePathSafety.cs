namespace DotCharter.Services.Infrastructure.Storage;

/// <summary>Cloud exclusion over resolved local identities, never lexical aliases or host-specific path rules.</summary>
internal static class RecyclePathSafety
{
    internal static bool IsOutsideCloudRoot(string path, string folder, Func<string, string> canonicalize)
    {
        // Validate both inputs before any metadata resolver: an untrusted cloud-root namespace must not be probed.
        if (!TryNormalizeLocal(path, out _) || !TryNormalizeLocal(folder, out _)) return false;
        if (!TryCanonicalizeLocal(path, canonicalize, out var candidate) ||
            !TryCanonicalizeLocal(folder, canonicalize, out var cloud))
        {
            return false;
        }
        candidate = candidate.TrimEnd('\\');
        cloud = cloud.TrimEnd('\\');
        return !candidate.Equals(cloud, StringComparison.OrdinalIgnoreCase) &&
            !candidate.StartsWith(cloud + '\\', StringComparison.OrdinalIgnoreCase) &&
            !cloud.StartsWith(candidate + '\\', StringComparison.OrdinalIgnoreCase);
    }

    internal static bool HasSingleVolumeRoot(string root, IReadOnlyList<string> mounts) =>
        mounts.Count == 1 && mounts[0].Equals(root, StringComparison.OrdinalIgnoreCase);

    internal static bool TryCanonicalizeLocal(string path, Func<string, string> canonicalize, out string canonical)
    {
        canonical = "";
        if (!TryNormalizeLocal(path, out _)) return false;
        try { return TryNormalizeLocal(canonicalize(path), out canonical); }
        catch (Exception) { return false; }
    }

    internal static bool TryNormalizeLocal(string path, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrEmpty(path)) return false;
        var local = path.Replace('/', '\\');
        if (local.StartsWith(@"\\?\", StringComparison.Ordinal)) local = local[4..];
        if (local.Length is < 3 or >= 260 || !char.IsAsciiLetter(local[0]) || local[1] != ':' || local[2] != '\\')
            return false;
        var relative = local[3..].TrimEnd('\\');
        if (relative.IndexOfAny([':', '*', '?', '"', '<', '>', '|']) >= 0 || relative.Any(character => character < ' '))
            return false;
        if (relative.Length > 0 && relative.Split('\\').Any(component =>
            component.Length == 0 || component.EndsWith('.') || component.EndsWith(' ')))
        {
            return false;
        }
        normalized = local;
        return true;
    }
}
