using System.Globalization;

namespace DotCharter.Services.Package.DcSong;

/// <summary>
///     The constants of the <c>.dcsong</c> package format. The prose that explains
///     them, and the single source of truth for the format, is
///     <c>docs/agents/services/dcsong-format.md</c>; a value changed here without
///     changing that file is a format change nobody can see.
/// </summary>
public static class DcSongFormat
{
    /// <summary>The file extension, with its dot.</summary>
    public const string Extension = ".dcsong";

    /// <summary>The <c>format</c> field every manifest carries.</summary>
    public const string FormatId = "dcsong";

    /// <summary>The major version this build writes and the newest it reads. A
    /// package with a NEWER major is refused; a newer minor is read.</summary>
    public const int CurrentMajor = 1;

    /// <summary>The minor version this build writes.</summary>
    public const int CurrentMinor = 1;

    public const string ManifestPath = "manifest.json";
    /// <summary>The chart member this build writes: the project file, under the project extension.</summary>
    public const string ChartPath = "chart.dcproj";

    /// <summary>The chart member format 1.0 packages carry; read, never written.</summary>
    public const string LegacyChartPath = "chart.rscp";
    public const string AudioStem = "audio/song";
    public const string PreviewPath = "audio/preview.opus";
    public const string CoverStem = "images/cover";
    public const string BannerStem = "images/banner";
    public const string StemFolder = "stems/";

    /// <summary>Song, preview and stems are Opus at this rate, in kb/s, stereo.</summary>
    public const int OpusBitrateKbps = 192;

    /// <summary>Most members a package may declare. A full package is the
    /// manifest, the chart, two audio files, two images and at most
    /// <see cref="MaxStems" /> stems.</summary>
    public const int MaxEntries = 64;

    public const int MaxStems = 16;

    /// <summary>Largest manifest accepted, in bytes.</summary>
    public const long MaxManifestBytes = 1L * 1024 * 1024;

    /// <summary>Largest inflated chart member accepted. A real project is a
    /// few MB; this is the bomb ceiling.</summary>
    public const long MaxChartBytes = 64L * 1024 * 1024;

    /// <summary>Largest cover or banner accepted, in bytes.</summary>
    public const long MaxImageBytes = 16L * 1024 * 1024;

    /// <summary>The current version as the manifest spells it.</summary>
    public static string CurrentVersionText { get; } =
        string.Create(CultureInfo.InvariantCulture, $"{CurrentMajor}.{CurrentMinor}");

    /// <summary>Parses <c>major.minor</c>: two non-negative integers, nothing else.</summary>
    public static bool TryParseVersion(string? text, out int major, out int minor)
    {
        major = minor = 0;
        if (string.IsNullOrEmpty(text)) return false;

        var parts = text.Split('.');
        return parts.Length == 2
               && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out major)
               && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out minor);
    }

    /// <summary>A stem name becomes a member name, so it is held to
    /// <c>[a-z0-9_-]{1,32}</c>.</summary>
    public static bool IsValidStemName(string? name) =>
        !string.IsNullOrEmpty(name)
        && name.Length <= 32
        && name.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-');
}

/// <summary>What a member of the package IS. The manifest spells it in lower case.</summary>
public enum DcSongFileRole
{
    Chart,
    Audio,
    Preview,
    Cover,
    Banner,
    Stem,

    /// <summary>A role a NEWER minor version added. Listed and checksummed like
    /// every member, never read by this build.</summary>
    Other
}

/// <summary>How the song audio is stored.</summary>
public enum DcSongAudioCodec
{
    /// <summary>Ogg Opus, 192 kb/s stereo. Playback only.</summary>
    Opus,

    /// <summary>FLAC. Lossless: what chart-from-audio and stem separation need.</summary>
    Flac
}
