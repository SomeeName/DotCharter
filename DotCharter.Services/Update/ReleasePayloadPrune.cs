namespace DotCharter.Services.Update;

/// <summary>Why a published file is not worth shipping.</summary>
public enum PruneReason
{
    /// <summary>Keep it — the payload needs this file.</summary>
    Keep,

    /// <summary>Debug symbols for third-party native code we cannot debug into.</summary>
    ForeignNativeSymbols,

    /// <summary>An IntelliSense documentation file for a sibling assembly.</summary>
    XmlDocumentation,

    /// <summary>A C/C++ import library — a link-time artefact with no runtime role.</summary>
    ImportLibrary,

    /// <summary>An ONNX Runtime execution provider this app never requests.</summary>
    UnusedOnnxProvider
}

/// <summary>
///     Decides which files of a published payload are dead weight in the download.
///     <para>
///         The v0.47.0 release was <b>417 MB compressed / 817 MB on disk</b>, and a
///         measurement of the shipped zip's own per-entry compressed sizes said a
///         meaningful slice of that is content no install has ever executed: debug
///         symbols for Skia and HarfBuzz (27.9 MB compressed — we cannot step into
///         either), XML documentation files (1.4 MB, an IDE aid for people
///         referencing our assemblies as a library, which nobody does), Windows
///         import libraries (link-time only), and an execution provider nothing
///         asks for.
///     </para>
///     <para>
///         The rules are stated as PROPERTIES rather than as a file list, so a
///         package update that renames or adds one of these is still caught. That
///         matters more than it sounds: the payload is whatever NuGet decided to
///         lay down, and every one of these categories arrived without anyone
///         choosing it.
///     </para>
///     <para>
///         <b>Our own symbols stay.</b> They are 1.8 MB compressed and they are the
///         difference between a crash report with line numbers and one with
///         hexadecimal offsets. This prune is about files with no reader, not about
///         the smallest possible zip.
///     </para>
/// </summary>
public static class ReleasePayloadPrune
{
    /// <summary>
    ///     Assemblies whose debug symbols we keep — the ones we can actually open
    ///     the source of. Matched case-insensitively as a filename prefix.
    /// </summary>
    private static readonly string[] OurAssemblyPrefixes = ["DotCharter", "JuceSharp"];

    /// <summary>
    ///     ONNX Runtime providers DotCharter never requests. The app ships the
    ///     DirectML build, which carries none of these; the entry stays so a payload
    ///     that ever picks up the Gpu package again does not ship TensorRT, which
    ///     needs an SDK this app neither ships nor downloads.
    /// </summary>
    private static readonly string[] UnusedProviders = ["onnxruntime_providers_tensorrt.dll"];

    /// <summary>
    ///     Decides one file, given its name and a way to ask whether a sibling file
    ///     exists in the same directory.
    ///     <para>
    ///         <paramref name="siblingExists" /> is what makes the XML rule safe: a
    ///         documentation file is only ever recognised as one when the assembly
    ///         it documents sits beside it. A configuration or data file that
    ///         happens to end in <c>.xml</c> has no such sibling and is kept.
    ///     </para>
    /// </summary>
    /// <param name="fileName">File name only, no directory.</param>
    /// <param name="siblingExists">Answers whether a file of that name exists in
    /// the same directory.</param>
    public static PruneReason Classify(string fileName, Func<string, bool> siblingExists)
    {
        ArgumentNullException.ThrowIfNull(siblingExists);
        if (string.IsNullOrWhiteSpace(fileName)) return PruneReason.Keep;

        var name = Path.GetFileName(fileName);
        var extension = Path.GetExtension(name);

        if (extension.Equals(".lib", StringComparison.OrdinalIgnoreCase))
            return PruneReason.ImportLibrary;

        if (extension.Equals(".pdb", StringComparison.OrdinalIgnoreCase))
            return IsOurs(name) ? PruneReason.Keep : PruneReason.ForeignNativeSymbols;

        if (extension.Equals(".xml", StringComparison.OrdinalIgnoreCase))
        {
            var assembly = Path.ChangeExtension(name, ".dll");
            return siblingExists(assembly) ? PruneReason.XmlDocumentation : PruneReason.Keep;
        }

        foreach (var provider in UnusedProviders)
        {
            if (name.Equals(provider, StringComparison.OrdinalIgnoreCase))
                return PruneReason.UnusedOnnxProvider;
        }

        return PruneReason.Keep;
    }

    /// <summary>True when this file belongs to code we build and can debug into.</summary>
    private static bool IsOurs(string fileName)
    {
        foreach (var prefix in OurAssemblyPrefixes)
        {
            if (fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    ///     Human-readable reason, for the release script's log. A prune that cannot
    ///     say why it removed a file is one nobody can audit later.
    /// </summary>
    public static string Describe(PruneReason reason) => reason switch
    {
        PruneReason.ForeignNativeSymbols => "third-party native debug symbols",
        PruneReason.XmlDocumentation => "XML documentation for a sibling assembly",
        PruneReason.ImportLibrary => "C/C++ import library (link-time only)",
        PruneReason.UnusedOnnxProvider => "ONNX Runtime provider this app never requests",
        _ => "kept"
    };
}
