namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     The DotCharter.UI SOURCE TREE is two projects since the tone rig moved out:
///     <c>DotCharter.Tone.UI</c> holds the rig's views and view models, the Fire and
///     Dsp controls and the design-system styles, in <c>DotCharter.Tone.UI.*</c>
///     namespaces that MIRROR <c>DotCharter.UI.*</c>, and mirrors the folder layout too
///     (<c>Views/PlayMode/AmpFxRigView.axaml</c> sits at the same relative path under
///     either root; <see cref="UiNamespace" /> folds the namespaces the same way).
///     <para>
///         A lint that walks <c>DotCharter.UI/Views</c> and silently stopped seeing
///         the rig would pass vacuously for exactly the surface the split was about.
///         So the source lints walk through here: a directory under
///         <c>DotCharter.UI</c> is enumerated together with its mirror under
///         <c>DotCharter.Tone.UI</c>, and a file that is not under the first is read
///         from the second. A path outside <c>DotCharter.UI</c> is untouched.
///     </para>
/// </summary>
internal static class UiSourceTree
{
    private const string Ui = "DotCharter.UI";
    private const string ToneUi = "DotCharter.Tone.UI";

    /// <summary>The same path under DotCharter.Tone.UI, or null when
    /// <paramref name="path" /> is not under DotCharter.UI.</summary>
    public static string? Mirror(string path)
    {
        var sep = Path.DirectorySeparatorChar;
        var normal = path.Replace('/', sep);
        var marker = sep + Ui + sep;
        var at = normal.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (at >= 0) return normal[..at] + sep + ToneUi + sep + normal[(at + marker.Length)..];
        var tail = sep + Ui;
        return normal.EndsWith(tail, StringComparison.OrdinalIgnoreCase)
            ? normal[..^Ui.Length] + ToneUi
            : null;
    }

    /// <summary>
    ///     <paramref name="ns" /> with a leading <c>DotCharter.Tone.UI</c> read as
    ///     <c>DotCharter.UI</c>, so a lint that maps a view's namespace to its folder
    ///     (or groups views by it) sees the rig's views where it sees the app's.
    /// </summary>
    public static string? UiNamespace(string? ns) =>
        ns is not null && (ns == ToneUi || ns.StartsWith(ToneUi + ".", StringComparison.Ordinal))
            ? Ui + ns[ToneUi.Length..]
            : ns;

    public static string[] GetFiles(string dir) => GetFiles(dir, "*");

    public static IEnumerable<string> EnumerateFiles(string dir) => EnumerateFiles(dir, "*");

    public static string[] GetFiles(string dir, string pattern) =>
        GetFiles(dir, pattern, SearchOption.TopDirectoryOnly);

    public static string[] GetFiles(string dir, string pattern, SearchOption option) =>
        EnumerateFiles(dir, pattern, option).ToArray();

    public static IEnumerable<string> EnumerateFiles(string dir, string pattern) =>
        EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly);

    public static IEnumerable<string> EnumerateFiles(string dir, string pattern, SearchOption option)
    {
        var mirror = Mirror(dir);
        var first = Directory.Exists(dir) || mirror is null
            ? Directory.EnumerateFiles(dir, pattern, option)
            : [];
        return mirror is not null && Directory.Exists(mirror)
            ? first.Concat(Directory.EnumerateFiles(mirror, pattern, option))
            : first;
    }

    public static IEnumerable<string> EnumerateFiles(string dir, string pattern, EnumerationOptions options)
    {
        var mirror = Mirror(dir);
        var first = Directory.Exists(dir) || mirror is null
            ? Directory.EnumerateFiles(dir, pattern, options)
            : [];
        return mirror is not null && Directory.Exists(mirror)
            ? first.Concat(Directory.EnumerateFiles(mirror, pattern, options))
            : first;
    }

    /// <summary>The file, or its mirror when it moved to DotCharter.Tone.UI.</summary>
    public static string Resolve(string path)
    {
        if (File.Exists(path)) return path;
        var mirror = Mirror(path);
        return mirror is not null && File.Exists(mirror) ? mirror : path;
    }

    /// <summary>
    ///     <paramref name="file" /> relative to <paramref name="uiDir" /> — or, for a
    ///     file under the mirror, relative to the mirror — so a list keyed by
    ///     "Styles/Fire/FireRigBoard.axaml" matches in whichever project the file lives.
    /// </summary>
    public static string Relative(string uiDir, string file)
    {
        var full = Path.GetFullPath(file);
        var mirror = Mirror(uiDir);
        if (mirror is not null)
        {
            var m = Path.GetFullPath(mirror).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (full.StartsWith(m, StringComparison.OrdinalIgnoreCase)) return Path.GetRelativePath(mirror, full);
        }
        return Path.GetRelativePath(uiDir, full);
    }

    public static string ReadAllText(string path) => File.ReadAllText(Resolve(path));

    public static string[] ReadAllLines(string path) => File.ReadAllLines(Resolve(path));
}
