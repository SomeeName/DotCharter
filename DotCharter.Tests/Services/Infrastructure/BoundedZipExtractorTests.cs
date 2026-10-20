using System.Buffers.Binary;
using System.IO.Compression;
using DotCharter.Services.Infrastructure.Storage;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Infrastructure;

/// <summary>
///     The zip-bomb guard both untrusted-archive call sites share (a LAN
///     project transfer, a roamed profile bundle). The bug being pinned: the
///     previous guard summed <c>ZipArchiveEntry.Length</c>, which is the
///     uncompressed size the SENDER declared in the central directory and which
///     <c>ZipFile.ExtractToDirectory</c> never checks against the bytes it
///     actually writes. <see cref="LyingZip" /> is exactly that archive — small
///     declarations over deflate streams that expand far past them — and the
///     declared-sum guard waves it through.
/// </summary>
public sealed class BoundedZipExtractorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "DotCharter.Tests", "zipguard-" + Guid.NewGuid().ToString("N"));

    public BoundedZipExtractorTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => BestEffortDelete.Directory(_dir);

    // ---- archive builders ---------------------------------------------------

    private string WriteZip(string name, Action<ZipArchive> fill)
    {
        var path = Path.Combine(_dir, name);
        using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
            fill(archive);
        return path;
    }

    private static void AddCompressibleEntry(ZipArchive archive, string name, int bytes)
    {
        using var stream = archive.CreateEntry(name, CompressionLevel.Optimal).Open();
        var chunk = new byte[64 * 1024]; // zeros — deflate collapses them
        for (var written = 0; written < bytes; written += chunk.Length)
            stream.Write(chunk, 0, Math.Min(chunk.Length, bytes - written));
    }

    /// <summary>
    ///     Rewrites every uncompressed-size field (local header AND central
    ///     directory) to <paramref name="declaredBytes" />, leaving the deflate
    ///     streams untouched. The result is a well-formed archive whose members
    ///     LIE about how much they expand to — the exact shape the old guard
    ///     trusted. Walks the records properly instead of scanning for
    ///     signatures, so compressed data can never be mistaken for a header.
    /// </summary>
    private static void LyingZip(string path, uint declaredBytes)
    {
        var bytes = File.ReadAllBytes(path);

        // End-of-central-directory (no comment): entry count at +10, central
        // directory offset at +16.
        var eocd = bytes.Length - 22;
        Assert.Equal(0x06054b50u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(eocd)));
        var count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(eocd + 10));
        var offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(eocd + 16));

        for (var i = 0; i < count; i++)
        {
            Assert.Equal(0x02014b50u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset)));
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 28));
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 30));
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 32));
            var localOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 42));

            // Central directory: uncompressed size at +24.
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 24), declaredBytes);

            // Local file header: uncompressed size at +22.
            Assert.Equal(0x04034b50u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(localOffset)));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(localOffset + 22), declaredBytes);

            offset += 46 + nameLength + extraLength + commentLength;
        }

        File.WriteAllBytes(path, bytes);
    }

    private static long DirectorySize(string dir)
        => !Directory.Exists(dir)
            ? 0
            : Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);

    // ---- the counting guard --------------------------------------------------

    [Fact]
    public void RealExpansionPastTheBudgetIsAbortedMidMember()
    {
        // 8 MB of real, honestly-declared expansion against a 64 KB budget.
        // The point is WHERE it stops: the running total is checked per read,
        // so the abort happens inside the member instead of after the whole
        // thing has landed on disk.
        var path = WriteZip("bomb.zip", a => AddCompressibleEntry(a, "payload.bin", 8 * 1024 * 1024));

        var target = Path.Combine(_dir, "out");
        var result = BoundedZipExtractor.ExtractBounded(path, target, maxTotalBytes: 64 * 1024);

        Assert.Equal(ZipExtractionRefusal.BudgetExceeded, result.Refusal);
        Assert.True(DirectorySize(target) <= 64 * 1024 + 81920,
            $"wrote {DirectorySize(target)} bytes past a 64 KB budget");
    }

    /// <summary>
    ///     Documents a MEASURED runtime guarantee the audit assumed was absent:
    ///     .NET hands <c>DeflateStream</c> the member's declared uncompressed
    ///     size, so the inflater simply stops there. An under-declared member
    ///     therefore extracts TRUNCATED — it cannot smuggle extra bytes past a
    ///     declared-size guard. That is why the fix here is not "the old guard
    ///     was bypassable" but "the old guard measured the wrong thing":
    ///     <c>Entries.Sum(e =&gt; e.Length)</c> is a CHECKED <c>long</c> sum over
    ///     attacker-written fields, and a zip64 archive can overflow it into an
    ///     <c>OverflowException</c> that no call site catches.
    /// </summary>
    [Fact]
    public void AnUnderDeclaredMemberIsTruncatedByTheRuntime()
    {
        var path = WriteZip("lying.zip", a => AddCompressibleEntry(a, "payload.bin", 8 * 1024 * 1024));
        LyingZip(path, declaredBytes: 1024);

        using (var probe = ZipFile.OpenRead(path))
            Assert.Equal(1024, probe.Entries.Sum(e => e.Length));

        var target = Path.Combine(_dir, "lying");
        var result = BoundedZipExtractor.ExtractBounded(path, target, maxTotalBytes: 64 * 1024);

        Assert.True(result.Succeeded);
        Assert.Equal(1024, result.BytesWritten);
        Assert.Equal(1024, DirectorySize(target));
    }

    [Fact]
    public void AnHonestArchiveInsideTheBudgetExtractsNormally()
    {
        var path = WriteZip("ok.zip", a =>
        {
            AddCompressibleEntry(a, "one.bin", 10_000);
            AddCompressibleEntry(a, "sub/two.bin", 20_000);
        });

        var target = Path.Combine(_dir, "ok");
        var result = BoundedZipExtractor.ExtractBounded(path, target, maxTotalBytes: 1024 * 1024);

        Assert.True(result.Succeeded);
        Assert.Equal(30_000, result.BytesWritten);
        Assert.Equal(2, result.FilesWritten);
        Assert.Equal(10_000, new FileInfo(Path.Combine(target, "one.bin")).Length);
        Assert.Equal(20_000, new FileInfo(Path.Combine(target, "sub", "two.bin")).Length);
    }

    [Fact]
    public void TheBudgetIsInclusive()
    {
        var path = WriteZip("exact.zip", a => AddCompressibleEntry(a, "one.bin", 4096));
        var result = BoundedZipExtractor.ExtractBounded(path, Path.Combine(_dir, "exact"), maxTotalBytes: 4096);
        Assert.True(result.Succeeded);
    }

    // ---- the traversal + count guards ----------------------------------------

    [Fact]
    public void AMemberOutsideTheDestinationIsRefused()
    {
        var path = WriteZip("slip.zip", a =>
        {
            // CreateEntry keeps the name verbatim, so this is a real zip-slip.
            using var stream = a.CreateEntry("../escaped.txt").Open();
            stream.Write("nope"u8);
        });

        var target = Path.Combine(_dir, "slip");
        var result = BoundedZipExtractor.ExtractBounded(path, target, maxTotalBytes: 1024 * 1024);

        Assert.Equal(ZipExtractionRefusal.UnsafePath, result.Refusal);
        Assert.False(File.Exists(Path.Combine(_dir, "escaped.txt")));
    }

    /// <summary>
    ///     `..` is only the FAMILIAR zip-slip. A member name may also be ROOTED,
    ///     and <see cref="Path.Combine(string,string)" /> silently DISCARDS the
    ///     first argument when the second is rooted — so a rooted member is the
    ///     shape that reaches straight past a destination directory without a
    ///     single `..` in it. Drive-qualified, drive-relative and UNC names are
    ///     all rooted in different ways on Windows and each has to land on the
    ///     same refusal.
    ///     <para>
    ///         The extractor gets this right today; nothing asserted it. That is
    ///         the whole reason to write it down — the check is one
    ///         <c>StartsWith</c> and it is the only thing between an
    ///         unauthenticated LAN peer's archive and an arbitrary file write.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     The DRIVE-QUALIFIED case names an absolute path inside this test's own
    ///     temp directory rather than somewhere like <c>C:\Windows\Temp</c>.
    ///     That is deliberate and was learned the hard way while proving this
    ///     test can fail: with the guard removed the member really is written to
    ///     wherever it points, so a machine-global target leaves a file behind
    ///     that makes the assertion fail for every LATER run on that box.
    /// </remarks>
    [Theory]
    [InlineData("<abs>")]                          // drive-qualified absolute
    [InlineData("/dc-escaped.txt")]                // drive-relative root
    [InlineData(@"\dc-escaped.txt")]               // same, Windows separator
    [InlineData(@"\\server\share\dc-escaped.txt")] // UNC
    [InlineData("../../dc-escaped.txt")]           // classic, two levels
    [InlineData("sub/../../dc-escaped.txt")]       // traversal after a real segment
    public void ARootedOrTraversingMemberIsRefused(string entryName)
    {
        var absoluteTarget = Path.Combine(_dir, "dc-escaped-abs.txt");
        if (entryName == "<abs>") entryName = absoluteTarget;

        var path = WriteZip("rooted-" + Guid.NewGuid().ToString("N")[..8] + ".zip", a =>
        {
            // CreateEntry keeps the name verbatim, so these are real archives —
            // not something only a hand-built central directory could express.
            using var stream = a.CreateEntry(entryName).Open();
            stream.Write("nope"u8);
        });

        var target = Path.Combine(_dir, "rooted-out");
        var result = BoundedZipExtractor.ExtractBounded(path, target, maxTotalBytes: 1024 * 1024);

        Assert.Equal(ZipExtractionRefusal.UnsafePath, result.Refusal);
        Assert.Equal(0, result.FilesWritten);
        // And nothing landed anywhere the name pointed at.
        Assert.False(File.Exists(Path.Combine(_dir, "dc-escaped.txt")));
        Assert.False(File.Exists(absoluteTarget));
    }

    /// <summary>
    ///     A member name that only LOOKS like the destination's sibling must not
    ///     slip past a naive prefix test (`…/out` vs `…/outsider`). The prefix is
    ///     built with a trailing separator for exactly this.
    /// </summary>
    [Fact]
    public void ASiblingDirectoryWithTheSameNamePrefixIsRefused()
    {
        var path = WriteZip("sibling.zip", a =>
        {
            using var stream = a.CreateEntry("../outsider/x.txt").Open();
            stream.Write("nope"u8);
        });

        var result = BoundedZipExtractor.ExtractBounded(
            path, Path.Combine(_dir, "out"), maxTotalBytes: 1024 * 1024);

        Assert.Equal(ZipExtractionRefusal.UnsafePath, result.Refusal);
        Assert.False(Directory.Exists(Path.Combine(_dir, "outsider")));
    }

    /// <summary>
    ///     The rule is "an untrusted zip is expanded ONLY through
    ///     <c>BoundedZipExtractor</c>", and it has been broken twice by code that
    ///     read perfectly reasonably at the call site (the update apply path, the
    ///     LAN package validation). A source lint is the only thing that catches
    ///     the THIRD one, because nothing about
    ///     <c>ZipFile.ExtractToDirectory</c> looks wrong locally.
    ///     <para>
    ///         <c>entry.ExtractToFile</c> carries an allowlist rather than a ban:
    ///         the one current use extracts from an archive whose provenance is
    ///         already established (this app's own stem cache) and flattens the
    ///         member name to a basename, so it cannot traverse. A NEW one has to
    ///         be argued for here.
    ///     </para>
    /// </summary>
    [Fact]
    public void NoServiceCodeExpandsAnArchiveOutsideTheBoundedExtractor()
    {
        var services = FindServicesRoot();

        var allowedExtractToFile = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {

            // Our own .dcstems cache, written by this app; uses entry.Name, which
            // is the basename by definition.
            Path.Combine("Audio", "StemSeparation", "Cache", "StemArchive.cs")
        };

        var toDirectory = new List<string>();
        var toFile = new List<string>();

        // DotCharter.Tone (the rig, NAM capture library, TONE3000) is part of the
        // same service layer; a relative path there starts with "..".
        var tone = Path.Combine(Path.GetDirectoryName(services)!, "DotCharter.Tone");
        foreach (var file in new[] { services, tone }
                     .SelectMany(r => Directory.EnumerateFiles(r, "*.cs", SearchOption.AllDirectories)))
        {
            var relative = Path.GetRelativePath(services, file);
            if (relative.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                relative.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var code = StripComments(File.ReadAllText(file));
            if (code.Contains("ZipFile.ExtractToDirectory", StringComparison.Ordinal)) toDirectory.Add(relative);
            if (code.Contains(".ExtractToFile(", StringComparison.Ordinal) &&
                !allowedExtractToFile.Contains(relative))
            {
                toFile.Add(relative);
            }
        }

        Assert.True(toDirectory.Count == 0,
            "ZipFile.ExtractToDirectory has no member count and no decompressed-byte budget — route it " +
            "through BoundedZipExtractor.ExtractBounded: " + string.Join(", ", toDirectory));
        Assert.True(toFile.Count == 0,
            "entry.ExtractToFile writes a member straight to a path the SENDER named. Either route it " +
            "through BoundedZipExtractor or add it to this test's allowlist with the reason it cannot " +
            "traverse: " + string.Join(", ", toFile));
    }

    /// <summary>Drops `//` and `/* */` so a `see cref` or an example in a
    /// doc-comment cannot satisfy — or trip — the lint above.</summary>
    private static string StripComments(string code)
    {
        code = System.Text.RegularExpressions.Regex.Replace(code, @"/\*.*?\*/", " ",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        return string.Join('\n', code.Split('\n')
            .Select(l => l.TrimStart().StartsWith("//", StringComparison.Ordinal) ? string.Empty : l));
    }

    private static string FindServicesRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "DotCharter.sln")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        var services = Path.Combine(dir!.FullName, "DotCharter.Services");
        Assert.True(Directory.Exists(services), $"DotCharter.Services not found at {services}");
        return services;
    }

    [Fact]
    public void MoreMembersThanAllowedIsRefusedBeforeTheArchiveIsOpened()
    {
        var path = WriteZip("many.zip", a =>
        {
            for (var i = 0; i < 40; i++) AddCompressibleEntry(a, $"f{i}.bin", 8);
        });

        Assert.Equal(ZipExtractionRefusal.TooManyEntries,
            BoundedZipExtractor.CheckDeclaredEntryCount(path, maxEntries: 10));
        Assert.Equal(ZipExtractionRefusal.None,
            BoundedZipExtractor.CheckDeclaredEntryCount(path, maxEntries: 40));

        var result = BoundedZipExtractor.ExtractBounded(path, Path.Combine(_dir, "many"),
            maxTotalBytes: 1024 * 1024, maxEntries: 10);
        Assert.Equal(ZipExtractionRefusal.TooManyEntries, result.Refusal);
        Assert.Equal(0, result.FilesWritten); // nothing was even opened
    }

    [Fact]
    public void TheDeclaredCountComesFromTheEndOfCentralDirectory()
    {
        var path = WriteZip("count.zip", a =>
        {
            for (var i = 0; i < 7; i++) AddCompressibleEntry(a, $"f{i}.bin", 4);
        });

        using var stream = File.OpenRead(path);
        Assert.Equal(7, BoundedZipExtractor.TryReadDeclaredEntryCount(stream));
    }

    [Fact]
    public void SomethingThatIsNotAZipIsUnreadable()
    {
        var path = Path.Combine(_dir, "notazip.bin");
        File.WriteAllBytes(path, new byte[4096]);
        Assert.Equal(ZipExtractionRefusal.Unreadable, BoundedZipExtractor.CheckDeclaredEntryCount(path));
    }

    // ---- the subtree selector (the profile-bundle shape) ----------------------

    [Fact]
    public void OnlyTheSelectedSubtreeIsWritten()
    {
        var path = WriteZip("bundle.zip", a =>
        {
            AddCompressibleEntry(a, "profile.json", 16);
            AddCompressibleEntry(a, "data/stats.json", 32);
            AddCompressibleEntry(a, "data/nested/avatar.png", 64);
        });

        var target = Path.Combine(_dir, "bundle");
        var result = BoundedZipExtractor.ExtractBounded(path, target, maxTotalBytes: 1024 * 1024,
            selectRelativePath: e => e.FullName.StartsWith("data/", StringComparison.Ordinal)
                ? e.FullName["data/".Length..]
                : null);

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.FilesWritten);
        Assert.False(File.Exists(Path.Combine(target, "profile.json")));
        Assert.True(File.Exists(Path.Combine(target, "stats.json")));
        Assert.True(File.Exists(Path.Combine(target, "nested", "avatar.png")));
    }
}
