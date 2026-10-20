using System.Reflection.PortableExecutable;
using DotCharter.Services.Update;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Update;

/// <summary>
///     The pack-time binary check (<c>tools/release/CheckPayloadBinaries.cs</c>,
///     run by <c>New-Release.ps1</c> on both staged payloads). v0.59.0 (#137): an
///     interrupted build left <c>DotCharter.dll</c> and 259 other outputs the right
///     size and entirely zero, the next build reused them, and the zip validated —
///     the packed app died at launch with <c>BadImageFormatException</c>. Each fault
///     is pinned from both sides: the broken file is refused, the healthy one beside
///     it is not.
/// </summary>
public sealed class ReleasePayloadIntegrityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DotCharterTests", "payload-" + Guid.NewGuid().ToString("N"));

    public ReleasePayloadIntegrityTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => BestEffortDelete.Directory(_dir);

    /// <summary>A real managed assembly: the one this check lives in.</summary>
    private static string ManagedAssembly => typeof(ReleasePayloadIntegrity).Assembly.Location;

    private string Write(string relativePath, byte[] bytes)
    {
        var path = Path.Combine(_dir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private string Copy(string source, string relativePath) => Write(relativePath, File.ReadAllBytes(source));

    private PayloadFileProblem SingleProblem()
    {
        var report = ReleasePayloadIntegrity.Scan(_dir);
        return Assert.Single(report.Problems);
    }

    // ---- the incident ------------------------------------------------------

    /// <summary>
    ///     The v0.59.0 file exactly: an assembly of plausible size, every byte zero.
    ///     Larger than one scan buffer, so the zero scan has to read past its first
    ///     chunk to be sure.
    /// </summary>
    [Fact]
    public void A_zero_filled_assembly_is_refused_by_name()
    {
        Write("DotCharter.dll", new byte[200_000]);

        var problem = SingleProblem();

        Assert.Equal("DotCharter.dll", problem.RelativePath);
        Assert.Equal(PayloadFileFault.AllZeros, problem.Fault);
    }

    /// <summary>The interrupted build zeroed data files too — a deps.json among them.</summary>
    [Fact]
    public void A_zero_filled_data_file_is_refused()
    {
        Write("DotCharter.deps.json", new byte[4096]);

        Assert.Equal(PayloadFileFault.AllZeros, SingleProblem().Fault);
    }

    /// <summary>
    ///     …but a file whose LAST byte is the only non-zero one is real data, not an
    ///     unwritten allocation: the scan must not stop judging at its first buffer.
    /// </summary>
    [Fact]
    public void A_data_file_that_is_zero_except_its_last_byte_is_kept()
    {
        var bytes = new byte[200_000];
        bytes[^1] = 1;
        Write("sparse.bin", bytes);

        Assert.True(ReleasePayloadIntegrity.Scan(_dir).IsClean);
    }

    // ---- what a PE image must be --------------------------------------------

    [Fact]
    public void An_empty_image_is_refused_and_an_empty_data_file_is_kept()
    {
        Write("hostfxr.dll", []);
        Write("marker.txt", []);

        var problem = SingleProblem();

        Assert.Equal("hostfxr.dll", problem.RelativePath);
        Assert.Equal(PayloadFileFault.Empty, problem.Fault);
    }

    /// <summary>The shape <c>-SelfTest</c>'s zip-layout payload uses: text where a binary belongs.</summary>
    [Fact]
    public void A_text_stub_named_exe_is_not_a_portable_executable()
    {
        Write("DotCharter.exe", "stub\n"u8.ToArray());

        Assert.Equal(PayloadFileFault.NotPortableExecutable, SingleProblem().Fault);
    }

    /// <summary>A DOS header and nothing behind it: <c>MZ</c> alone proves nothing.</summary>
    [Fact]
    public void An_mz_header_over_zeros_has_no_pe_headers()
    {
        var bytes = new byte[8192];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        Write("coreclr.dll", bytes);

        Assert.Equal(PayloadFileFault.BadPeHeaders, SingleProblem().Fault);
    }

    /// <summary>
    ///     A managed assembly whose PE headers survived but whose metadata did not —
    ///     the headers alone would pass it, and the CLR would not.
    /// </summary>
    [Fact]
    public void A_managed_assembly_with_zeroed_metadata_is_refused()
    {
        var bytes = File.ReadAllBytes(ManagedAssembly);
        int metadataOffset;
        using (var pe = new PEReader(new MemoryStream(bytes)))
        {
            var cor = pe.PEHeaders.CorHeader!;
            Assert.True(pe.PEHeaders.TryGetDirectoryOffset(cor.MetadataDirectory, out metadataOffset));
        }

        Array.Clear(bytes, metadataOffset, 256);
        Write("DotCharter.Services.dll", bytes);

        Assert.Equal(PayloadFileFault.BadMetadata, SingleProblem().Fault);
    }

    /// <summary>A build cut off mid-write: the first half of a real assembly.</summary>
    [Fact]
    public void A_truncated_managed_assembly_is_refused()
    {
        var bytes = File.ReadAllBytes(ManagedAssembly);
        Write("DotCharter.Services.dll", bytes[..(bytes.Length / 2)]);

        Assert.NotEqual(PayloadFileFault.None, SingleProblem().Fault);
    }

    // ---- what must pass ------------------------------------------------------

    [Fact]
    public void A_real_managed_assembly_passes()
    {
        Copy(ManagedAssembly, "DotCharter.Services.dll");

        var report = ReleasePayloadIntegrity.Scan(_dir);

        Assert.True(report.IsClean, string.Join("; ", report.Problems));
        Assert.Equal(1, report.ImagesChecked);
    }

    /// <summary>A native image (this process's own apphost): no CLI header, still a valid PE.</summary>
    [Fact]
    public void A_real_native_executable_passes()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the process image is a PE only on Windows.");
        Copy(Environment.ProcessPath!, "tools/ffmpeg.exe");

        Assert.True(ReleasePayloadIntegrity.Scan(_dir).IsClean);
    }

    /// <summary>
    ///     The report names files the way the release log prints them — relative to
    ///     the payload root, forward slashes, nested folders included — and counts
    ///     everything it looked at.
    /// </summary>
    [Fact]
    public void Problems_in_subfolders_are_named_relative_to_the_payload_root()
    {
        Copy(ManagedAssembly, "DotCharter.Services.dll");
        Write("tools/yt-dlp.exe", new byte[1024]);
        Write("voices/VOICE-CREDITS.txt", "credits"u8.ToArray());

        var report = ReleasePayloadIntegrity.Scan(_dir);

        Assert.Equal(3, report.FilesChecked);
        Assert.Equal(2, report.ImagesChecked);
        var problem = Assert.Single(report.Problems);
        Assert.Equal("tools/yt-dlp.exe", problem.RelativePath);
    }

    [Theory]
    [InlineData("a.dll", true)]
    [InlineData("A.EXE", true)]
    [InlineData("a.deps.json", false)]
    [InlineData("a.pdb", false)]
    [InlineData("guitar.dcvp", false)]
    public void Only_dll_and_exe_are_held_to_the_pe_rules(string name, bool image) =>
        Assert.Equal(image, ReleasePayloadIntegrity.IsExecutableImage(name));
}
