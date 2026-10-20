using System.Collections;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using DotCharter.Presentation.Contracts.Dialogs;

namespace DotCharter.UI.Controls;

/// <summary>
/// A searchable picker for one <see cref="IToneChoice" /> out of a long list, built on first use.
///     <para>
///         The Tone designer's gear slots hold the whole catalogue (every amp, pedal,
///         cabinet and rack unit); finding one by scrolling a <see cref="ComboBox" />
///         is slow. Here the field is a text box: type to filter (a case-insensitive
///         substring of the display name or the <see cref="ISearchableChoice.SearchKey" />),
///         Up/Down to move through the matches, Enter to commit. Escape, and
///         leaving the field, keep the old choice and put its name back.
///     </para>
///     <para>
///         At rest it is the bordered field of <see cref="DeferredFieldBase" /> showing
///         the picked name; the first focus builds the <see cref="AutoCompleteBox" />
///         (a templated <see cref="TextBox" /> and a popup, ~10 ms in Debug), so an open
///         of the designer, which rebuilds every gear row, builds none.
///     </para>
/// </summary>
public sealed class DeferredChoiceBox : DeferredFieldBase
{
    /// <summary>The choices on offer.</summary>
    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<DeferredChoiceBox, IEnumerable?>(nameof(ItemsSource));

    /// <summary>The picked choice, two-way by default like a <see cref="ComboBox" />'s.</summary>
    public static readonly StyledProperty<IToneChoice?> SelectedItemProperty =
        AvaloniaProperty.Register<DeferredChoiceBox, IToneChoice?>(nameof(SelectedItem), defaultBindingMode: BindingMode.TwoWay);

    private static readonly IDataTemplate ItemTemplate =
        new FuncDataTemplate<IToneChoice>((choice, _) => new TextBlock { Text = choice?.DisplayName });

    private AutoCompleteBox? _editor;
    private bool _syncing;

    public DeferredChoiceBox() => AddHandler(KeyDownEvent, OnEditorKeyDown, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);

    public IEnumerable? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public IToneChoice? SelectedItem
    {
        get => GetValue(SelectedItemProperty);
        set => SetValue(SelectedItemProperty, value);
    }

    /// <summary>The real box once something focused this field; null at rest.</summary>
    internal AutoCompleteBox? Editor => _editor;

    protected override bool IsRealized => _editor is not null;

    protected override string? FieldValue => SelectedItem?.DisplayName;

    /// <summary>Whether <paramref name="choice" /> belongs in the list for the typed
    /// <paramref name="search" />: a case-insensitive substring of its name or key.
    /// Text equal to the picked name is not a search yet (the list opens whole).</summary>
    internal static bool Matches(IToneChoice choice, string? search, IToneChoice? picked)
    {
        var needle = search?.Trim();
        if (string.IsNullOrEmpty(needle)) return true;
        if (picked is not null && string.Equals(search, picked.DisplayName, StringComparison.Ordinal)) return true;
        return choice.DisplayName.Contains(needle, StringComparison.OrdinalIgnoreCase)
               || (choice is ISearchableChoice { SearchKey: var key } && key.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Builds the real box in place (once) and returns it. The focus path
    /// calls it; tests call it to drive the box without a focus manager.</summary>
    internal AutoCompleteBox Realize()
    {
        if (_editor is not null) return _editor;

        var box = new AutoCompleteBox
        {
            MinimumPrefixLength = 0,
            IsTextCompletionEnabled = false,
            ItemsSource = ItemsSource,
            ItemTemplate = ItemTemplate,
            ItemFilter = (search, item) => item is IToneChoice choice && Matches(choice, search, SelectedItem),
            ItemSelector = (_, item) => (item as IToneChoice)?.DisplayName ?? "",
            Text = SelectedItem?.DisplayName
        };
        box.Bind(AutoCompleteBox.FontFamilyProperty, this.GetObservable(FontFamilyProperty));
        AutomationProperties.SetName(box, AutomationProperties.GetName(this));
        box.DropDownClosed += OnEditorDropDownClosed;
        box.LostFocus += OnEditorLostFocus;
        _editor = box;

        ShowEditor(box);
        box.ApplyTemplate();
        // Set after the template so the box's own change handling has a TextBox to talk to.
        _syncing = true;
        try
        {
            box.SelectedItem = SelectedItem;
        }
        finally
        {
            _syncing = false;
        }

        return box;
    }

    protected override Control RealizeEditor() => Realize();

    protected override void SetFieldValue(string? value)
    {
        var match = Items().FirstOrDefault(c => string.Equals(c.DisplayName, value, StringComparison.OrdinalIgnoreCase));
        if (match is not null) SetCurrentValue(SelectedItemProperty, match);
    }

    protected override void OnEditorFocused(Control editor, NavigationMethod method)
    {
        // Typing replaces the picked name; a click also shows the list, whole.
        if (editor.GetVisualDescendants().OfType<TextBox>().FirstOrDefault() is { } input) input.SelectAll();
        if (method == NavigationMethod.Pointer && editor is AutoCompleteBox box) box.IsDropDownOpen = true;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SelectedItemProperty)
        {
            RestingText = change.GetNewValue<IToneChoice?>()?.DisplayName;
            ShowPickedInEditor();
        }
        else if (change.Property == ItemsSourceProperty && _editor is not null)
        {
            _editor.ItemsSource = change.GetNewValue<IEnumerable?>();
        }
    }

    private IEnumerable<IToneChoice> Items() => ItemsSource?.OfType<IToneChoice>() ?? [];

    /// <summary>Puts the picked name (and the pick itself) back in the box.</summary>
    private void ShowPickedInEditor()
    {
        if (_editor is not { } box) return;
        _syncing = true;
        try
        {
            var name = SelectedItem?.DisplayName ?? "";
            if (!ReferenceEquals(box.SelectedItem, SelectedItem)) box.SelectedItem = SelectedItem;
            if (box.Text != name) box.Text = name;
        }
        finally
        {
            _syncing = false;
        }
    }

    private void Commit(IToneChoice choice)
    {
        if (!Equals(SelectedItem, choice)) SetCurrentValue(SelectedItemProperty, choice);
        ShowPickedInEditor();
    }

    /// <summary>The box picks as you type (an exact name selects at once), so a selection alone
    /// is not a commit. A commit is the list closing on a pick while the caret is still in the
    /// field (Enter, or a click on an entry); Escape and leaving the field close it too, and
    /// those keep the old choice.</summary>
    private void OnEditorDropDownClosed(object? sender, EventArgs e)
    {
        if (_syncing || _editor is not { IsKeyboardFocusWithin: true } box) return;
        if (box.SelectedItem is IToneChoice choice && box.Text == choice.DisplayName) Commit(choice);
    }

    private void OnEditorLostFocus(object? sender, RoutedEventArgs e)
    {
        // Typed text that was never committed does not stay: the field shows what is picked.
        if (_editor is { IsKeyboardFocusWithin: false } box) Cancel(box);
    }

    /// <summary>Closes the list and puts the picked name back, committing nothing.</summary>
    private void Cancel(AutoCompleteBox box)
    {
        _syncing = true;
        try
        {
            box.IsDropDownOpen = false;
        }
        finally
        {
            _syncing = false;
        }

        ShowPickedInEditor();
    }

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (_editor is not { } box) return;
        // The box has seen the key first on the way back up (bubble); the tunnel pass is
        // for the keys that must never reach it or the dialog around it.
        var tunnel = e.Route == RoutingStrategies.Tunnel;
        switch (e.Key)
        {
            case Key.Escape when tunnel && (box.IsDropDownOpen || box.Text != SelectedItem?.DisplayName):
                Cancel(box);
                e.Handled = true;
                break;
            case Key.Down when tunnel && !box.IsDropDownOpen:
                box.IsDropDownOpen = true;
                e.Handled = true;
                break;
            case Key.Enter when !tunnel:
                CommitTyped(box);
                break;
        }
    }

    /// <summary>Enter with the list open but nothing highlighted: take the first match
    /// (an exact name first) rather than dropping what was typed.</summary>
    private void CommitTyped(AutoCompleteBox box)
    {
        var text = box.Text;
        if (string.IsNullOrWhiteSpace(text) || text == SelectedItem?.DisplayName) return;
        var matches = Items().Where(c => Matches(c, text, null)).ToList();
        var pick = matches.FirstOrDefault(c => string.Equals(c.DisplayName, text.Trim(), StringComparison.OrdinalIgnoreCase))
                   ?? matches.FirstOrDefault();
        Cancel(box);
        if (pick is not null) Commit(pick);
    }
}
