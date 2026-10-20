using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;

namespace DotCharter.UI.Views.Editor.Inspector;

/// <summary>
/// The icon-only section rail of an Inspector tab that has several sections: Mixer,
///     Song and Lyrics wear the same 38 px rail the Sound tab does, one section on screen at a time.
///     <para>
///         The rail is a panel of ENTRIES, each a <see cref="Panel" /> holding a
///         <see cref="ToggleButton" /> whose <c>Tag</c> is the <c>x:Name</c> of the section body it
///         opens. The body and the entry's accent bar bind their <c>IsVisible</c> to the toggle's
///         <c>IsChecked</c> in markup, so this class only decides WHICH toggle is lit: exactly one,
///         the chosen section's. An entry that is hidden (its section does not apply, e.g. Stems
///         without stems) is never the open one: the open section falls back to the first entry
///         shown. The choice lives as long as the view (a session), like the Sound tab's.
///     </para>
///     <para>
///         The Sound tab's sections are a function of the SELECTION, so its rail is a view model
///         (<c>SoundSectionsViewModel</c>); these tabs' sections are fixed, so the rail is pure
///         layout and stays in the view. The section bodies are listed in
///         <c>InspectorView.NestedPaneNames</c> so the prewarm realizes the hidden ones.
///     </para>
/// </summary>
internal sealed class InspectorSectionRail
{
    private readonly Control _owner;
    private readonly List<(Panel Entry, ToggleButton Toggle, string Body)> _entries = [];
    private string? _chosen;

    public InspectorSectionRail(Control owner, Panel entries)
    {
        _owner = owner;
        foreach (var entry in entries.Children.OfType<Panel>())
        {
            if (entry.Children.OfType<ToggleButton>().FirstOrDefault() is not { Tag: string body } toggle) continue;
            _entries.Add((entry, toggle, body));
            toggle.Click += OnEntryClick;
            entry.PropertyChanged += OnEntryPropertyChanged;
        }

        Apply();
    }

    /// <summary>The <c>x:Name</c> of the section body on screen.</summary>
    public string Chosen { get; private set; } = string.Empty;

    /// <summary>Every section body this rail switches, in rail order.</summary>
    public IReadOnlyList<Control> Bodies =>
        [.. _entries.Select(e => _owner.FindControl<Control>(e.Body)).OfType<Control>()];

    /// <summary>Opens a section — what a click on its entry does.</summary>
    public void Choose(string body)
    {
        _chosen = body;
        Apply();
    }

    private void OnEntryClick(object? sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string body }) Choose(body);
    }

    private void OnEntryPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Visual.IsVisibleProperty) Apply();
    }

    /// <summary>Lights exactly the open section's toggle — a click on the lit one unchecked it
    /// locally, so every toggle is written, not only the ones that changed.</summary>
    private void Apply()
    {
        var shown = _entries.Where(e => e.Entry.IsVisible).Select(e => e.Body).ToList();
        Chosen = _chosen is { } chosen && shown.Contains(chosen) ? chosen : shown.FirstOrDefault() ?? string.Empty;
        foreach (var (_, toggle, body) in _entries) toggle.IsChecked = body == Chosen;
    }
}
