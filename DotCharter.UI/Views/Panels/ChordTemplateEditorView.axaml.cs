using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using DotCharter.Tone.UI.Controls;
using DotCharter.UI.Controls;
using DotCharter.UI.ViewModels.Panels;

namespace DotCharter.UI.Views.Panels;

public partial class ChordTemplateEditorView : UserControl
{
    /// <summary>
    ///     The string-edit mask the chord box draws on its rail.
    ///     <para>
    ///         It is passed IN rather than read off <c>ChordTemplateEditorViewModel</c>
    ///         because the mask is EDITOR state (<c>ProjectState.StringEditMask</c>,
    ///         also set by <c>Ctrl+1..9</c> and drawn on the lane headers), not
    ///         template state. Routing it through the host keeps the template
    ///         view model free of a second selection concept — which is the
    ///         whole point of the 0.36 unification.
    ///     </para>
    /// </summary>
    public static readonly StyledProperty<StringEditMask> MaskProperty =
        AvaloniaProperty.Register<ChordTemplateEditorView, StringEditMask>(nameof(Mask));

    public static readonly StyledProperty<ICommand?> ToggleMaskCommandProperty =
        AvaloniaProperty.Register<ChordTemplateEditorView, ICommand?>(nameof(ToggleMaskCommand));

    /// <summary>Validation pips for the chord box's rail. Passed in for the same
    /// reason as <see cref="Mask" />: findings are the INSPECTOR's read-model of
    /// the arrangement, not the template's.</summary>
    public static readonly StyledProperty<IReadOnlyList<ChordBoxIssuePip>?> IssueStringsProperty =
        AvaloniaProperty.Register<ChordTemplateEditorView, IReadOnlyList<ChordBoxIssuePip>?>(nameof(IssueStrings));

    /// <summary>
    ///     What a click on the chord box means here. <c>Shape</c> in every
    ///     selection state; <c>ReadOnly</c> in the Library block.
    ///     <para>
    ///         The read-only mode is what kills the <c>ChordTemplates.First()</c>
    ///         trap by construction. Browse mode loads a template with nothing
    ///         selected, and a shape edit there had no selection to scope it —
    ///         so the model refused it SILENTLY. Now the box cannot be reshaped
    ///         at all and the reason is stated on it; name, fingering and
    ///         arpeggio stay editable, which is exactly what browse mode always
    ///         permitted.
    ///     </para>
    /// </summary>
    public static readonly StyledProperty<ChordBoxRole> ShapeRoleProperty =
        AvaloniaProperty.Register<ChordTemplateEditorView, ChordBoxRole>(
            nameof(ShapeRole), ChordBoxRole.Shape);

    /// <summary>Whether this panel shows its own "Apply to" ladder. The Sound tab turns it off
    /// and pins a <see cref="ChordApplyScopeBar" /> above its segments instead.</summary>
    public static readonly StyledProperty<bool> ShowsApplyScopeProperty =
        AvaloniaProperty.Register<ChordTemplateEditorView, bool>(nameof(ShowsApplyScope), true);

    /// <summary>The chord box and the per-string fingering rows ABOVE the template and name
    /// pickers — the Sound tab's Shape segment, where fingering is the common edit.</summary>
    public static readonly StyledProperty<bool> FingeringFirstProperty =
        AvaloniaProperty.Register<ChordTemplateEditorView, bool>(nameof(FingeringFirst));

    private readonly EffectiveVisibilityWatcher _visibility;
    private ChordApplyPreview? _previewHost;

    public ChordTemplateEditorView()
    {
        InitializeComponent();
        // The bulk-edit preview shows only while a panel is on screen: the
        // rung is sticky, and marks for a panel nobody can see would be noise.
        _visibility = new EffectiveVisibilityWatcher(this, _ => ReportShown());
    }

    public bool ShowsApplyScope
    {
        get => GetValue(ShowsApplyScopeProperty);
        set => SetValue(ShowsApplyScopeProperty, value);
    }

    public bool FingeringFirst
    {
        get => GetValue(FingeringFirstProperty);
        set => SetValue(FingeringFirstProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != FingeringFirstProperty) return;
        var fingeringFirst = change.GetNewValue<bool>();
        if (this.FindControl<Control>("NamingBlock") is { } naming) Grid.SetRow(naming, fingeringFirst ? 1 : 0);
        if (this.FindControl<Control>("FingeringBlock") is { } fingering) Grid.SetRow(fingering, fingeringFirst ? 0 : 1);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        ReportShown();
    }

    /// <summary>Tells the preview of the view model this view shows whether the
    /// view is effectively visible — and tells the previous one that it is not.</summary>
    private void ReportShown()
    {
        var preview = (DataContext as ChordTemplateEditorViewModel)?.Preview;
        if (!ReferenceEquals(preview, _previewHost)) _previewHost?.SetHostShown(this, false);
        _previewHost = preview;
        preview?.SetHostShown(this, _visibility.IsEffectivelyVisible);
    }

    public StringEditMask Mask
    {
        get => GetValue(MaskProperty);
        set => SetValue(MaskProperty, value);
    }

    public ICommand? ToggleMaskCommand
    {
        get => GetValue(ToggleMaskCommandProperty);
        set => SetValue(ToggleMaskCommandProperty, value);
    }

    public IReadOnlyList<ChordBoxIssuePip>? IssueStrings
    {
        get => GetValue(IssueStringsProperty);
        set => SetValue(IssueStringsProperty, value);
    }

    public ChordBoxRole ShapeRole
    {
        get => GetValue(ShapeRoleProperty);
        set => SetValue(ShapeRoleProperty, value);
    }
}
