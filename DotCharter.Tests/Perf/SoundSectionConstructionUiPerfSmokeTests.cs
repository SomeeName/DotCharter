using Avalonia.Controls;
using DotCharter.UI;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.Views.Editor;
using DotCharter.UI.Views.Editor.Inspector;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     #601: every section view of the Sound tab's rail builds, attaches to a real window inside
///     the inspector grammar (<c>InspectorStyles.axaml</c>, which the tab inherits from
///     <see cref="InspectorView" />) and MEASURES non-zero over the app's own
///     <see cref="InspectorViewModel" /> — a view whose styles or resources failed still arranges,
///     it just has no size. The tab as a whole, with real selections, is
///     <see cref="SoundTabRailUiPerfSmokeTests" />.
/// </summary>
[Collection("HeadlessUI")]
public sealed class SoundSectionConstructionUiPerfSmokeTests
{
    public static TheoryData<Type> Sections =>
    [
        typeof(SoundTaskView), typeof(SoundPlacementView), typeof(SoundShapeView), typeof(SoundBulkCardView),
        typeof(SoundNoteTechniqueView), typeof(SoundChordTechniqueView), typeof(SoundFindingsView)
    ];

    [Theory]
    [MemberData(nameof(Sections))]
    public async Task EverySection_BuildsAttachesAndMeasures(Type viewType) =>
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var vm = (App.Services ?? throw new InvalidOperationException("the headless app session did not boot."))
                .GetRequiredService<InspectorViewModel>();
            var view = (Control)Activator.CreateInstance(viewType)!;
            view.DataContext = vm;
            var host = new UserControl { Content = view };
            host.Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://DotCharter/"))
            {
                Source = new Uri("avares://DotCharter/Views/Editor/InspectorStyles.axaml")
            });

            var window = new Window { Width = 420, Height = 900, Content = host };
            window.Show();
            try
            {
                window.UpdateLayout();
                Assert.True(view.Bounds is { Width: > 1, Height: > 1 }, $"{viewType.Name} laid out as {view.Bounds} — it rendered nothing.");
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
}
