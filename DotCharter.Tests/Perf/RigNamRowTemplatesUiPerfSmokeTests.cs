using System.Collections;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using DotCharter.Tone.Online.Tone3000;
using DotCharter.Tone.UI.ViewModels.PlayMode;
using DotCharter.Localization;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The NAM capture browser's two row templates live in their own dictionary,
///     <c>Views/PlayMode/RigNamRowTemplates.axaml</c>, merged by <c>RigTonesView</c>
///     (#242 moved them out to keep the view inside its size budget). A template is
///     built only when a row exists, and the rig's other headless cases never seed
///     one, so a template that no longer resolves — a missing key, a compiled binding
///     that cannot reach <see cref="NamCaptureBrowserViewModel" /> through
///     <c>$parent[ItemsControl]</c>, a family theme that is not loaded — would ship
///     silently. This builds both lists over the 63 bundled captures' names (and a
///     page of TONE3000 results with long titles), lays them out at the Tones column's
///     width and asserts every row measured and every action is bound.
/// </summary>
[Collection("HeadlessUI")]
public sealed class RigNamRowTemplatesUiPerfSmokeTests
{
    [Fact]
    public async Task BothRowTemplates_RealizeAndLayOut_OverTheBundledCaptures()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var templates = (ResourceDictionary)AvaloniaXamlLoader.Load(
                new Uri("avares://DotCharter.Tone.UI/Views/PlayMode/RigNamRowTemplates.axaml"));
            var browser = new NamCaptureBrowserViewModel(
                Stub<ITone3000Service>(), Stub<ITone3000AuthService>(), Stub<INamCaptureLibrary>(),
                () => new FakeDspChainService(), LocalizationService.Instance, () => { });

            var names = Directory.EnumerateFiles(
                    Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "NamCaptures"), "*.nam")
                .Select(Path.GetFileNameWithoutExtension).Order(StringComparer.Ordinal).ToList();
            Assert.True(names.Count >= 60, $"only {names.Count} bundled captures found");
            var id = 0;
            foreach (var name in names)
            {
                id++;
                browser.LibraryRows.Add(new NamLibraryRowViewModel(new NamCaptureManifest(
                    id, id, $"{name} — a capture title long enough to trim at the column's edge", "Bundled with DotCharter",
                    id % 3 == 0 ? "pedal" : "amp", "nam", "standard", "CC-BY-4.0", "https://example.invalid/license",
                    "https://example.invalid/tone", $"{name}.nam", "00", 5, DateTimeOffset.UnixEpoch), LocalizationService.Instance));
                if (id <= 25)
                {
                    browser.Results.Add(new NamToneRowViewModel(new Tone3000Tone(
                        id, $"{name} through a very long signal chain description", "a capturist with a long handle",
                        "amp", "CC-BY", "https://example.invalid/by", $"https://example.invalid/tones/{id}", "",
                        ["nam"], ["standard"], 3), isDownloaded: id % 2 == 0, localization: LocalizationService.Instance));
                }
            }

            foreach (var (key, rows) in new[]
                     {
                         ("RigNamLibraryRowTemplate", (IEnumerable)browser.LibraryRows),
                         ("RigNamToneRowTemplate", browser.Results)
                     })
            {
                var list = new ItemsControl
                {
                    ItemTemplate = (IDataTemplate)templates[key]!,
                    ItemsSource = rows,
                    DataContext = browser
                };
                var window = new Window { Width = 560, Height = 4000, Content = new ScrollViewer { Content = list } };
                window.Show();
                try
                {
                    list.UpdateLayout();
                    var count = rows.Cast<object>().Count();
                    var borders = list.GetVisualDescendants().OfType<Border>()
                        .Where(b => b.Classes.Contains("FireListRow")).ToList();
                    Assert.True(borders.Count == count, $"{key}: {borders.Count} of {count} rows realized");
                    Assert.All(borders, b => Assert.True(b.Bounds.Width > 100 && b.Bounds.Height > 20,
                        $"{key}: a row measured {b.Bounds.Size}, so its theme or template did not apply"));
                    // the family theme is worn and applied, not just named
                    Assert.All(borders, b => Assert.NotNull(b.Theme));
                    Assert.All(borders, b => Assert.True(b.Bounds.Right <= list.Bounds.Width + 0.5,
                        $"{key}: a row overflows the column ({b.Bounds.Right} > {list.Bounds.Width})"));
                    var buttons = list.GetVisualDescendants().OfType<Button>().ToList();
                    Assert.NotEmpty(buttons);
                    Assert.All(buttons, b => Assert.NotNull(b.Command));
                }
                finally
                {
                    window.Content = null;
                    window.Close();
                }
            }
        }, TestContext.Current.CancellationToken);
    }

    private static T Stub<T>() where T : class => DispatchProxy.Create<T, DefaultAnswers>();

    /// <summary>Answers every call with a default (a completed task, an empty list):
    /// the rows only read the browser's command properties.</summary>
    public class DefaultAnswers : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var type = targetMethod!.ReturnType;
            if (type == typeof(void)) return null;
            if (type == typeof(Task)) return Task.CompletedTask;
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var inner = type.GetGenericArguments()[0];
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(inner)
                    .Invoke(null, [inner.IsValueType ? Activator.CreateInstance(inner) : null]);
            }

            if (type == typeof(string)) return "";
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
                return Array.CreateInstance(type.GetGenericArguments()[0], 0);
            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }
    }
}
