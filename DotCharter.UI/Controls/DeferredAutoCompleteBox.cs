using System.Collections;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Data;

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

    private AutoCompleteBox? _editor;

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
            Text = Text
        };
        box.Bind(AutoCompleteBox.FontFamilyProperty, this.GetObservable(FontFamilyProperty));
        AutomationProperties.SetName(box, AutomationProperties.GetName(this));
        box.PropertyChanged += OnEditorPropertyChanged;
        _editor = box;

        ShowEditor(box);
        box.ApplyTemplate();
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
        else if (change.Property == ItemsSourceProperty && _editor is not null)
        {
            _editor.ItemsSource = change.GetNewValue<IEnumerable?>();
        }
    }

    private void OnEditorPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != AutoCompleteBox.TextProperty) return;
        var text = e.GetNewValue<string?>();
        if (Text != text) SetCurrentValue(TextProperty, text);
    }
}
