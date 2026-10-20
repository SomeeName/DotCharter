using System.Text;
using System.Xml.Linq;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     The editor's MENU BAR as source, for every lint that reads it.
///     <para>
///         Since #653 the bar is NINE files: <c>Views/Shell/MainMenuView.axaml</c>
///         lists the top-level menus as elements (<c>&lt;menus:FileMenu /&gt;</c>),
///         and each menu — header and every row under it — is its own
///         <c>Views/Shell/Menus/&lt;Name&gt;.axaml</c>. A lint that kept reading
///         <c>MainMenuView.axaml</c> alone would see no <c>MenuItem</c> at all and
///         pass VACUOUSLY on every "this row must exist / must not exist" rule, so
///         every menu lint reads the bar through here instead.
///     </para>
///     <para>
///         The menus are derived from the bar's own markup, in bar order, and the
///         set is checked both ways: a menu element whose file is missing, or a
///         <c>Menus/*.axaml</c> the bar does not list, throws rather than shrinking
///         what the lints see.
///     </para>
/// </summary>
internal static class MainMenuSource
{
    private const string MenusNamespace = "using:DotCharter.UI.Views.Shell.Menus";

    /// <summary>The fewest top-level menus the bar has ever had; fewer means the
    /// derivation broke, not that the bar shrank.</summary>
    private const int MinimumMenus = 8;

    public static string BarFile => UiSourceTree.Resolve(Path.Combine(
        TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Shell", "MainMenuView.axaml"));

    public static string MenusDirectory => UiSourceTree.Resolve(Path.Combine(
        TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Shell", "Menus"));

    /// <summary>Every top-level menu file, in the order the bar lists them.</summary>
    public static IReadOnlyList<string> MenuFiles()
    {
        var bar = XDocument.Load(BarFile);
        var files = Placeholders(bar).Select(p => p.File).ToList();

        var onDisk = Directory.EnumerateFiles(MenusDirectory, "*.axaml")
            .Select(Path.GetFullPath)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var listed = files.Select(Path.GetFullPath).Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (!onDisk.SequenceEqual(listed, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Views/Shell/Menus and the bar disagree — every menu file must be listed in " +
                $"MainMenuView.axaml and every listed menu must have a file. On disk: " +
                $"[{string.Join(", ", onDisk.Select(Path.GetFileName))}]; listed: " +
                $"[{string.Join(", ", listed.Select(Path.GetFileName))}]");
        }

        return files;
    }

    /// <summary>The bar file followed by every menu file.</summary>
    public static IReadOnlyList<string> Files() => [BarFile, .. MenuFiles()];

    /// <summary>The whole bar's markup as one text: the bar file, then each menu
    /// file in bar order. For the lints that grep for a key, a binding or a
    /// pattern anywhere in the menu.</summary>
    public static string ReadAllText()
    {
        var text = new StringBuilder();
        foreach (var file in Files()) text.Append(File.ReadAllText(file)).Append('\n');
        return text.ToString();
    }

    /// <summary>
    ///     The bar as ONE document, shaped exactly as it was when it was one file:
    ///     each <c>&lt;menus:XMenu /&gt;</c> element is replaced by the root
    ///     <c>MenuItem</c> of its file (which carries the <c>FireMenuTop</c> class
    ///     and the header). Parent / child walks across the menu boundary —
    ///     "the rows of the Tools menu", "the path to this row" — work unchanged.
    /// </summary>
    public static XDocument Document()
    {
        _ = MenuFiles(); // the two-way check: no unlisted file, no missing one
        var bar = XDocument.Load(BarFile);
        var placeholders = Placeholders(bar);

        foreach (var (element, file) in placeholders)
        {
            var root = XDocument.Load(file).Root
                       ?? throw new InvalidOperationException($"{Path.GetFileName(file)} has no root element");
            element.ReplaceWith(root);
        }

        return bar;
    }

    private static List<(XElement Element, string File)> Placeholders(XDocument bar)
    {
        var found = bar.Descendants()
            .Where(e => e.Name.NamespaceName == MenusNamespace)
            .Select(e => (Element: e, File: Path.Combine(MenusDirectory, e.Name.LocalName + ".axaml")))
            .ToList();
        if (found.Count < MinimumMenus)
        {
            throw new InvalidOperationException(
                $"MainMenuView.axaml lists {found.Count} menus (expected at least {MinimumMenus}) — " +
                $"the menu elements are no longer in {MenusNamespace}");
        }

        foreach (var (element, file) in found)
        {
            if (!File.Exists(file))
                throw new InvalidOperationException($"the bar lists <{element.Name.LocalName}> but {file} does not exist");
        }

        return found;
    }
}
