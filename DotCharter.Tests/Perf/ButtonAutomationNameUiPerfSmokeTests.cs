using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Layout;

namespace DotCharter.Tests.Perf;

/// <summary>
///     What Avalonia actually ANNOUNCES for a button, by the shape of its content — the ground truth
///     <c>UI/AccessibleNameTests</c> lints the markup against. The live app (2026-09-23) exposed 18 of
///     the editor's 141 automation elements as "Avalonia.Controls.StackPanel" or
///     "Avalonia.Controls.Viewbox": a button whose face is an icon beside its words is named after the
///     PANEL, not the words. Avalonia 12.1.5 (#22378) stopped using the type name and now announces the
///     words inside the panel, so a panel button reads "Mix". <c>UI/AccessibleNameTests</c> still demands an
///     explicit name there on purpose: it does not depend on how a given Avalonia version walks the tree.
/// </summary>
[Collection("HeadlessUI")]
public sealed class ButtonAutomationNameUiPerfSmokeTests
{
    [Fact]
    public async Task DirectWords_And_WordsInsideAPanel_NameAButton()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var byString = new Button { Content = "Mix" };
            var byTextBlock = new Button { Content = new TextBlock { Text = "Mix" } };
            var byPanel = new Button
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children = { new PathIcon(), new TextBlock { Text = "Mix" } }
                }
            };
            var named = new Button { Content = new StackPanel { Children = { new PathIcon(), new TextBlock { Text = "Mix" } } } };
            AutomationProperties.SetName(named, "Mix");

            var window = new Window { Content = new StackPanel { Children = { byString, byTextBlock, byPanel, named } } };
            window.Show();
            window.UpdateLayout();

            Assert.Equal("Mix", NameOf(byString));
            Assert.Equal("Mix", NameOf(byTextBlock));
            Assert.Equal("Mix", NameOf(byPanel));
            Assert.Equal("Mix", NameOf(named));
            window.Close();
        }, TestContext.Current.CancellationToken);
    }

    private static string? NameOf(Control control) => ControlAutomationPeer.CreatePeerForElement(control).GetName();
}
