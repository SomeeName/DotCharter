using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using DotCharter.Tone.Infrastructure.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.UI.Controls.Markdown;

/// <summary>
/// Renders GitHub-flavored markdown (the subset release notes use) as themed
/// Avalonia text: headings, lists, bold/italic/strike, inline + fenced code,
/// clickable http(s) links. All colors/sizes come from Styles/Markdown.axaml
/// via style classes — nothing visual is hard-coded here. Text stays
/// selectable; links open in the default browser on click (press+release on
/// the same link without dragging, so text selection keeps working).
/// </summary>
public sealed class MarkdownView : StackPanel
{
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownView, string?>(nameof(Markdown));

    private static readonly FontFamily CodeFont = new("Consolas, Menlo, Monospace");

    // Lazy: Cursor needs the platform's ICursorFactory, which only exists once
    // the app is up — a field initializer would break plain unit-test construction.
    private static Cursor? _handCursor;
    private static Cursor HandCursor => _handCursor ??= new Cursor(StandardCursorType.Hand);

    static MarkdownView() => MarkdownProperty.Changed.AddClassHandler<MarkdownView>((view, _) => view.Rebuild());

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    private void Rebuild()
    {
        Children.Clear();
        foreach (var block in MarkdownParser.Parse(Markdown))
            Children.Add(BuildBlock(block));
    }

    private static Control BuildBlock(MdBlock block)
    {
        switch (block.Kind)
        {
            case MdBlockKind.Heading:
                return BuildTextBlock(block.Inlines, $"MdH{Math.Clamp(block.HeadingLevel, 1, 3)}");

            case MdBlockKind.Bullet:
            {
                var row = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                    Margin = new Thickness(4 + block.IndentLevel * 16, 0, 0, 0)
                };
                var glyph = new TextBlock
                {
                    Text = block.Marker ?? block.IndentLevel switch { 0 => "•", 1 => "◦", _ => "▪" },
                    VerticalAlignment = VerticalAlignment.Top
                };
                glyph.Classes.Add("MdBulletGlyph");
                row.Children.Add(glyph);
                var content = BuildTextBlock(block.Inlines, "MdPara");
                Grid.SetColumn(content, 1);
                row.Children.Add(content);
                return row;
            }

            case MdBlockKind.Code:
            {
                var body = new SelectableTextBlock { Text = block.Literal ?? string.Empty };
                body.Classes.Add("MdCodeText");
                var border = new Border { Child = body };
                border.Classes.Add("MdCodeBlock");
                return border;
            }

            case MdBlockKind.Rule:
            {
                var rule = new Border();
                rule.Classes.Add("MdRule");
                return rule;
            }

            default:
                return BuildTextBlock(block.Inlines, "MdPara");
        }
    }

    private static SelectableTextBlock BuildTextBlock(IReadOnlyList<MdInline> inlines, string blockClass)
    {
        var tb = new SelectableTextBlock();
        tb.Classes.Add("MdText");
        tb.Classes.Add(blockClass);

        List<LinkRegion>? links = null;
        var position = 0;
        foreach (var inline in inlines)
        {
            // Hard breaks are plain "\n" runs (not LineBreak elements) so the
            // link hit-test positions below stay one-char-per-char exact.
            if (inline.IsLineBreak)
            {
                tb.Inlines!.Add(new Run("\n"));
                position += 1;
                continue;
            }

            var run = new Run(inline.Text);
            if (inline.Bold) run.FontWeight = FontWeight.SemiBold;
            if (inline.Italic) run.FontStyle = FontStyle.Italic;
            if (inline.Strike) run.TextDecorations = TextDecorations.Strikethrough;
            if (inline.Code)
            {
                run.FontFamily = CodeFont;
                run.Bind(TextElement.BackgroundProperty, run.GetResourceObservable("SurfaceHoverStrong"));
            }
            if (inline.LinkUrl is { } url)
            {
                run.TextDecorations = TextDecorations.Underline;
                run.Bind(TextElement.ForegroundProperty, run.GetResourceObservable("AccentColor"));
                (links ??= []).Add(new LinkRegion(position, inline.Text.Length, url));
            }

            tb.Inlines!.Add(run);
            position += inline.Text.Length;
        }

        if (links != null) AttachLinkHandlers(tb, links);
        return tb;
    }

    private sealed record LinkRegion(int Start, int Length, string Url);

    private static void AttachLinkHandlers(SelectableTextBlock tb, List<LinkRegion> links)
    {
        LinkRegion? pressedLink = null;
        var pressedAt = default(Point);

        tb.AddHandler(PointerPressedEvent, (_, e) =>
        {
            pressedAt = e.GetPosition(tb);
            pressedLink = HitLink(tb, links, pressedAt);
        }, RoutingStrategies.Tunnel);

        tb.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            var point = e.GetPosition(tb);
            var moved = Math.Abs(point.X - pressedAt.X) + Math.Abs(point.Y - pressedAt.Y);
            if (pressedLink != null && moved < 4 && HitLink(tb, links, point) == pressedLink)
                OpenUrl(pressedLink.Url);
            pressedLink = null;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);

        tb.AddHandler(PointerMovedEvent, (_, e) =>
        {
            tb.Cursor = HitLink(tb, links, e.GetPosition(tb)) != null ? HandCursor : Cursor.Default;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private static LinkRegion? HitLink(SelectableTextBlock tb, List<LinkRegion> links, Point point)
    {
        var local = new Point(point.X - tb.Padding.Left, point.Y - tb.Padding.Top);
        var hit = tb.TextLayout.HitTestPoint(local);
        if (!hit.IsInside) return null;
        foreach (var link in links)
        {
            if (hit.TextPosition >= link.Start && hit.TextPosition < link.Start + link.Length)
                return link;
        }
        return null;
    }

    private static void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return;
        try
        {
            App.Services?.GetService<IShellLauncher>()?.OpenUrl(uri.AbsoluteUri);
        }
        catch
        {
            // Browser launch is best-effort; a missing handler must not crash the dialog.
        }
    }
}
