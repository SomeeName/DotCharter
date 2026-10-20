using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using DotCharter.Tone.UI.Controls;
using DotCharter.UI.ViewModels.Panels;

namespace DotCharter.UI.Views.Editor.Inspector;

/// <summary>
/// The Sound tab's pinned "Apply to" picker. The code-behind keeps the box on the
///     ladder's live rung (<see cref="ChordApplyLadder.Effective" />), forwards a pick to the
///     ladder's own verb, and — like <c>ChordApplyScopeBar</c>, whose job it took on the Sound tab
/// — reports itself as a host of the bulk-edit preview: pinned over the Technique and
///     Bends sections it is on screen while the chord panel is not, and the marks must show for the
/// technique, bend and keyboard edits it governs there (a technique KEY follows the rung
///     only while a host is on screen, <see cref="ChordApplyPreview.IsHostShown" />).
/// </summary>
public partial class SoundApplyBarView : UserControl
{
    private readonly EffectiveVisibilityWatcher _visibility;
    private ChordTemplateEditorViewModel? _editor;
    private ChordApplyPreview? _previewHost;
    private bool _hooked;
    private bool _syncing;

    public SoundApplyBarView()
    {
        InitializeComponent();
        _visibility = new EffectiveVisibilityWatcher(this, _ => ReportShown());
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Unhook();
        _editor = DataContext as ChordTemplateEditorViewModel;
        if (VisualRoot != null) Hook();
        SyncPicker();
        ReportShown();
    }

    // The ladder is a singleton's: listen only while this view is on screen (a dock pane is
    // re-created on float/redock).
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Hook();
        SyncPicker();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Unhook();
    }

    private void Hook()
    {
        if (_hooked || _editor is null) return;
        _editor.Ladder.PropertyChanged += OnLadderChanged;
        _hooked = true;
    }

    private void Unhook()
    {
        if (!_hooked || _editor is null) return;
        _editor.Ladder.PropertyChanged -= OnLadderChanged;
        _hooked = false;
    }

    /// <summary>Tells the preview of the view model this row shows whether the row is
    /// effectively visible — and tells the previous one that it is not.</summary>
    private void ReportShown()
    {
        var preview = _editor?.Preview;
        if (!ReferenceEquals(preview, _previewHost)) _previewHost?.SetHostShown(this, false);
        _previewHost = preview;
        preview?.SetHostShown(this, _visibility.IsEffectivelyVisible);
    }

    private void OnLadderChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChordApplyLadder.Effective)) SyncPicker();
    }

    /// <summary>The box shows the rung the next edit takes — the ladder's lit row.</summary>
    private void SyncPicker()
    {
        if (_editor is null || this.FindControl<ComboBox>("SoundApplyPicker") is not { } picker) return;
        _syncing = true;
        try
        {
            picker.SelectedItem = _editor.Ladder.Rows.FirstOrDefault(r => r.Rung == _editor.Ladder.Effective);
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>
    ///     A pick: the SAME verb as a rung of the ladder (ChooseApplyRung) — but only a USER'S
    ///     pick, made in the open list or with the keyboard on the focused box. The box's own
    ///     selection model also moves its selection (re-attached to a new window, its items
    ///     re-enabled), and taking such a stray change as a pick would silently widen what the
    ///     next edit reaches; it is put back on the live rung instead.
    /// </summary>
    private void OnRungPicked(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _editor is null || sender is not ComboBox picker) return;
        if (!(picker.IsDropDownOpen || picker.IsKeyboardFocusWithin) || picker.SelectedItem is not ChordApplyRungRow row)
        {
            Dispatcher.UIThread.Post(SyncPicker);
            return;
        }

        _editor.ChooseApplyRungCommand.Execute(row.Rung);
        // A refused or no-op pick leaves Effective where it was: put the box back on it.
        SyncPicker();
    }
}
