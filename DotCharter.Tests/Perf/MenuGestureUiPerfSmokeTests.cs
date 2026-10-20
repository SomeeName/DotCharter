using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using DotCharter.Localization;
using DotCharter.UI;
using DotCharter.UI.ViewModels.Shell;
using DotCharter.UI.Views.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The menu rows and toolbar tooltips #131 gave a chord, built for REAL against the app's
///     own shell view model: they construct, attach, lay out and print the chord the LIVE
///     <c>ProjectState.Keybinds</c> table holds — and print the new one after a rebind or after
///     the whole table is replaced (what Settings Cancel and a config import do). The default
///     gate's <c>UI/MenuGestureCoverageTests</c> proves every row is wired; this proves the
///     wiring resolves.
/// </summary>
/// <remarks>
///     Rows are found by their localized caption, which is what a user reads; the menus are
///     realized through <see cref="MenuFlyoutPrewarmer" /> exactly as the app does after first
///     paint, so nothing is opened.
/// </remarks>
[Collection("HeadlessUI")]
public sealed class MenuGestureUiPerfSmokeTests
{
    private static readonly Size Slot = new(1600, 1000);

    [Fact]
    public Task TheMenuRows_ShowTheLiveChord_AndFollowARebind() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var shell = Shell();
            var state = shell.ProjectState;
            var original = state.Keybinds;

            var view = new MainMenuView { DataContext = shell };
            var window = new Window { Width = Slot.Width, Height = Slot.Height, Content = view };
            window.Show();
            try
            {
                view.Measure(Slot);
                view.Arrange(new Rect(Slot));
                var menus = view.TopLevelMenus();
                Assert.True(menus.Sum(m => MenuFlyoutPrewarmer.Realize(m, Slot)) > 100, "the dropdowns were not realized");
                var rows = Rows(menus).ToList();

                MenuItem Row(string key) => rows.Single(r => r.Header?.ToString() == LocalizationService.Instance[key]);
                MenuItem Bookmark(int digit) => rows.Single(r => r.Header?.ToString() == digit.ToString(CultureInfo.InvariantCulture));

                (string Key, Func<KeybindConfig, KeyChord> Chord)[] keyed =
                [
                    ("Menu_PanelInspector", k => k.ToggleInspector),
                    ("Menu_Panel3DPreview", k => k.TogglePreview3D),
                    ("Menu_ShowWaveform", k => k.ToggleWaveform),
                    ("Menu_SnapSelected", k => k.SnapSelected),
                    ("Menu_SnapAll", k => k.SnapAll),
                    ("Menu_IncreaseLength", k => k.IncreaseLength),
                    ("Menu_DecreaseLength", k => k.DecreaseLength),
                    ("Menu_InsertBeatAtPlayhead", k => k.InsertBeatAtPlayhead),
                    ("Menu_RemoveBeatAtPlayhead", k => k.RemoveBeatAtPlayhead),
                    ("Menu_InsertVocalAtPlayhead", k => k.InsertVocalAtPlayhead),
                ];
                foreach (var (key, chord) in keyed)
                {
                    var row = Row(key);
                    Assert.True(row.DesiredSize is { Width: > 1, Height: > 1 }, $"the row {key} measured {row.DesiredSize}");
                    Assert.Equal(Gesture(chord(state.Keybinds)), row.InputGesture);
                }

                var bookmarkModifiers = state.Keybinds.BookmarkBase.Modifiers;
                for (var digit = 0; digit <= 9; digit++)
                {
                    Assert.Equal(new KeyGesture((Key)((int)Key.D0 + digit), bookmarkModifiers), Bookmark(digit).InputGesture);
                }

                // A rebind in Settings writes the property on the live table…
                var inspector = Row("Menu_PanelInspector");
                var before = state.Keybinds.ToggleInspector;
                state.Keybinds.ToggleInspector = new KeyChord(Key.F8, KeyModifiers.Shift);
                Assert.Equal(new KeyGesture(Key.F8, KeyModifiers.Shift), inspector.InputGesture);
                state.Keybinds.ToggleInspector = before;

                // …and Cancel / import REPLACE the table: the rows follow the new one.
                var replaced = new KeybindConfig
                {
                    ToggleWaveform = new KeyChord(Key.W, KeyModifiers.Control | KeyModifiers.Shift),
                    BookmarkBase = new KeyChord(Key.D0, KeyModifiers.Control | KeyModifiers.Shift),
                };
                state.Keybinds = replaced;
                Assert.Equal(new KeyGesture(Key.W, KeyModifiers.Control | KeyModifiers.Shift), Row("Menu_ShowWaveform").InputGesture);
                Assert.Equal(new KeyGesture(Key.D7, KeyModifiers.Control | KeyModifiers.Shift), Bookmark(7).InputGesture);

                // An UNBOUND chord shows nothing rather than a stale key.
                replaced.SnapAll = KeyChord.None;
                Assert.Null(Row("Menu_SnapAll").InputGesture);
            }
            finally
            {
                state.Keybinds = original;
                window.Close();
            }
        }, CancellationToken.None);

    [Theory]
    [InlineData("Main_ToggleInspector", "Toolbar_ToggleInspector_Hint", nameof(KeybindConfig.ToggleInspector))]
    [InlineData("Main_TogglePreview", "Toolbar_TogglePreview_Hint", nameof(KeybindConfig.TogglePreview3D))]
    [InlineData("Menu_ZoomIn", "Toolbar_ZoomIn_Hint", nameof(KeybindConfig.ZoomIn))]
    [InlineData("Menu_ZoomOut", "Toolbar_ZoomOut_Hint", nameof(KeybindConfig.ZoomOut))]
    [InlineData("Toolbar_Play", "Toolbar_Play_Hint", nameof(KeybindConfig.PlayPause))]
    [InlineData("Toolbar_Pause", "Toolbar_Pause_Hint", nameof(KeybindConfig.PlayPause))]
    [InlineData("Menu_SaveProject", "Toolbar_SaveProject_Hint", nameof(KeybindConfig.SaveProject))]
    public Task AToolbarTooltip_ShowsTheLiveChord(string nameKey, string hintKey, string chordProperty) =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var shell = Shell();
            var keybinds = shell.ProjectState.Keybinds;
            var property = typeof(KeybindConfig).GetProperty(chordProperty)!;
            var original = (KeyChord)property.GetValue(keybinds)!;

            var view = new ToolbarView { DataContext = shell };
            var window = new Window { Width = Slot.Width, Height = 80, Content = view };
            window.Show();
            try
            {
                view.Measure(new Size(Slot.Width, 80));
                view.Arrange(new Rect(0, 0, Slot.Width, 80));

                var name = LocalizationService.Instance[nameKey];
                var button = view.GetVisualDescendants().OfType<Button>()
                    .Single(b => AutomationProperties.GetName(b) == name);
                Assert.True(button.Bounds is { Width: > 1, Height: > 1 }, $"the {nameKey} button laid out as {button.Bounds}");

                string Expected(KeyChord chord) =>
                    string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance[hintKey], chord);

                Assert.Equal(Expected(original), ToolTip.GetTip(button));
                Assert.Contains(original.ToString(), ToolTip.GetTip(button) as string ?? "", StringComparison.Ordinal);

                var rebound = new KeyChord(Key.F8, KeyModifiers.Control | KeyModifiers.Shift);
                property.SetValue(keybinds, rebound);
                Assert.Equal(Expected(rebound), ToolTip.GetTip(button));
            }
            finally
            {
                property.SetValue(keybinds, original);
                window.Close();
            }
        }, CancellationToken.None);

    private static MainWindowViewModel Shell() =>
        (App.Services ?? throw new InvalidOperationException("the headless app session did not boot"))
        .GetRequiredService<MainWindowViewModel>();

    private static KeyGesture? Gesture(KeyChord chord) => chord.IsBound ? new KeyGesture(chord.Key, chord.Modifiers) : null;

    private static IEnumerable<MenuItem> Rows(IEnumerable<MenuItem> menus)
    {
        foreach (var menu in menus)
        {
            foreach (var row in menu.Items.OfType<MenuItem>())
            {
                yield return row;
                foreach (var nested in Rows([row])) yield return nested;
            }
        }
    }
}
