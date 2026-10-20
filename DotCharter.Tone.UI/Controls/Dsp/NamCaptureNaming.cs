namespace DotCharter.Tone.UI.Controls.Dsp;

/// <summary>
/// How a neural capture presents itself on an amp faceplate: the model name, who
/// captured it, the file format and the model size tier. Every field is
/// display-only and may be empty — a capture picked from an arbitrary folder has
/// no sidecar, so only what the file NAME can prove survives.
/// </summary>
public readonly record struct NamCaptureIdentity(
    string Title,
    string Author,
    string Format,
    string SizeTier)
{
    public static readonly NamCaptureIdentity None = new(string.Empty, string.Empty, string.Empty, string.Empty);

    public bool HasTitle => Title.Length > 0;
    public bool HasAuthor => Author.Length > 0;

    /// <summary>The little stamped chip beside the model name: the format, plus
    /// the size tier when it is known ("NAM · Lite"). Empty when neither is.
    /// Proper nouns — deliberately NOT localized.</summary>
    public string FormatLabel => (Format.Length, SizeTier.Length) switch
    {
        (0, 0) => string.Empty,
        (_, 0) => Format,
        (0, _) => SizeTier,
        _ => Format + " · " + SizeTier
    };
}

/// <summary>
/// Pure naming rules that turn a neural-capture FILE PATH (plus, when the local
/// library has one, its manifest) into the faceplate identity a NAM amp head
/// prints on its model plate.
///
/// <para>Why the file name has to carry its own weight: the amp face is the honest
/// place to say WHICH capture is loaded, and a capture is only in the local
/// library when it came from TONE3000 or shipped with the app. One picked
/// straight off disk through the file picker has no manifest at all — but its
/// name almost always does (<c>NamCaptureIndex.BuildFileName</c> writes
/// <c>{modelId}-{slug}-{tier}.nam</c>, and hand-made captures follow the same
/// dash-slug habit), so de-slugging it beats printing a raw path or a generic
/// "Neural Amp".</para>
///
/// <para>No Avalonia types and no Services types — the manifest values are passed
/// IN as plain strings, so this is unit-tested directly
/// (<c>NamCaptureNamingTests</c>) and cannot drag a service dependency onto a
/// view model. Throw-free for every input, including null, empty and paths with
/// characters the platform dislikes: a faceplate that throws during layout is
/// worse than one that says nothing.</para>
/// </summary>
public static class NamCaptureNaming
{
    /// <summary>The model size tiers TONE3000 publishes plus the two architecture
    /// names captures are commonly suffixed with. Matched case-insensitively at
    /// the END of a slug and lifted out of the title, because "Deluxe Reverb
    /// Clean Lite" is not the name of an amp.</summary>
    private static readonly string[] SizeTiers =
        ["nano", "feather", "lite", "standard", "lstm", "wavenet"];

    /// <summary>Resolves the faceplate identity, manifest first. Every manifest
    /// field is optional: a blank one falls back to what the file name proves, so
    /// a half-filled sidecar degrades field by field instead of all at once.</summary>
    public static NamCaptureIdentity Resolve(
        string? modelPath,
        string? manifestTitle = null,
        string? manifestAuthor = null,
        string? manifestFormat = null,
        string? manifestSizeTier = null)
    {
        var fromName = FromFileName(modelPath);
        if (!fromName.HasTitle && string.IsNullOrWhiteSpace(manifestTitle))
            return NamCaptureIdentity.None;

        return new NamCaptureIdentity(
            Pick(manifestTitle, fromName.Title),
            Clean(manifestAuthor),
            FormatLabelFor(Pick(manifestFormat, fromName.Format)),
            TierLabel(Pick(manifestSizeTier, fromName.SizeTier)));
    }

    /// <summary>The identity a bare file path can prove on its own: the de-slugged
    /// model name, the size tier when the name ends in one, and the format from
    /// the extension. Author is never derivable — only a manifest knows it.</summary>
    public static NamCaptureIdentity FromFileName(string? modelPath)
    {
        var name = FileStem(modelPath);
        if (name.Length == 0) return NamCaptureIdentity.None;

        var ext = FormatLabelFor(Extension(modelPath));

        // Preserve the established numeric-id format (including short ids).
        // Bare numeric amp names are ambiguous; manifest titles take precedence.
        // Id-less downloads use the explicit x-prefixed hash instead.
        var dash = name.IndexOf('-');
        if (dash > 0 && dash < name.Length - 1 && IsDownloadPrefix(name.AsSpan(0, dash)))
            name = name[(dash + 1)..];

        var tier = string.Empty;
        foreach (var candidate in SizeTiers)
        {
            var suffix = "-" + candidate;
            if (name.Length <= suffix.Length) continue;
            if (!name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
            tier = candidate;
            name = name[..^suffix.Length];
            break;
        }

        return new NamCaptureIdentity(TitleCase(name), string.Empty, ext, tier);
    }

    /// <summary>Does this leading segment look like something
    /// <c>NamCaptureIndex.FileNamePrefix</c> wrote? A run of digits (a
    /// TONE3000 model id), or <c>x</c> followed by eight hex characters (the
    /// id-less fallback).</summary>
    private static bool IsDownloadPrefix(ReadOnlySpan<char> segment)
    {
        if (segment.Length > 0 && segment.IsAllAsciiDigits()) return true;
        if (segment.Length != 9 || (segment[0] != 'x' && segment[0] != 'X')) return false;
        foreach (var c in segment[1..])
        {
            if (!char.IsAsciiHexDigit(c))
                return false;
        }

        return true;
    }

    /// <summary>Display spelling for a capture format. TONE3000 uses several
    /// (<c>aida-x</c>/<c>keras</c>/<c>h5</c>), and the extension is what the
    /// neural loader actually dispatches on, so both funnel to one label.</summary>
    public static string FormatLabelFor(string? format)
    {
        var f = (format ?? string.Empty).Trim().TrimStart('.').ToLowerInvariant();
        if (f.Length == 0) return string.Empty;
        if (f is "nam") return "NAM";
        if (f is "aidax" or "aida-x" or "aida_x" or "keras" or "h5" || f.Contains("aida", StringComparison.Ordinal))
            return "AIDA-X";
        if (f is "ir" or "wav" or "impulse" or "impulse-response" or "cab") return "IR";
        return f.ToUpperInvariant();
    }

    /// <summary>Title-cases the size tier for the chip ("lstm" is an acronym and
    /// stays upper; "feather" is a word and gets one capital).</summary>
    private static string TierLabel(string? tier)
    {
        var t = (tier ?? string.Empty).Trim();
        if (t.Length == 0) return string.Empty;
        return t.Equals("lstm", StringComparison.OrdinalIgnoreCase) ? "LSTM"
            : t.Equals("wavenet", StringComparison.OrdinalIgnoreCase) ? "WaveNet"
            : TitleCase(t);
    }

    private static string Pick(string? preferred, string fallback)
        => string.IsNullOrWhiteSpace(preferred) ? fallback : preferred.Trim();

    private static string Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    /// <summary>Dash/underscore slug → spaced Title Case. A token that is already
    /// mixed- or all-upper ("DI", "JCM800", "AC30") is left ALONE — lower-casing
    /// it would invent a spelling the capture's author did not use.</summary>
    private static string TitleCase(string slug)
    {
        var parts = slug.Split(['-', '_', ' '], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return string.Empty;

        var words = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            var hasUpper = false;
            foreach (var c in part)
                if (char.IsUpper(c)) { hasUpper = true; break; }

            words.Add(hasUpper
                ? part
                : char.ToUpperInvariant(part[0]) + part[1..]);
        }

        return string.Join(' ', words);
    }

    private static string FileStem(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try
        {
            var stem = Path.GetFileNameWithoutExtension(path.Trim());
            return string.IsNullOrWhiteSpace(stem) ? string.Empty : stem.Trim();
        }
        catch
        {
            return string.Empty; // a path the platform refuses is simply nameless
        }
    }

    private static string Extension(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try { return Path.GetExtension(path.Trim()); }
        catch { return string.Empty; }
    }

    private static bool IsAllAsciiDigits(this ReadOnlySpan<char> span)
    {
        if (span.Length == 0) return false;
        foreach (var c in span)
            if (!char.IsAsciiDigit(c)) return false;
        return true;
    }
}
