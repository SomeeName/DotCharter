using System.Reflection;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;
using DotCharter.Localization;
using DotCharter.Tone.Online.Tone3000;
using DotCharter.Tone.UI.ViewModels.PlayMode;
using DotCharter.Tone.UI.Views.PlayMode;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The "Powered by TONE3000" credit (#867) that the capture browser must show:
///     it constructs, lays out with a size, wears an automation name and fires the
///     browser's command. A second case pins that <c>RigTonesView</c> hosts it, so
///     removing the line from the browser reads as a failure here.
/// </summary>
[Collection("HeadlessUI")]
public sealed class Tone3000CreditUiPerfSmokeTests
{
    [Fact]
    public async Task CreditLink_ConstructsLaysOutAndOpensTone3000()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            string? opened = null;
            var browser = new NamCaptureBrowserViewModel(
                Stub<ITone3000Service>(), Stub<ITone3000AuthService>(), Stub<INamCaptureLibrary>(),
                () => new FakeDspChainService(), LocalizationService.Instance, () => { },
                openUrl: url =>
                {
                    opened = url;
                    return true;
                });
            var view = new Tone3000CreditView { DataContext = browser };
            var window = new Window { Width = 400, Height = 200, Content = view };
            window.Show();
            try
            {
                view.UpdateLayout();
                var link = view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "Tone3000CreditLink");
                Assert.True(link.Bounds.Width > 0 && link.Bounds.Height > 0, $"credit measured {link.Bounds.Size}");
                Assert.Equal("Powered by TONE3000", link.Content as string);
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(link)));
                link.Command!.Execute(null);
                Assert.Equal("https://www.tone3000.com", opened);
            }
            finally
            {
                window.Content = null;
                window.Close();
            }
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public void TheCaptureBrowser_HostsTheCredit()
    {
        var xaml = File.ReadAllText(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.Tone.UI", "Views", "PlayMode", "RigTonesView.axaml"));
        Assert.Contains("<views:Tone3000CreditView", xaml);
    }

    private static T Stub<T>() where T : class => DispatchProxy.Create<T, RigNamRowTemplatesUiPerfSmokeTests.DefaultAnswers>();
}
