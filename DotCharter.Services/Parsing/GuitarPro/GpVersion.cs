using System.Globalization;

namespace DotCharter.Services.Parsing.GuitarPro;

/// <summary>
/// Parsed Guitar Pro file format version, with the format-quirk flags that
/// drive the binary reader's branching. Lifted out of
/// <see cref="GpLegacyImporter"/> so the (subtle) version rules can be
/// unit-tested without spinning up a binary parser.
///
/// Encoding notes:
/// <list type="bullet">
/// <item>"5.10" in the version string is GP 5.1.0 (the post-5.0 update),
///   NOT 5 + 0.10 — Guitar Pro shipped a layout/format change with that
///   release that callers must branch on.</item>
/// <item>Version 3.0 is the oldest supported. Anything below 3 / above 5 is
///   the caller's problem (out of scope for the legacy importer).</item>
/// </list>
/// </summary>
internal readonly struct GpVersion
{
    public int Major { get; }
    public int Minor { get; }

    public GpVersion(int major, int minor)
    {
        Major = major;
        Minor = minor;
    }

    /// <summary>True for GP 5.10 and later (the post-5.0 layout update).</summary>
    public bool IsAfter50 => Major > 5 || (Major == 5 && Minor >= 10);

    /// <summary>True for any 5.x version.</summary>
    public bool IsVersion5 => Major >= 5;

    /// <summary>True for GP 4 or any later major.</summary>
    public bool IsVersion4Plus => Major >= 4;

    /// <summary>
    /// Parse a version string of the form "X" or "X.Y" (the suffix that
    /// follows the last 'v' in the GP file's version banner, e.g.
    /// "FICHIER GUITAR PRO v5.10" → "5.10").
    /// </summary>
    public static bool TryParse(string s, out GpVersion version)
    {
        version = default;
        if (string.IsNullOrEmpty(s)) return false;

        var dot = s.IndexOf('.');
        int major;
        var minor = 0;
        if (dot < 0)
        {
            if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out major))
                return false;
        }
        else
        {
            if (!int.TryParse(s.Substring(0, dot), NumberStyles.Integer, CultureInfo.InvariantCulture, out major) ||
                !int.TryParse(s.Substring(dot + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out minor))
            {
                return false;
            }
        }

        version = new GpVersion(major, minor);
        return true;
    }
}
