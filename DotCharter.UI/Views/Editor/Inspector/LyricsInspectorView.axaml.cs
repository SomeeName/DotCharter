using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using DotCharter.UI.Controls;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.UI.Views.Editor.Inspector;

/// <summary>
///     The Lyrics tab in inspector design D: the review list, the selected syllable, the raw text and the files
///     as ColumnFlow cards over a pinned status and Apply footer. A press on the row that is already picked seeks to
///     its syllable again (picking another row seeks through the selection; the same row raises no selection change).
/// </summary>
public partial class LyricsInspectorView : UserControl
{
    public LyricsInspectorView()
    {
        InitializeComponent();
        Cards = this.FindControl<ColumnFlowPanel>("LyricsCards")!;
        this.FindControl<TextBox>("LyricsSelectedTextBox")!.LostFocus += (_, _) =>
            (DataContext as LyricsInspectorViewModel)?.CommitSelectedLyricText();
        // Tunnel: seen BEFORE the list moves its selection, so only a press on the row picked already re-seeks.
        this.FindControl<ListBox>("LyricsList")!.AddHandler(PointerPressedEvent, OnListPressed, RoutingStrategies.Tunnel);
    }

    /// <summary>The card body — the test hook.</summary>
    internal ColumnFlowPanel Cards { get; }

    /// <summary>Brings the named card into view (pages to it when the pane is narrow).</summary>
    internal void ShowCard(string name) => Cards.ShowPage(this.FindControl<Control>(name)!);

    private void OnListPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not LyricsInspectorViewModel vm || e.Source is not Visual source) return;
        var item = source.FindAncestorOfType<ListBoxItem>(includeSelf: true);
        if (item?.DataContext is LyricSyllableRow row && ReferenceEquals(row, vm.Syllables.SelectedRow)) vm.Syllables.SeekSelected();
    }
}
