using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using DotCharter.Tone.UI.ViewModels.PlayMode;

namespace DotCharter.Tone.UI.Views.PlayMode;

/// <summary>
///     The four performance macros in the module editor's side rack.
///     <para>
///         The only thing this code-behind does is fold the four macros'
///         <see cref="RigMacroViewModel.CanControl" /> flags into ONE flag the
///         markup can gate the card's empty state on — Avalonia has no OR in a
///         binding, and a card that draws four greyed dials plus a sentence
///         explaining that they do nothing is the shape W4 removed.
///     </para>
///     <para>
///         It lives in the VIEW rather than on <c>RigMacroBankViewModel</c>
///         deliberately: "is this card in its empty state" is presentation, and
///         it is what keeps the fold beside the markup that reads it.
///     </para>
/// </summary>
public partial class RigPerformanceControlsView : UserControl
{
    /// <summary>True while at least one macro has a parameter assigned to it,
    /// i.e. while there is a dial worth drawing.</summary>
    public static readonly StyledProperty<bool> HasAnyAssignmentProperty =
        AvaloniaProperty.Register<RigPerformanceControlsView, bool>(nameof(HasAnyAssignment));

    /// <inheritdoc cref="HasAnyAssignmentProperty" />
    public bool HasAnyAssignment
    {
        get => GetValue(HasAnyAssignmentProperty);
        private set => SetValue(HasAnyAssignmentProperty, value);
    }

    private RigMacroBankViewModel? _bank;

    /// <summary>Builds the view.</summary>
    public RigPerformanceControlsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as RigMacroBankViewModel);
        // The rack view is rebuilt on float/redock and on every selection
        // change, so an un-detached subscription would keep a retired bank
        // alive — the rule ViewSubscriptionLeakGuardTests exists for.
        DetachedFromVisualTree += (_, _) => Attach(null);
    }

    private void Attach(RigMacroBankViewModel? bank)
    {
        if (ReferenceEquals(_bank, bank)) return;

        if (_bank is not null)
        {
            foreach (var m in _bank.Macros)
                m.PropertyChanged -= OnMacroChanged;
        }

        _bank = bank;

        if (_bank is not null)
        {
            foreach (var m in _bank.Macros)
                m.PropertyChanged += OnMacroChanged;
        }

        Refresh();
    }

    private void OnMacroChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RigMacroViewModel.CanControl) or null) Refresh();
    }

    private void Refresh() =>
        HasAnyAssignment = _bank is not null && _bank.Macros.Any(m => m.CanControl);
}
