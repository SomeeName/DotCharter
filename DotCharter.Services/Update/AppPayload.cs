using System.IO.Compression;
using System.Text.Json;

namespace DotCharter.Services.Update;

/// <summary>
///     How an install expects to find the .NET runtime. Determined by
///     <c>DotCharter.runtimeconfig.json</c> — the same file the host itself
///     reads, so this classification cannot disagree with what actually
///     happens at launch.
/// </summary>
public enum InstallLayout
{
    /// <summary>Not a DotCharter install, or its runtimeconfig is missing / unreadable.</summary>
    Unknown = 0,

    /// <summary>Carries its own runtime next to the exe (<c>includedFrameworks</c>).
    /// What a <c>--self-contained</c> publish — i.e. every shipped release — produces.</summary>
    SelfContained,

    /// <summary>Resolves a machine-wide shared framework (<c>framework</c> /
    /// <c>frameworks</c>). What a plain <c>dotnet build</c> produces.</summary>
    FrameworkDependent
}

/// <summary>
///     Identifies and validates a DotCharter application payload — an install
///     directory or an update archive.
///
///     Every update is applied by copying a payload OVER an existing install
///     without purging (robocopy /E), so the two must have the same
///     <see cref="InstallLayout" />. Merging a framework-dependent payload onto
///     a self-contained install overwrites the exe + runtimeconfig + deps while
///     leaving the old <c>hostfxr.dll</c> behind; the apphost then loads that
///     stale hostfxr, which probes the *app directory* for a shared framework
///     and fails with "You must install .NET" — a break no system-wide runtime
///     install can repair (multi-level lookup was removed in .NET 7). Guarding
///     the layout is what prevents that.
///
///     All members are pure with respect to the filesystem inputs they are
///     handed, so the rules are unit-testable without a real install.
/// </summary>
public static class AppPayload
{
    public const string ExeName = "DotCharter.exe";
    public const string RuntimeConfigName = "DotCharter.runtimeconfig.json";
    public const string DepsName = "DotCharter.deps.json";

    /// <summary>The host shim an apphost prefers over the machine-wide one when
    /// it sits next to the exe. Present only in a self-contained layout.</summary>
    public const string HostFxrName = "hostfxr.dll";

    public const string CoreClrName = "coreclr.dll";

    /// <summary>
    /// The folder a SPLIT payload keeps the app in: the top of the
    ///     install holds only the launcher (also <see cref="ExeName" />),
    ///     <c>voices/</c>, <c>tools/</c> and a readme, and everything
    ///     <see cref="Validate" /> asks about lives in here. Same name as
    ///     <c>InstallLocation.AppFolderName</c>, which resolves it at run time.
    /// </summary>
    public const string AppFolderName = InstallLocation.AppFolderName;

    /// <summary>The payload-relative prefix of the app's files in a split payload.</summary>
    public const string SplitPrefix = AppFolderName + "/";

    /// <summary>
    ///     <c>"app/"</c> when the file set is a split payload (the app's exe is in
    ///     <c>app/</c>), else <c>""</c>. Every requirement <see cref="Validate" />
    ///     checks is read under this prefix.
    /// </summary>
    public static string AppPrefix(Func<string, bool> hasFile)
    {
        ArgumentNullException.ThrowIfNull(hasFile);
        return hasFile(SplitPrefix + ExeName) ? SplitPrefix : string.Empty;
    }

    /// <summary>Whether a payload or install directory is split.</summary>
    public static bool IsSplitDirectory(string directory)
        => File.Exists(Path.Combine(directory, AppFolderName, ExeName));

    /// <summary>The folder of a payload or install that holds the app's binaries.</summary>
    public static string ResolveAppDirectory(string directory)
        => IsSplitDirectory(directory) ? Path.Combine(directory, AppFolderName) : directory;

    /// <summary>
    ///     Refuses to copy a FLAT payload over a SPLIT install, or null when the
    ///     shapes may meet. A flat payload's exe would overwrite the launcher and
    ///     its runtime would land beside it, while the app the launcher starts
    ///     stayed the old one in <c>app/</c> — an install that reports the new
    ///     version and runs the old. A split payload over a flat install is the
    /// one-step migration every pre-0.63 install takes, and is allowed.
    /// </summary>
    public static string? CheckShape(bool installIsSplit, bool payloadIsSplit)
        => installIsSplit && !payloadIsSplit
            ? $"it is a flat payload and this install keeps the app in its {AppFolderName} folder — " +
              "copying it over would leave the old app running behind a new launcher"
            : null;

    /// <summary><see cref="CheckShape(bool, bool)" /> for an install and a staged payload on disk.</summary>
    public static string? CheckShape(string installDirectory, string payloadDirectory)
        => CheckShape(IsSplitDirectory(installDirectory), IsSplitDirectory(payloadDirectory));

    /// <summary>
    ///     Best-effort display version of a staged payload, read off the APP's exe —
    /// never the launcher's on a split payload. Only ever used for a
    ///     label, so every failure is an empty string.
    /// </summary>
    public static string TryReadVersion(string payloadDirectory)
    {
        try
        {
            var exe = Path.Combine(ResolveAppDirectory(payloadDirectory), ExeName);
            if (!File.Exists(exe)) return string.Empty;
            return System.Diagnostics.FileVersionInfo.GetVersionInfo(exe).ProductVersion?.Trim() ?? string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    ///     Members an update archive may declare. A self-contained release is
    ///     under a thousand files and the whole runtime is already in that count,
    ///     so this is ~20× a real payload — it exists to stop the cheap attack,
    ///     not to police the build.
    ///     <para>
    ///         The attack it stops is the one <see cref="BoundedZipExtractor" />'s
    ///         own doc-comment describes: merely reaching
    ///         <see cref="ZipArchive.Entries" /> materialises one
    ///         <see cref="ZipArchiveEntry" /> per member, so a 200 MB archive of
    ///         empty members costs millions of live objects before any per-entry
    ///         check can run — and <see cref="ValidateArchive" /> does exactly
    ///         that, on a zip a LAN peer sent us.
    ///     </para>
    ///     <para>
    ///         A zip64 archive stores <c>0xFFFF</c> in the record this is read
    ///         from, so it fails this bound and is refused. That is the wanted
    ///         answer: nothing this app publishes has 65,535 members or a 4 GB
    ///         central directory.
    ///     </para>
    /// </summary>
    public const int MaxArchiveEntries = 16_384;

    /// <summary>Bytes an update payload may inflate to, given the size of the
    /// archive it came from. A ~414 MB package expands to well under 8×, so a
    /// real release is never near it; a hand-crafted bomb is stopped at the
    /// 512 MiB floor instead of running until the disk fills. Same idiom as
    /// the package reader's archive.</summary>
    public static long InflationCeiling(long archiveLengthBytes)
        => Math.Max(512L * 1024 * 1024, Math.Max(0, archiveLengthBytes) * 8);

    /// <summary>Largest <c>runtimeconfig.json</c> we will read out of an
    /// untrusted archive. It is JSON describing one framework reference; a member
    /// under this name that inflates to gigabytes is not one.</summary>
    private const int MaxRuntimeConfigBytes = 1024 * 1024;

    /// <summary>
    ///     Bounds an archive's declared member count straight from the
    ///     end-of-central-directory record, WITHOUT opening it — so the bound
    ///     runs before anything materialises entries. Null when the archive may
    ///     be opened; otherwise a message describing the refusal, in the same
    ///     voice as <see cref="Validate" />'s.
    /// </summary>
    public static string? CheckArchiveBounds(string archivePath, int maxEntries = MaxArchiveEntries)
    {
        try
        {
            using var stream = File.OpenRead(archivePath);
            var declared = BoundedZipExtractor.TryReadDeclaredEntryCount(stream);
            if (declared == null) return "it is not a readable zip archive";
            if (declared == 0) return "it contains no entries";
            return declared > maxEntries
                ? $"it declares {declared} members, past the {maxEntries} an app payload may have"
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "it could not be read";
        }
    }

    /// <summary>Human-readable name for messages ("self-contained" / "framework-dependent").</summary>
    public static string Describe(InstallLayout layout) => layout switch
    {
        InstallLayout.SelfContained => "self-contained",
        InstallLayout.FrameworkDependent => "framework-dependent",
        _ => "unrecognised"
    };

    /// <summary>Classifies the contents of a <c>runtimeconfig.json</c>.</summary>
    public static InstallLayout ClassifyRuntimeConfig(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return InstallLayout.Unknown;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("runtimeOptions", out var options) ||
                options.ValueKind != JsonValueKind.Object)
            {
                return InstallLayout.Unknown;
            }

            // A self-contained publish lists the runtime it carries;
            // a framework-dependent one names the shared framework it needs.
            if (options.TryGetProperty("includedFrameworks", out var included) &&
                included.ValueKind == JsonValueKind.Array && included.GetArrayLength() > 0)
            {
                return InstallLayout.SelfContained;
            }

            if (options.TryGetProperty("framework", out var framework) &&
                framework.ValueKind == JsonValueKind.Object)
            {
                return InstallLayout.FrameworkDependent;
            }

            if (options.TryGetProperty("frameworks", out var frameworks) &&
                frameworks.ValueKind == JsonValueKind.Array && frameworks.GetArrayLength() > 0)
            {
                return InstallLayout.FrameworkDependent;
            }

            return InstallLayout.Unknown;
        }
        catch (JsonException)
        {
            return InstallLayout.Unknown;
        }
    }

    /// <summary>Classifies an install / payload directory.</summary>
    public static InstallLayout ClassifyDirectory(string directory)
        => ClassifyRuntimeConfig(TryReadAllText(Path.Combine(ResolveAppDirectory(directory), RuntimeConfigName)));

    /// <summary>
    ///     The core rule set: returns <c>null</c> when the file set is a coherent
    ///     DotCharter payload whose layout matches <paramref name="expected" />,
    ///     otherwise a message describing the problem.
    /// </summary>
    /// <param name="hasFile">Existence probe for a payload-root-relative file name.</param>
    /// <param name="runtimeConfigJson">Contents of the payload's runtimeconfig, if any —
    /// the one under <see cref="AppPrefix" /> for a split payload.</param>
    /// <param name="expected">Layout the payload must match.
    /// <see cref="InstallLayout.Unknown" /> skips the match check.</param>
    public static string? Validate(Func<string, bool> hasFile, string? runtimeConfigJson, InstallLayout expected)
    {
        // A split payload carries the launcher at its top and the app in
        // app/: both exes are required, and every runtime file is the app's.
        var at = AppPrefix(hasFile);
        if (!hasFile(ExeName)) return $"it has no {ExeName}";
        if (!hasFile(at + RuntimeConfigName)) return $"it has no {at}{RuntimeConfigName}";
        if (!hasFile(at + DepsName)) return $"it has no {at}{DepsName}";

        var actual = ClassifyRuntimeConfig(runtimeConfigJson);
        if (actual == InstallLayout.Unknown)
            return $"{RuntimeConfigName} does not describe a known runtime layout";

        if (expected != InstallLayout.Unknown && actual != expected)
        {
            return $"it is a {Describe(actual)} build but this install is {Describe(expected)} — " +
                   "merging them would leave an unbootable mix of runtime files";
        }

        // A self-contained payload without its host cannot boot, and copying it
        // over a self-contained install would strand the old host next to a new
        // runtimeconfig. Either way, refuse.
        if (actual == InstallLayout.SelfContained && (!hasFile(at + HostFxrName) || !hasFile(at + CoreClrName)))
            return $"it claims to be self-contained but is missing {at}{HostFxrName} / {at}{CoreClrName}";

        return null;
    }

    /// <summary>Validates an extracted payload directory. Null when it is safe to apply.</summary>
    public static string? ValidateDirectory(string directory, InstallLayout expected)
        => Validate(
            name => File.Exists(Path.Combine(directory, name)),
            TryReadAllText(Path.Combine(ResolveAppDirectory(directory), RuntimeConfigName)),
            expected);

    /// <summary>Validates an update archive before anything is written to disk.
    /// Null when it is safe to extract and apply.</summary>
    public static string? ValidateArchive(ZipArchive archive, InstallLayout expected)
    {
        var root = ResolveArchiveRoot(archive);
        var names = archive.Entries
            .Select(entry => entry.FullName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        bool HasFile(string name) => names.Contains(root + name);
        return Validate(
            HasFile,
            TryReadEntry(archive, root + AppPrefix(HasFile) + RuntimeConfigName),
            expected);
    }

    /// <summary>
    ///     Where the payload sits inside an archive: <c>""</c> when entries are at
    ///     the root (LAN packages), or <c>"folder/"</c> when the release zip wraps
    ///     everything in a single top-level directory.
    /// </summary>
    public static string ResolveArchiveRoot(ZipArchive archive)
    {
        if (FindEntry(archive, ExeName) != null) return string.Empty;

        foreach (var entry in archive.Entries)
        {
            if (!entry.FullName.EndsWith("/" + ExeName, StringComparison.OrdinalIgnoreCase)) continue;

            var prefix = entry.FullName[..^ExeName.Length];
            // Only unwrap a *single* top-level folder — a nested match is not the payload root.
            if (!prefix.TrimEnd('/').Contains('/')) return prefix;
        }

        return string.Empty;
    }

    private static ZipArchiveEntry? FindEntry(ZipArchive archive, string entryName)
        => archive.GetEntry(entryName)
           ?? archive.Entries.FirstOrDefault(
               entry => string.Equals(entry.FullName, entryName, StringComparison.OrdinalIgnoreCase));

    private static string? TryReadEntry(ZipArchive archive, string entryName)
    {
        var entry = FindEntry(archive, entryName);
        if (entry == null) return null;

        try
        {
            // The archive is untrusted (a LAN peer's package), so this member is
            // read under a ceiling rather than whole: a "runtimeconfig.json" that
            // inflates to gigabytes must not be materialised just to classify it.
            // A truncated read simply fails to parse, which is the right answer.
            using var stream = entry.Open();
            using var bounded = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while (bounded.Length < MaxRuntimeConfigBytes &&
                   (read = stream.Read(buffer, 0,
                       (int)Math.Min(buffer.Length, MaxRuntimeConfigBytes - bounded.Length))) > 0)
            {
                bounded.Write(buffer, 0, read);
            }

            bounded.Position = 0;
            using var reader = new StreamReader(bounded);
            return reader.ReadToEnd();
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static string? TryReadAllText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
