using System.Buffers.Binary;
using DotCharter.Services.Update;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Update;

/// <summary>
///     The import guard behind DotCharter #176. v1.0.0's <c>juce_sharp.dll</c> imported
///     <c>VCRUNTIME140.dll</c> and <c>MSVCP140.dll</c>, the zip carried neither, and on a Windows
///     without the Visual C++ redistributable the engine did not load. Every case builds a real
///     (minimal) PE image with a chosen import table, so the audit is exercised on bytes the
///     loader's own reader parses, not on a mock. The last case is the product: the native
///     runtime the build just produced or fetched must satisfy the same rule.
/// </summary>
public sealed class NativeImportAuditTests : IDisposable
{
    private const int HeadersBytes = 0x200;
    private const int SectionRva = 0x1000;
    private const int ImportDirectoryIndex = 1;
    private const int DelayImportDirectoryIndex = 13;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DotCharterTests", "imports-" + Guid.NewGuid().ToString("N"));

    public NativeImportAuditTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => BestEffortDelete.Directory(_dir);

    /// <summary>The incident's own import list.</summary>
    private static readonly string[] Incident =
        ["KERNEL32.dll", "VCRUNTIME140.dll", "VCRUNTIME140_1.dll", "MSVCP140.dll", "MSVCP140_ATOMIC_WAIT.dll", "api-ms-win-crt-runtime-l1-1-0.dll"];

    /// <summary>
    ///     A machine that carries the redistributable: its system directory HAS the runtime. The
    ///     audit must refuse the import anyway, because a clean machine does not.
    /// </summary>
    private static bool MachineWithRedistributable(string dll) =>
        dll.Equals("KERNEL32.dll", StringComparison.OrdinalIgnoreCase)
        || dll.StartsWith("VCRUNTIME140", StringComparison.OrdinalIgnoreCase)
        || dll.StartsWith("MSVCP140", StringComparison.OrdinalIgnoreCase);

    private string Image(string relativePath, string[] imports, string[]? delayImports = null)
    {
        var path = Path.Combine(_dir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, SyntheticPe.Build(imports, delayImports ?? []));
        return path;
    }

    private static List<string> Missing(NativeImportReport report) =>
        report.Problems.Select(problem => $"{problem.RelativePath}>{problem.ImportedDll}").Order(StringComparer.Ordinal).ToList();

    // ---- reading the table --------------------------------------------------

    [Fact]
    public void The_import_table_and_the_delay_import_table_are_both_read()
    {
        var path = Image("a.dll", ["KERNEL32.dll", "VCRUNTIME140.dll"], ["DirectML.dll"]);

        using var stream = File.OpenRead(path);

        Assert.Equal(["KERNEL32.dll", "VCRUNTIME140.dll", "DirectML.dll"], NativeImportAudit.ReadImports(stream));
    }

    [Fact]
    public void A_managed_assembly_has_no_native_imports_to_audit()
    {
        using var stream = File.OpenRead(typeof(NativeImportAudit).Assembly.Location);

        Assert.Empty(NativeImportAudit.ReadImports(stream));
    }

    // ---- the rule ------------------------------------------------------------

    /// <summary>The v1.0.0 shape: the runtime is on the importer's machine, not in the payload.</summary>
    [Fact]
    public void An_import_of_the_cpp_runtime_that_nothing_ships_is_refused_even_where_the_system_directory_has_it()
    {
        Image("app/juce_sharp.dll", Incident);

        var report = NativeImportAudit.Scan(_dir, MachineWithRedistributable);

        Assert.Equal(
            ["app/juce_sharp.dll>MSVCP140.dll", "app/juce_sharp.dll>MSVCP140_ATOMIC_WAIT.dll",
                "app/juce_sharp.dll>VCRUNTIME140.dll", "app/juce_sharp.dll>VCRUNTIME140_1.dll"],
            Missing(report));
    }

    [Fact]
    public void Windows_system_dlls_and_api_sets_are_accepted()
    {
        Image("juce_sharp.dll", ["KERNEL32.dll", "api-ms-win-crt-runtime-l1-1-0.dll", "ext-ms-win-ntuser-window-l1-1-0.dll", "ADVAPI32.dll"]);

        var report = NativeImportAudit.Scan(_dir, name => name is "KERNEL32.dll" or "ADVAPI32.dll");

        Assert.True(report.IsClean, string.Join("; ", Missing(report)));
        Assert.Equal(1, report.ImagesChecked);
    }

    [Fact]
    public void An_import_that_exists_neither_in_the_system_directory_nor_beside_the_image_is_refused()
    {
        Image("app/native.dll", ["KERNEL32.dll", "madeup.dll"]);

        var report = NativeImportAudit.Scan(_dir, name => name == "KERNEL32.dll");

        Assert.Equal(["app/native.dll>madeup.dll"], Missing(report));
    }

    /// <summary>Shipping the runtime app-local is the other way out, and it must be enough.</summary>
    [Fact]
    public void An_import_shipped_beside_the_image_is_accepted()
    {
        Image("app/juce_sharp.dll", ["VCRUNTIME140.dll"]);
        File.WriteAllBytes(Path.Combine(_dir, "app", "VCRUNTIME140.dll"), SyntheticPe.Build([], []));

        Assert.True(NativeImportAudit.Scan(_dir, _ => false).IsClean);
    }

    /// <summary>The loader looks in the image's own folder, not anywhere in the payload.</summary>
    [Fact]
    public void An_import_shipped_in_another_folder_is_refused()
    {
        Image("app/juce_sharp.dll", ["VCRUNTIME140.dll"]);
        File.WriteAllBytes(Path.Combine(_dir, "VCRUNTIME140.dll"), SyntheticPe.Build([], []));

        Assert.Equal(["app/juce_sharp.dll>VCRUNTIME140.dll"], Missing(NativeImportAudit.Scan(_dir, _ => false)));
    }

    [Fact]
    public void A_delay_loaded_import_nothing_ships_is_refused_too()
    {
        Image("native.dll", ["KERNEL32.dll"], ["MSVCP140.dll"]);

        Assert.Equal(["native.dll>MSVCP140.dll"], Missing(NativeImportAudit.Scan(_dir, name => name == "KERNEL32.dll")));
    }

    [Fact]
    public void A_managed_assembly_in_the_payload_is_not_audited()
    {
        File.Copy(typeof(NativeImportAudit).Assembly.Location, Path.Combine(_dir, "DotCharter.Services.dll"));

        var report = NativeImportAudit.Scan(_dir, _ => false);

        Assert.True(report.IsClean);
        Assert.Equal(0, report.ImagesChecked);
    }

    // ---- no exceptions ----------------------------------------------------------

    /// <summary>
    ///     ONNX Runtime imports the dynamic runtime. It passes only because the release ships
    ///     those four DLLs beside it; the same image without them is refused, whatever the
    ///     machine's system directory holds.
    /// </summary>
    [Fact]
    public void ONNX_Runtime_passes_with_the_runtime_beside_it_and_fails_without()
    {
        string[] runtime = ["MSVCP140.dll", "MSVCP140_1.dll", "VCRUNTIME140.dll", "VCRUNTIME140_1.dll"];
        Image("app/onnxruntime.dll", ["KERNEL32.dll", .. runtime]);
        Image("app/onnxruntime_providers_shared.dll", ["KERNEL32.dll", "VCRUNTIME140.dll"]);

        var without = NativeImportAudit.Scan(_dir, MachineWithRedistributable);
        Assert.Equal(
            ["app/onnxruntime.dll>MSVCP140.dll", "app/onnxruntime.dll>MSVCP140_1.dll", "app/onnxruntime.dll>VCRUNTIME140.dll",
             "app/onnxruntime.dll>VCRUNTIME140_1.dll", "app/onnxruntime_providers_shared.dll>VCRUNTIME140.dll"],
            Missing(without));

        foreach (var name in runtime)
            Image("app/" + name, ["KERNEL32.dll", "api-ms-win-crt-runtime-l1-1-0.dll"]);

        var with = NativeImportAudit.Scan(_dir, MachineWithRedistributable);
        Assert.True(with.IsClean, string.Join("; ", Missing(with)));
    }

    [Fact]
    public void A_runtime_DLL_beside_one_image_does_not_satisfy_an_image_in_another_folder()
    {
        Image("app/VCRUNTIME140.dll", ["KERNEL32.dll"]);
        Image("app/sub/other.dll", ["VCRUNTIME140.dll"]);

        var report = NativeImportAudit.Scan(_dir, MachineWithRedistributable);

        Assert.Equal(["app/sub/other.dll>VCRUNTIME140.dll"], Missing(report));
    }

    // ---- the product ---------------------------------------------------------------

    /// <summary>
    ///     The native runtime in <c>JuceSharp/src/JuceSharp/runtimes/win-x64/native</c> (built by
    ///     CMake, or fetched prebuilt for the gitlink) is what the release ships beside the app, and
    ///     it must run on a Windows with no Visual C++ redistributable: every import is a system DLL
    ///     or sits beside it. Non-vacuity: the v1.0.0 pair (dynamic CRT) fails this case.
    /// </summary>
    [Fact]
    public void The_native_runtime_this_checkout_builds_imports_only_windows_system_dlls()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("The system DLL set is Windows'.");

        var native = Path.Combine(TestPaths.RepoRoot(), "JuceSharp", "src", "JuceSharp", "runtimes", "win-x64", "native");
        if (!File.Exists(Path.Combine(native, "juce_sharp.dll")))
            Assert.Skip($"No native runtime under {native} (not built, and not fetched in this environment).");

        var report = NativeImportAudit.Scan(native);

        Assert.True(report.ImagesChecked >= 2, "expected juce_sharp.dll and juce_vst_host.exe to be audited");
        Assert.True(report.IsClean,
            "The native runtime imports a DLL a clean Windows does not have: "
            + string.Join("; ", Missing(report)) + ". Link the MSVC runtime statically (JUCESHARP_STATIC_CRT).");
    }

    /// <summary>A minimal, loader-valid PE32+ image carrying only an import table and a delay-import table.</summary>
    private static class SyntheticPe
    {
        public static byte[] Build(string[] imports, string[] delayImports)
        {
            var section = new List<byte>();
            var importRva = imports.Length == 0 ? 0 : SectionRva + section.Count;
            Table(section, imports, descriptorSize: 20, nameOffset: 12, flagsAtZero: false);
            while (section.Count % 8 != 0) section.Add(0);
            var delayRva = delayImports.Length == 0 ? 0 : SectionRva + section.Count;
            Table(section, delayImports, descriptorSize: 32, nameOffset: 4, flagsAtZero: true);

            var rawSize = (section.Count + 0x1FF) / 0x200 * 0x200;
            var file = new byte[HeadersBytes + Math.Max(rawSize, 0x200)];
            var span = file.AsSpan();

            span[0] = (byte)'M';
            span[1] = (byte)'Z';
            BinaryPrimitives.WriteInt32LittleEndian(span[0x3C..], 0x40);

            span[0x40] = (byte)'P';
            span[0x41] = (byte)'E';
            var coff = span[0x44..];
            BinaryPrimitives.WriteUInt16LittleEndian(coff, 0x8664); // AMD64
            BinaryPrimitives.WriteUInt16LittleEndian(coff[2..], 1); // one section
            BinaryPrimitives.WriteUInt16LittleEndian(coff[16..], 240); // optional header size
            BinaryPrimitives.WriteUInt16LittleEndian(coff[18..], 0x2022); // executable | large address aware | DLL

            var optional = span[0x58..];
            BinaryPrimitives.WriteUInt16LittleEndian(optional, 0x20B); // PE32+
            BinaryPrimitives.WriteInt64LittleEndian(optional[24..], 0x180000000);
            BinaryPrimitives.WriteInt32LittleEndian(optional[32..], 0x1000); // section alignment
            BinaryPrimitives.WriteInt32LittleEndian(optional[36..], 0x200); // file alignment
            BinaryPrimitives.WriteUInt16LittleEndian(optional[40..], 6); // OS version
            BinaryPrimitives.WriteUInt16LittleEndian(optional[48..], 6); // subsystem version
            BinaryPrimitives.WriteInt32LittleEndian(optional[56..], SectionRva + (section.Count + 0xFFF) / 0x1000 * 0x1000);
            BinaryPrimitives.WriteInt32LittleEndian(optional[60..], HeadersBytes);
            BinaryPrimitives.WriteUInt16LittleEndian(optional[68..], 3); // console subsystem
            BinaryPrimitives.WriteInt64LittleEndian(optional[72..], 0x100000);
            BinaryPrimitives.WriteInt64LittleEndian(optional[80..], 0x1000);
            BinaryPrimitives.WriteInt64LittleEndian(optional[88..], 0x100000);
            BinaryPrimitives.WriteInt64LittleEndian(optional[96..], 0x1000);
            BinaryPrimitives.WriteInt32LittleEndian(optional[108..], 16); // data directory count
            WriteDirectory(optional, ImportDirectoryIndex, importRva, imports.Length == 0 ? 0 : (imports.Length + 1) * 20);
            WriteDirectory(optional, DelayImportDirectoryIndex, delayRva, delayImports.Length == 0 ? 0 : (delayImports.Length + 1) * 32);

            var header = span[(0x58 + 240)..];
            ".idata\0\0"u8.CopyTo(header);
            BinaryPrimitives.WriteInt32LittleEndian(header[8..], section.Count); // virtual size
            BinaryPrimitives.WriteInt32LittleEndian(header[12..], SectionRva);
            BinaryPrimitives.WriteInt32LittleEndian(header[16..], rawSize);
            BinaryPrimitives.WriteInt32LittleEndian(header[20..], HeadersBytes);
            BinaryPrimitives.WriteUInt32LittleEndian(header[36..], 0x40000040); // initialised data, readable

            section.CopyTo(file, HeadersBytes);
            return file;
        }

        private static void WriteDirectory(Span<byte> optional, int index, int rva, int size)
        {
            BinaryPrimitives.WriteInt32LittleEndian(optional[(112 + index * 8)..], rva);
            BinaryPrimitives.WriteInt32LittleEndian(optional[(112 + index * 8 + 4)..], size);
        }

        /// <summary>Descriptors first (zero-terminated), then the NUL-terminated names they point at.</summary>
        private static void Table(List<byte> section, string[] names, int descriptorSize, int nameOffset, bool flagsAtZero)
        {
            if (names.Length == 0) return;

            var start = section.Count;
            var namesAt = start + (names.Length + 1) * descriptorSize;
            var nameBytes = new List<byte>();
            for (var i = 0; i < names.Length; i++)
            {
                var descriptor = new byte[descriptorSize];
                if (flagsAtZero) BinaryPrimitives.WriteInt32LittleEndian(descriptor, 1); // RVAs, not VAs
                BinaryPrimitives.WriteInt32LittleEndian(descriptor.AsSpan(nameOffset), SectionRva + namesAt + nameBytes.Count);
                section.AddRange(descriptor);
                nameBytes.AddRange(System.Text.Encoding.ASCII.GetBytes(names[i]));
                nameBytes.Add(0);
            }

            section.AddRange(new byte[descriptorSize]);
            section.AddRange(nameBytes);
        }
    }
}
