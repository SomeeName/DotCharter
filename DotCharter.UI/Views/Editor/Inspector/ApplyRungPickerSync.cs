using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using DotCharter.UI.ViewModels.Panels;

namespace DotCharter.UI.Views.Editor.Inspector;

/// <summary>
///     Keeps a ComboBox of the "Apply to" ladder's rows (<see cref="ChordApplyLadder.Rows" />) on the live rung
///     (<see cref="ChordApplyLadder.Effective" />) and forwards a pick to the ladder's own verb
///     (<see cref="ChordTemplateEditorViewModel.ChooseApplyRungCommand" />). Shared by the footer's picker
///     (<see cref="SoundApplyBarView" />) and the Bends scope card's "Bend scope" (<see cref="SoundBendsScopeView" />), so
///     both show and set the SAME rung. The owning view hooks it while attached and unhooks it on detach.
/// </summary>
internal sealed class ApplyRungPickerSync
{
    private readonly ComboBox _picker;
    private ChordTemplateEditorViewModel? _editor;
    private bool _hooked;
    private bool _syncing;

    public ApplyRungPickerSync(ComboBox picker)
    {
        _picker = picker;
        _picker.SelectionChanged += OnRungPicked;
    }

    public ChordTemplateEditorViewModel? Editor => _editor;

    /// <summary>A new view model: listens to its ladder while <paramref name="attached" />, and shows its live rung.</summary>
    public void SetEditor(ChordTemplateEditorViewModel? editor, bool attached)
    {
        Unhook();
        _editor = editor;
        if (attached) Hook();
        Sync();
    }

    public void Hook()
    {
        if (_hooked || _editor is null) return;
        _editor.Ladder.PropertyChanged += OnLadderChanged;
        _hooked = true;
        Sync();
    }

    public void Unhook()
    {
        if (!_hooked || _editor is null) return;
        _editor.Ladder.PropertyChanged -= OnLadderChanged;
        _hooked = false;
    }

    private void OnLadderChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChordApplyLadder.Effective)) Sync();
    }

    /// <summary>The box shows the rung the next edit takes: the ladder's lit row.</summary>
    public void Sync()
    {
        if (_editor is null) return;
        _syncing = true;
        try
        {
            _picker.SelectedItem = _editor.Ladder.Rows.FirstOrDefault(r => r.Rung == _editor.Ladder.Effective);
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>
    ///     A pick: the SAME verb as a rung of the ladder (ChooseApplyRung), but only a USER'S pick, made in the open list or
    ///     with the keyboard on the focused box. The box's own selection model also moves its selection (re-attached to a
    ///     new window, its items re-enabled), and taking such a stray change as a pick would silently widen what the next
    ///     edit reaches; it is put back on the live rung instead.
    /// </summary>
    private void OnRungPicked(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _editor is null) return;
        if (!(_picker.IsDropDownOpen || _picker.IsKeyboardFocusWithin) || _picker.SelectedItem is not ChordApplyRungRow row)
        {
            Dispatcher.UIThread.Post(Sync);
            return;
        }

        _editor.ChooseApplyRungCommand.Execute(row.Rung);
        // A refused or no-op pick leaves Effective where it was: put the box back on it.
        Sync();
    }
}
