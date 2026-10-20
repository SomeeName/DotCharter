using System.Xml.Linq;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     The shell window's MARKUP as source, for every lint that reads it.
///     <para>
///         Since #654 it is TWO files: <c>Views/MainWindow.axaml</c> hosts
///         <c>&lt;shellViews:WindowChromeView x:Name="WindowChrome" /&gt;</c> in the
///         title bar's row, and the three chrome rows it used to declare inline —
///         <c>TitleBarRow</c> (with <c>MainMenuBarHost</c>), <c>BackBar</c> and
///         <c>WindowCaption</c> — are <c>Views/Shell/WindowChromeView.axaml</c>. A lint
///         that kept reading <c>MainWindow.axaml</c> alone would find no title bar, no
///         drag strip and no caption buttons, and every "this must NOT be there" rule
///         about them would pass VACUOUSLY, so every shell-markup lint reads through
///         here instead.
///     </para>
///     <para>
///         The set is checked both ways: the window must host the view exactly once
///         and the view must declare the three rows, or every reader throws rather
///         than shrinking what the lints see.
///     </para>
/// </summary>
internal static class MainWindowSource
{
    private const string ShellViewsNamespace = "clr-namespace:DotCharter.UI.Views.Shell;assembly=DotCharter";
    private const string ChromeView = "WindowChromeView";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>The rows the chrome view must declare; fewer means the file is not
    /// the chrome any more, not that the chrome shrank.</summary>
    private static readonly string[] ChromeRows = ["TitleBarRow", "BackBar", "WindowCaption"];

    public static string WindowFile => UiSourceTree.Resolve(Path.Combine(
        TestPaths.RepoRoot(), "DotCharter.UI", "Views", "MainWindow.axaml"));

    public static string ChromeFile => UiSourceTree.Resolve(Path.Combine(
        TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Shell", ChromeView + ".axaml"));

    /// <summary>The window file followed by the chrome view's.</summary>
    public static IReadOnlyList<string> Files()
    {
        Check();
        return [WindowFile, ChromeFile];
    }

    /// <summary>The window's markup as one text: <c>MainWindow.axaml</c>, then
    /// <c>WindowChromeView.axaml</c>. For the lints that grep for a name, a binding
    /// or a pattern anywhere in the shell.</summary>
    public static string ReadAllText() =>
        string.Join('\n', Files().Select(File.ReadAllText));

    /// <summary>
    ///     The window as ONE document: the <c>WindowChromeView</c> element is
    ///     replaced by the view's root, which keeps the element's own attributes
    ///     (<c>x:Name="WindowChrome"</c>, its <c>Grid.Row</c> and <c>Grid.Column</c>).
    ///     Parent / ancestor walks across the boundary — "the drag strip is inside
    ///     the title bar", "the title bar is in row 0" — see the real nesting.
    /// </summary>
    public static XDocument Document()
    {
        var window = XDocument.Load(WindowFile);
        var host = Hosts(window).Single();
        var chrome = LoadChrome();
        var root = chrome.Root!;
        foreach (var attribute in host.Attributes().Where(a => !a.IsNamespaceDeclaration))
            root.SetAttributeValue(attribute.Name, attribute.Value);
        host.ReplaceWith(root);
        return window;
    }

    private static void Check()
    {
        _ = Hosts(XDocument.Load(WindowFile));
        _ = LoadChrome();
    }

    private static List<XElement> Hosts(XDocument window)
    {
        var hosts = window.Descendants()
            .Where(e => e.Name.LocalName == ChromeView && e.Name.NamespaceName == ShellViewsNamespace)
            .ToList();
        if (hosts.Count != 1)
        {
            throw new InvalidOperationException(
                $"MainWindow.axaml hosts {hosts.Count} <shellViews:{ChromeView}> elements, expected exactly one — " +
                "the window chrome moved again; repoint MainWindowSource.");
        }

        return hosts;
    }

    private static XDocument LoadChrome()
    {
        if (!File.Exists(ChromeFile))
            throw new InvalidOperationException($"MainWindow.axaml hosts {ChromeView}, but {ChromeFile} does not exist");

        var chrome = XDocument.Load(ChromeFile);
        var names = chrome.Descendants().Select(e => (string?)e.Attribute(X + "Name")).ToHashSet(StringComparer.Ordinal);
        var missing = ChromeRows.Where(r => !names.Contains(r)).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"{ChromeView}.axaml no longer declares [{string.Join(", ", missing)}] — the chrome rows moved; " +
                "repoint MainWindowSource.");
        }

        return chrome;
    }
}
