using System.Xml.Linq;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     The module editor is a HOST (<c>RigModuleEditorView.axaml</c>) over one
///     view per editor family (<c>Views/PlayMode/RigEditors/*.axaml</c>). A
///     lint that reads "the editor" must read the whole set — reading the host
///     alone scans a file with no controls in it and passes vacuously (root
///     AGENTS.md § a file split breaks every lint that locates source by path).
/// </summary>
public static class RigEditorViews
{
    public static string HostPath(string repoRoot) =>
        Path.Combine(repoRoot, "DotCharter.Tone.UI", "Views", "PlayMode", "RigModuleEditorView.axaml");

    public static string FamilyDir(string repoRoot) =>
        Path.Combine(repoRoot, "DotCharter.Tone.UI", "Views", "PlayMode", "RigEditors");

    /// <summary>The pedal family's DETAILS (the AmpliTube layout, 2026-09-27): the
    /// capture card and library, the exact values, lyrics and meters that sat beside
    /// the pedal face until they moved into the host's details column.</summary>
    public static string PedalDetailsPath(string repoRoot) =>
        Path.Combine(repoRoot, "DotCharter.Tone.UI", "Views", "PlayMode", "RigPedalDetailsView.axaml");

    /// <summary>The host, the pedal details and every family view, host first.</summary>
    public static IReadOnlyList<string> Paths(string repoRoot)
    {
        var list = new List<string> { HostPath(repoRoot), PedalDetailsPath(repoRoot) };
        list.AddRange(UiSourceTree.EnumerateFiles(FamilyDir(repoRoot), "*.axaml").OrderBy(p => p, StringComparer.Ordinal));
        return list;
    }

    /// <summary>Every element of every editor file, with line info.</summary>
    public static IEnumerable<XElement> Elements(string repoRoot) =>
        Paths(repoRoot).SelectMany(p => XDocument.Load(p, LoadOptions.SetLineInfo).Root!.DescendantsAndSelf());

    public static IEnumerable<(string Path, XDocument Doc)> Documents(string repoRoot) =>
        Paths(repoRoot).Select(p => (p, XDocument.Load(p, LoadOptions.SetLineInfo)));
}
