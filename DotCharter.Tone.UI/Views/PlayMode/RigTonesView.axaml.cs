using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using DotCharter.Tone.UI.ViewModels.PlayMode;

namespace DotCharter.Tone.UI.Views.PlayMode;

/// <summary>
///     The rig's TONES MODE: the live chain strip, the tone library (sidebar,
///     card grid, the TONE3000 capture panel) and the AI designer's column. A
///     view over <c>AmpFxRigViewModel</c>, hosted once by <see cref="AmpFxRigView" />
///     beside the canvas body (<c>x:Name="RigTonesMode"</c>). All behaviour is on
///     the view models; the code here keeps the designer column from starving
///     the card grid on a small window, and opens a tone card's menu.
/// </summary>
public partial class RigTonesView : UserControl
{
    /// <summary>Builds the view.</summary>
    public RigTonesView()
    {
        InitializeComponent();
        RigTonesColumns.SizeChanged += (_, e) => FitDesigner(e.NewSize.Width);
        // A right-click on a card — or the menu key / Shift+F10 on a focused one,
        // which Avalonia raises as the same event — opens that card's menu.
        RigBrowserCards.AddHandler(ContextRequestedEvent, OnCardContextRequested, RoutingStrategies.Bubble);
    }

    /// <summary>The tone-card menu last opened (one at a time), for the tests.</summary>
    internal ContextMenu? CardMenu { get; private set; }

    private void OnCardContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (e.Source is not Visual source) return;
        var cardButton = source as Button ?? source.FindAncestorOfType<Button>(includeSelf: true);
        if (cardButton?.DataContext is not PresetCardViewModel card
            || RigBrowserCards.DataContext is not PresetBrowserViewModel browser)
        {
            return;
        }

        CardMenu?.Close();
        CardMenu = RigToneCardContextMenu.Build(browser, card);
        // At the pointer for a click; under the card for the keyboard, where
        // there is no pointer position to open at.
        CardMenu.Placement = e.TryGetPosition(cardButton, out _)
            ? PlacementMode.Pointer
            : PlacementMode.BottomEdgeAlignedLeft;
        CardMenu.Open(cardButton);
        e.Handled = true;
    }

    /// <summary>Sizes the designer column for the width the body has: its
    /// breakpointed token, narrowed when the library could not otherwise keep a
    /// usable grid (<see cref="RigTonesSizing.DesignerWidth" />).</summary>
    private void FitDesigner(double available)
    {
        var preferred = this.TryFindResource("FireRigToneDesignerWidth", out var value) && value is double d
            ? d
            : RigTonesSizing.FallbackDesignerWidth;
        var width = RigTonesSizing.DesignerWidth(available, preferred);
        if (Math.Abs(RigToneDesignPanel.Width - width) > 0.5) RigToneDesignPanel.Width = width;
    }
}

/// <summary>The Tones view's one layout rule, pure so the default gate can pin it.</summary>
public static class RigTonesSizing
{
    /// <summary>The designer's token when the resource cannot be read (a host with
    /// no theme): the base ladder's value.</summary>
    public const double FallbackDesignerWidth = 460;

    /// <summary>The narrowest the designer gets: its composer, the answer card's
    /// chips and a two-word prompt still fit.</summary>
    public const double MinDesignerWidth = 340;

    /// <summary>The largest share of the body the designer may take, so the
    /// library keeps the rest — at 1280×800 that is the sidebar plus one whole
    /// card, where the token alone left the grid a card-and-a-bit too narrow and
    /// cut the only card in it.</summary>
    public const double MaxDesignerShare = 0.42;

    /// <summary>
    ///     The designer column's width for a body <paramref name="available" /> px
    ///     wide: the <paramref name="preferred" /> token, capped at
    ///     <see cref="MaxDesignerShare" /> of the body, floored at
    ///     <see cref="MinDesignerWidth" /> (never above the token itself).
    /// </summary>
    public static double DesignerWidth(double available, double preferred)
    {
        if (double.IsNaN(available) || available <= 0) return preferred;
        var floor = Math.Min(MinDesignerWidth, preferred);
        return Math.Clamp(Math.Round(available * MaxDesignerShare), floor, preferred);
    }
}
