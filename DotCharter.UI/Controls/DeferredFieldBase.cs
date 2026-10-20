using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace DotCharter.UI.Controls;

/// <summary>
/// The resting half of a field that is not built until it is used.
///     <para>
///         At rest it is a bordered <see cref="TextBlock" /> drawn from the same
///         Fluent resource keys as a <see cref="TextBox" />'s own chrome, so it reads
///         as the field it stands for. It is a tab stop and a pointer target; the
///         first focus, from either, builds the real editor in its place, hands the
///         focus to the editor's <see cref="TextBox" /> and steps out of the tab
///         order, so Tab, Shift+Tab and a click land exactly where they did before.
///         Until then its own automation peer is an Edit with a value pattern.
///     </para>
///     <para>
///         A derived control supplies the editor (<see cref="RealizeEditor" />), the
///         text the resting field shows (<see cref="RestingText" />) and the value
///         the peer reports. <see cref="DeferredAutoCompleteBox" /> edits a string,
///         <see cref="DeferredChoiceBox" /> picks one object out of a long list.
///     </para>
/// </summary>
public abstract class DeferredFieldBase : Decorator
{
    /// <summary>The font of the resting text and of the realized editor.</summary>
    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        TextElement.FontFamilyProperty.AddOwner<DeferredFieldBase>();

    // Styles/Inputs.axaml gives every TextBox a 32 px floor, and the editor's
    // TextBox is what sets its height; the resting field keeps the same one, so
    // realizing an editor never moves the card around it.
    private const double FieldMinHeight = 32;

    private readonly Border _chrome;
    private readonly TextBlock _label;
    private readonly List<IDisposable> _chromeState = [];

    static DeferredFieldBase() => FocusableProperty.OverrideDefaultValue<DeferredFieldBase>(true);

    protected DeferredFieldBase()
    {
        _label = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        _chrome = new Border { BorderThickness = new Thickness(1), MinHeight = FieldMinHeight, Child = _label };
        _chrome.Bind(Border.PaddingProperty, _chrome.GetResourceObservable("TextControlThemePadding"));
        _chrome.Bind(Border.CornerRadiusProperty, _chrome.GetResourceObservable("ControlCornerRadius"));
        _label.Bind(TextBlock.FontSizeProperty, _label.GetResourceObservable("ControlContentThemeFontSize"));
        _label.Bind(TextBlock.FontFamilyProperty, this.GetObservable(FontFamilyProperty));
        ShowChromeState(pointerOver: false);
        Child = _chrome;
    }

    public FontFamily FontFamily
    {
        get => GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    /// <summary>True once the real editor stands in the resting field's place.</summary>
    protected abstract bool IsRealized { get; }

    /// <summary>What the peer reports as the field's value.</summary>
    protected abstract string? FieldValue { get; }

    /// <summary>The text the resting field draws.</summary>
    protected string? RestingText
    {
        get => _label.Text;
        set => _label.Text = value;
    }

    /// <summary>Builds the real editor in place (once) and returns it; the derived
    /// type puts it in with <see cref="ShowEditor" />.</summary>
    protected abstract Control RealizeEditor();

    /// <summary>The peer's SetValue.</summary>
    protected abstract void SetFieldValue(string? value);

    /// <summary>Takes the resting field's place with the editor.</summary>
    protected void ShowEditor(Control editor)
    {
        foreach (var state in _chromeState) state.Dispose();
        _chromeState.Clear();
        Child = editor;
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        if (IsRealized) return;

        var editor = RealizeEditor();
        // The editor's TextBox is the control that takes text; focusing it is what
        // the editor does itself when it is focused, done here directly because it
        // has only just been templated.
        InputElement target = editor.GetVisualDescendants().OfType<TextBox>().FirstOrDefault() is { } input ? input : editor;
        target.Focus(e.NavigationMethod, e.KeyModifiers);
        // Out of the tab order only AFTER the focus moved on: the editor now sits in
        // this control's place, so Tab and Shift+Tab reach it instead.
        SetCurrentValue(FocusableProperty, false);
        OnEditorFocused(editor, e.NavigationMethod);
    }

    /// <summary>Called once, right after the first focus built the editor and moved
    /// the focus into it.</summary>
    protected virtual void OnEditorFocused(Control editor, NavigationMethod method)
    {
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        if (!IsRealized) ShowChromeState(pointerOver: true);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (!IsRealized) ShowChromeState(pointerOver: false);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new DeferredFieldAutomationPeer(this);

    /// <summary>The Fluent TextBox's rest and hover brushes (FluentOverrides.axaml
    /// re-colours the keys), the same ones the realized editor draws with.</summary>
    private void ShowChromeState(bool pointerOver)
    {
        foreach (var state in _chromeState) state.Dispose();
        _chromeState.Clear();
        var suffix = pointerOver ? "PointerOver" : "";
        _chromeState.Add(_chrome.Bind(Border.BackgroundProperty, _chrome.GetResourceObservable("TextControlBackground" + suffix)));
        _chromeState.Add(_chrome.Bind(Border.BorderBrushProperty, _chrome.GetResourceObservable("TextControlBorderBrush" + suffix)));
        _chromeState.Add(_label.Bind(TextBlock.ForegroundProperty, _label.GetResourceObservable("TextControlForeground" + suffix)));
    }

    /// <summary>At rest the field is an Edit with a value, named by the
    /// <c>AutomationProperties.Name</c> it carries; once the real editor exists, the
    /// editor's own peer is the field and this one steps aside.</summary>
    private sealed class DeferredFieldAutomationPeer(DeferredFieldBase owner)
        : ControlAutomationPeer(owner), IValueProvider
    {
        private new DeferredFieldBase Owner => (DeferredFieldBase)base.Owner;

        public bool IsReadOnly => !Owner.IsEffectivelyEnabled;

        public string? Value => Owner.FieldValue;

        public void SetValue(string? value) => Owner.SetFieldValue(value);

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Edit;

        protected override string GetClassNameCore() => nameof(AutoCompleteBox);

        protected override bool IsControlElementCore() => !Owner.IsRealized;

        protected override bool IsContentElementCore() => !Owner.IsRealized;
    }
}
