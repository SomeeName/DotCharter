using System.Collections;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Threading;

namespace DotCharter.UI.Controls;

/// <summary>
/// An <see cref="AutoCompleteBox" /> that is not built until it is used.
///     <para>
///         At rest it is the bordered field of <see cref="DeferredFieldBase" />; the
///         first focus builds the real box in its place.
///     </para>
///     <para>
///         Why: one <see cref="AutoCompleteBox" /> is a templated <see cref="TextBox" />
///         plus a popup, ~10 ms to build and style in Debug, and the Tone designer's
///         arrangement cards had five per card, all rebuilt on every open because the
///         dialog's view model is new each time. An open now builds none of them.
///     </para>
///     <para>
///         <see cref="Text" /> binds two-way and is kept in step with the realized
///         box in both directions; <see cref="ItemsSource" /> is handed over when the
///         box is built and follows later changes. <c>AutomationProperties.Name</c> is
///         copied onto the box, and until then this control's own peer is an Edit
///         with a value pattern, so a screen reader meets the same named field.
///     </para>
/// </summary>
public sealed class DeferredAutoCompleteBox : DeferredFieldBase
{
    /// <summary>The text, two-way by default like <see cref="AutoCompleteBox.Text" />.</summary>
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<DeferredAutoCompleteBox, string?>(nameof(Text), defaultBindingMode: BindingMode.TwoWay);

    /// <summary>The completion suggestions.</summary>
    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<DeferredAutoCompleteBox, IEnumerable?>(nameof(ItemsSource));

    /// <summary>The hint shown while the field is empty, resting or realized.</summary>
    public static readonly StyledProperty<string?> PlaceholderTextProperty =
        AvaloniaProperty.Register<DeferredAutoCompleteBox, string?>(nameof(PlaceholderText));

    private AutoCompleteBox? _editor;

    public string? PlaceholderText
    {
        get => GetValue(PlaceholderTextProperty);
        set => SetValue(PlaceholderTextProperty, value);
    }

    /// <summary>
    /// Whether the suggestion list is worth opening for <paramref name="text" />: it is not when the
    /// only entry that contains the text IS the text, since the row would repeat what is typed.
    /// </summary>
    public static bool OffersSuggestions(string? text, IEnumerable? items)
    {
        if (items is null) return false;
        var matches = 0;
        string? only = null;
        foreach (var item in items)
        {
            var name = item?.ToString();
            if (name is null || !name.Contains(text ?? "", StringComparison.OrdinalIgnoreCase)) continue;
            if (++matches > 1) return true;
            only = name;
        }

        return matches == 1 && !string.Equals(only, text, StringComparison.Ordinal);
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public IEnumerable? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    /// <summary>The real box once something focused this field; null at rest.</summary>
    internal AutoCompleteBox? Editor => _editor;

    protected override bool IsRealized => _editor is not null;

    protected override string? FieldValue => Text;

    /// <summary>Builds the real box in place (once) and returns it. The focus path
    /// calls it; tests call it to drive the box without a focus manager.</summary>
    internal AutoCompleteBox Realize()
    {
        if (_editor is not null) return _editor;

        var box = new AutoCompleteBox
        {
            FilterMode = AutoCompleteFilterMode.Contains,
            MinimumPrefixLength = 0,
            ItemsSource = ItemsSource,
            Text = Text,
            PlaceholderText = PlaceholderText
        };
        box.DropDownOpening += OnEditorDropDownOpening;
        box.Bind(AutoCompleteBox.FontFamilyProperty, this.GetObservable(FontFamilyProperty));
        AutomationProperties.SetName(box, AutomationProperties.GetName(this));
        box.PropertyChanged += OnEditorPropertyChanged;
        _editor = box;

        ShowEditor(box);
        box.ApplyTemplate();
        GearPickerParts.StylePopup(box, this);
        return box;
    }

    protected override Control RealizeEditor() => Realize();

    protected override void SetFieldValue(string? value) => SetCurrentValue(TextProperty, value);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty)
        {
            var text = change.GetNewValue<string?>();
            RestingText = text;
            if (_editor is not null && _editor.Text != text) _editor.Text = text;
        }
        else if (change.Property == PlaceholderTextProperty)
        {
            var hint = change.GetNewValue<string?>();
            RestingPlaceholder = hint;
            _editor?.PlaceholderText = hint;
        }
        else if (change.Property == ItemsSourceProperty && _editor is not null)
        {
            _editor.ItemsSource = change.GetNewValue<IEnumerable?>();
        }
    }

    private void OnEditorDropDownOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (sender is not AutoCompleteBox box || OffersSuggestions(box.Text, box.ItemsSource)) return;
        e.Cancel = true;
        CloseList(box);
    }

    private void OnEditorPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != AutoCompleteBox.TextProperty) return;
        var text = e.GetNewValue<string?>();
        if (Text != text) SetCurrentValue(TextProperty, text);
        // The list opens once per typing run, so a key typed to the end of its only match has to
        // close the one already showing; after the box's own handling of the change.
        if (sender is AutoCompleteBox box && !OffersSuggestions(text, box.ItemsSource))
            Dispatcher.UIThread.Post(() => CloseList(box), DispatcherPriority.Loaded);
    }

    /// <summary>Closes the list for good: the box's own flag can read closed while its popup still shows.</summary>
    private static void CloseList(AutoCompleteBox box)
    {
        box.IsDropDownOpen = false;
        if (box.GetTemplateDescendants().OfType<Popup>().FirstOrDefault() is { IsOpen: true } popup) popup.IsOpen = false;
    }
}
