using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DotCharter.Tests.UI;

/// <summary>
///     Reading Play Mode's markup now that its screens are their own views (#294).
///     <para>
///         <c>PlayModeView.axaml</c> used to declare every screen inline, and a
///         dozen source lints read it by path: they find a screen root by
///         <c>x:Name</c>, slice the markup between two roots, or walk a screen's
///         XML. Each screen now lives in <c>Views/PlayMode/Screens/</c> and
///         <c>PlayModeView</c> only DECLARES it (<c>&lt;screens:GetReadyView /&gt;</c>),
///         so a lint still reading that one file would find nothing — and the
///         ones that only assert ABSENCE would pass vacuously.
///     </para>
///     <para>
///         So the lints read the screen set through here. <see cref="Text" /> is
///         <c>PlayModeView.axaml</c> with each declared screen view's markup
///         INLINED at its declaration — the host element kept (it carries the
///         screen's <c>IsVisible</c> gate and, for a deferred screen, sits inside its
///         <c>ContentTemplate</c>), the view's body as its content — recursively,
///         in document order. It is the markup the screen set had when it was one
///         file, so a lint that slices from <c>TuningCheckScreenRoot</c> to
///         <c>CountdownScreenRoot</c> still slices the same screen.
///     </para>
/// </summary>
internal static class PlayModeMarkup
{
    /// <summary>The XML namespace the lifted screen views are declared under.</summary>
    internal const string ScreensNamespace = "using:DotCharter.UI.Views.PlayMode.Screens";

    private static readonly Regex ScreenElement = new(
        @"<screens:(?<type>[A-Za-z0-9_]+)\b(?<attrs>[^>]*?)\s*/>", RegexOptions.Compiled);

    private static readonly Regex RootOpenTag = new(@"<UserControl\b[^>]*>", RegexOptions.Compiled);

    private static readonly Regex NamespaceDeclaration = new(@"\sxmlns(:\w+)?=""[^""]*""", RegexOptions.Compiled);

    private static string? _text;

    internal static string ViewPath() => UiSourceTree.Resolve(Path.Combine(
        TestPaths.RepoRoot(), "DotCharter.UI", "Views", "PlayMode", "PlayModeView.axaml"));

    internal static string ScreensDirectory() => Path.Combine(
        TestPaths.RepoRoot(), "DotCharter.UI", "Views", "PlayMode", "Screens");

    /// <summary>The lifted screen views' markup files, in name order.</summary>
    internal static IReadOnlyList<string> ScreenFiles() =>
        Directory.Exists(ScreensDirectory())
            ? [.. Directory.GetFiles(ScreensDirectory(), "*.axaml").Order(StringComparer.Ordinal)]
            : [];

    /// <summary>The path of the one Play Mode markup file that declares
    /// <c>x:Name="<paramref name="name" />"</c>.</summary>
    internal static string FileDeclaring(string name)
    {
        var files = new[] { ViewPath() }.Concat(ScreenFiles())
            .Where(f => File.ReadAllText(f).Contains($"x:Name=\"{name}\"", StringComparison.Ordinal))
            .ToList();
        Assert.True(files.Count == 1,
            $"expected exactly one Play Mode markup file to declare x:Name=\"{name}\", found {files.Count}");
        return files[0];
    }

    /// <summary><c>PlayModeView.axaml</c> with every declared screen view inlined (see the class remarks).</summary>
    internal static string Text() => _text ??= Inline(File.ReadAllText(ViewPath()), 0);

    /// <summary><see cref="Text" />, parsed. A fresh document per call: callers may walk or edit it.</summary>
    internal static XDocument Document() => XDocument.Parse(Text());

    /// <summary>The <c>IsVisible</c> expression that decides whether
    /// <paramref name="surface" /> shows: its own, else that of the view or
    /// deferred host it is the whole body of.</summary>
    internal static string? GateOf(XElement surface)
    {
        for (var element = surface; element is not null; element = WrapperOf(element))
        {
            if (element.Attribute("IsVisible")?.Value is { } gate) return gate;
        }

        return null;
    }

    /// <summary>The <c>ContentControl</c> host <paramref name="surface" /> is the
    /// deferred body of, or <c>null</c> when it is declared inline.</summary>
    internal static XElement? DeferredHostOf(XElement surface) =>
        surface.Parent is { } template && template.Name.LocalName == "DataTemplate" &&
        template.Parent is { } property && property.Name.LocalName == "ContentControl.ContentTemplate"
            ? property.Parent
            : null;

    /// <summary>The element whose gate governs <paramref name="element" /> because
    /// <paramref name="element" /> is its whole content: a lifted view's declaration
    /// for the view's root, a deferred host for its template body.</summary>
    private static XElement? WrapperOf(XElement element) =>
        element.Parent is { } parent && parent.Name.NamespaceName == ScreensNamespace &&
        parent.Elements().Count(e => !e.Name.LocalName.Contains('.', StringComparison.Ordinal)) == 1
            ? parent
            : DeferredHostOf(element);

    private static string Inline(string markup, int depth)
    {
        Assert.True(depth < 4, "the Play Mode screen views nest more than four deep — a view declares itself?");
        return ScreenElement.Replace(markup, m =>
        {
            var type = m.Groups["type"].Value;
            var path = Path.Combine(ScreensDirectory(), type + ".axaml");
            Assert.True(File.Exists(path), $"<screens:{type}> is declared, but {path} does not exist");

            var view = File.ReadAllText(path);
            var open = RootOpenTag.Match(view);
            var close = view.LastIndexOf("</UserControl>", StringComparison.Ordinal);
            Assert.True(open.Success && close > open.Index, $"{type}.axaml is not a UserControl");

            var namespaces = string.Concat(NamespaceDeclaration.Matches(open.Value).Select(n => n.Value));
            var body = view[(open.Index + open.Length)..close];
            return $"<screens:{type}{m.Groups["attrs"].Value}{namespaces}>{Inline(body, depth + 1)}</screens:{type}>";
        });
    }
}
