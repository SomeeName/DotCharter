using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.RegularExpressions;

namespace DotCharter.Services.Update;

/// <summary>One DLL a native image imports that nothing in the payload satisfies.</summary>
public sealed record NativeImportProblem(string RelativePath, string ImportedDll, string Detail);

/// <summary>What <see cref="NativeImportAudit.Scan" /> looked at, and what it refused.</summary>
public sealed record NativeImportReport(int ImagesChecked, IReadOnlyList<NativeImportProblem> Problems)
{
    public bool IsClean => Problems.Count == 0;
}

/// <summary>
///     Refuses a native DLL or exe whose import table names a DLL that is neither
///     shipped next to it nor part of Windows.
///     <para>
///         Incident: v1.0.0 shipped a <c>juce_sharp.dll</c> linked against the dynamic
///         C++ runtime, so it imported <c>VCRUNTIME140.dll</c>, <c>VCRUNTIME140_1.dll</c>,
///         <c>MSVCP140.dll</c> and <c>MSVCP140_ATOMIC_WAIT.dll</c>. The zip is
///         self-contained for .NET and contained none of them: on a Windows without a
///         recent Visual C++ 2015-2022 x64 redistributable (a clean install, a VM) the
///         native engine failed to load and there was no audio. Nothing noticed, because
///         every development machine and most gaming PCs have the redistributable.
///     </para>
///     <para>
///         The rule, per image: every DLL in the import table (and the delay-import
///         table) must be (a) a file in the image's own directory, or (b) a Windows
///         system DLL: an <c>api-ms-win-*</c> / <c>ext-ms-win-*</c> API set, or a file
///         that exists in the system directory and is not the Visual C++ runtime
///         (<c>VCRUNTIME*</c>, <c>MSVCP*</c>, <c>CONCRT*</c>, <c>VCOMP*</c>, <c>MSVCR*</c>,
///         the debug <c>ucrtbased</c>…). Those exist in the system directory of any
///         machine that happens to carry the redistributable, which is exactly why a
///         plain existence check let the incident through on the machines that built it.
///         The universal CRT (<c>api-ms-win-crt-*</c>) is part of Windows 10 and 11, the
///         app's floor, so it is allowed.
///     </para>
///     <para>
///         Managed assemblies are skipped (their native imports are the host's). A
///         file the loader would not map as a PE image is the integrity check's
///         business, <see cref="ReleasePayloadIntegrity" />.
///     </para>
/// </summary>
public static partial class NativeImportAudit
{
    private const int MaxTableEntries = 4096;
    private const int ImportDescriptorNameOffset = 12;
    private const int ImportDescriptorSize = 20;
    private const int DelayDescriptorNameOffset = 4;
    private const int DelayDescriptorSize = 32;

    /// <summary>The DLLs a native image imports (import table, then delay-import table); empty for a managed or unreadable image.</summary>
    public static IReadOnlyList<string> ReadImports(Stream stream)
    {
        stream.Position = 0;
        using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
        var names = new List<string>();
        try
        {
            var header = pe.PEHeaders.PEHeader;
            if (header is null || pe.HasMetadata) return names;

            ReadTable(pe, header.ImportTableDirectory, ImportDescriptorSize, ImportDescriptorNameOffset, names);
            ReadTable(pe, header.DelayImportTableDirectory, DelayDescriptorSize, DelayDescriptorNameOffset, names);
        }
        catch (BadImageFormatException)
        {
            // The integrity check names an image whose headers do not parse; nothing to add here.
        }

        return names;
    }

    /// <summary>
    ///     Whether the loader finds <paramref name="dll" /> without anything shipped: an API set,
    ///     or a file in the system directory that is not the Visual C++ runtime.
    /// </summary>
    public static bool IsWindowsSystemDll(string dll, Func<string, bool> existsInSystemDirectory)
    {
        if (VisualCppRuntime().IsMatch(dll)) return false;

        return IsApiSet(dll) || existsInSystemDirectory(dll);
    }

    private static bool IsApiSet(string dll) =>
        dll.StartsWith("api-ms-win-", StringComparison.OrdinalIgnoreCase)
        || dll.StartsWith("ext-ms-win-", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     Audits every native image under <paramref name="payloadDir" />, recursively.
    ///     <paramref name="existsInSystemDirectory" /> defaults to the machine's system directory.
    /// </summary>
    public static NativeImportReport Scan(string payloadDir, Func<string, bool>? existsInSystemDirectory = null)
    {
        if (!Directory.Exists(payloadDir))
            throw new DirectoryNotFoundException($"No such payload directory: {payloadDir}");

        existsInSystemDirectory ??= ExistsInWindowsSystemDirectory;
        var problems = new List<NativeImportProblem>();
        var images = 0;
        foreach (var path in Directory.EnumerateFiles(payloadDir, "*", SearchOption.AllDirectories)
                     .Where(ReleasePayloadIntegrity.IsExecutableImage)
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            IReadOnlyList<string> imports;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                // A file that is not a PE image at all is the integrity check's to name.
                if (stream.Length < 2) continue;
                imports = ReadImports(stream);
            }

            if (imports.Count == 0) continue;

            images++;
            var directory = Path.GetDirectoryName(path)!;
            var relative = Path.GetRelativePath(payloadDir, path).Replace('\\', '/');
            foreach (var dll in imports.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (File.Exists(Path.Combine(directory, dll))) continue;
                if (IsWindowsSystemDll(dll, existsInSystemDirectory)) continue;

                problems.Add(new NativeImportProblem(relative, dll,
                    "imported, but not shipped beside it and not a Windows system DLL"));
            }
        }

        return new NativeImportReport(images, problems);
    }

    private static bool ExistsInWindowsSystemDirectory(string dll) =>
        File.Exists(Path.Combine(Environment.SystemDirectory, dll));

    private static void ReadTable(PEReader pe, DirectoryEntry table, int descriptorSize, int nameOffset, List<string> names)
    {
        if (table.Size == 0 || table.RelativeVirtualAddress == 0) return;

        var block = pe.GetSectionData(table.RelativeVirtualAddress);
        if (block.Length == 0) return;

        var reader = block.GetReader();
        for (var i = 0; i < MaxTableEntries && reader.RemainingBytes >= descriptorSize; i++)
        {
            var entry = reader.ReadBytes(descriptorSize);
            var nameRva = BitConverter.ToInt32(entry, nameOffset);
            if (nameRva == 0) return;

            var name = ReadAsciiName(pe, nameRva);
            if (!string.IsNullOrEmpty(name)) names.Add(name);
        }
    }

    private static string? ReadAsciiName(PEReader pe, int rva)
    {
        var block = pe.GetSectionData(rva);
        if (block.Length == 0) return null;

        var reader = block.GetReader();
        var builder = new StringBuilder();
        while (reader.RemainingBytes > 0)
        {
            var c = reader.ReadByte();
            if (c == 0) break;
            builder.Append((char)c);
        }

        return builder.ToString();
    }

    [GeneratedRegex(@"^(vcruntime|msvcp|msvcr|concrt|vccorlib|vcomp|vcamp|mfc|atl)\d|^ucrtbased", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VisualCppRuntime();
}
