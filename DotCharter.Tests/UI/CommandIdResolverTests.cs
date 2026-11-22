using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using DotCharter.UI.Diagnostics;

namespace DotCharter.Tests.UI;

/// <summary>
///     The id a click is counted under. It has to be STABLE: counts keyed on a
///     visible label would split across UI languages and reset whenever someone
///     rewords a menu entry — precisely when the history is most valuable.
/// </summary>
public sealed class CommandIdResolverTests
{
    private void GenerateFretHandPositions() { }

    private Task RunLongThingAsync() => Task.CompletedTask;

    private bool CanGenerateFretHandPositions() => true;

    private void XamlIlPopulateTrampoline() { }

    /// <summary>Stands in for a bound Button's own handler of the command's event.</summary>
    private void CanExecuteChanged(object? sender, EventArgs e) { }

    /// <summary>
    ///     A <c>[RelayCommand]</c> wraps the method it was generated from, so the
    ///     method name is the natural identity — and it is the SAME convention
    ///     <c>ChartKeybindHandler</c> uses for shortcuts, which is what puts a verb
    ///     reached by key and by menu into one bucket instead of two half-used ones.
    /// </summary>
    [Fact]
    public void ACommandIsIdentifiedByTheMethodItWasGeneratedFrom()
    {
        var command = new RelayCommand(GenerateFretHandPositions);

        Assert.Equal("GenerateFretHandPositions", CommandIdResolver.Resolve(command, null, null));
    }

    [Fact]
    public void AnAsyncCommandResolvesToo()
    {
        var command = new AsyncRelayCommand(RunLongThingAsync);

        Assert.Equal("RunLongThingAsync", CommandIdResolver.Resolve(command, null, null));
    }

    /// <summary>
    ///     A lambda compiles to "&lt;.ctor&gt;b__12_3" — no more meaningful than no
    ///     answer, and it churns whenever the file above it is edited.
    /// </summary>
    [Fact]
    public void ALambdaCommandFallsBackRatherThanRecordingACompilerName()
    {
        var command = new RelayCommand(() => { });

        var id = CommandIdResolver.Resolve(command, "ExportButton", null);

        Assert.Equal("ExportButton", id);
    }

    [Fact]
    public void TheControlNameIsPreferredOverTheVisibleLabel() => Assert.Equal("SaveButton", CommandIdResolver.Resolve(null, "SaveButton", "Save project"));

    /// <summary>Localized, so it is the last resort and marked as a label.</summary>
    [Fact]
    public void APlainHeaderIsTheLastResort() => Assert.Equal("label:Save project", CommandIdResolver.Resolve(null, null, "Save project"));

    /// <summary>Avalonia's access-key marker is not part of the identity.</summary>
    [Fact]
    public void AccessKeyUnderscoresAreStripped() => Assert.Equal("label:Save", CommandIdResolver.Resolve(null, null, "_Save"));

    /// <summary>
    ///     A submenu parent has no command, no name and a non-string header. Giving
    ///     it an id would count mouse travel rather than verbs.
    /// </summary>
    [Fact]
    public void SomethingWithNoStableIdentityIsNotCounted()
    {
        Assert.Null(CommandIdResolver.Resolve(null, null, null));
        Assert.Null(CommandIdResolver.Resolve(null, "  ", "   "));
        Assert.Null(CommandIdResolver.Resolve(null, null, new object()));
    }

    [Fact]
    public void ALongLabelIsBoundedRatherThanStoredWhole()
    {
        var id = CommandIdResolver.Resolve(null, null, new string('x', 500));

        Assert.NotNull(id);
        Assert.True(id!.Length <= 96, $"id was {id.Length} chars");
    }

    /// <summary>
    ///     530 clicks were counted as "CanExecuteChanged": the command's event
    ///     backing field holds the subscribed Button's handler, which is a named
    ///     method, and the field scan took it for the verb.
    /// </summary>
    [Fact]
    public void ASubscribedCanExecuteChangedHandlerIsNotTheCommandId()
    {
        var command = new RelayCommand(() => { });
        command.CanExecuteChanged += CanExecuteChanged;

        Assert.Equal("ExportButton", CommandIdResolver.Resolve(command, "ExportButton", null));
        Assert.Null(CommandIdResolver.Resolve(command, null, null));
    }

    [Fact]
    public void TheVerbWinsOverTheCanExecutePredicateAndTheSubscriber()
    {
        var command = new RelayCommand(GenerateFretHandPositions, CanGenerateFretHandPositions);
        command.CanExecuteChanged += CanExecuteChanged;

        Assert.Equal("GenerateFretHandPositions", CommandIdResolver.Resolve(command, null, null));
    }

    /// <summary>Dock builds some commands from compiled-XAML glue, whose method name says nothing.</summary>
    [Fact]
    public void AFrameworkTrampolineIsNotACommandId()
    {
        var command = new RelayCommand(XamlIlPopulateTrampoline);

        Assert.Equal("DockCloseButton", CommandIdResolver.Resolve(command, "DockCloseButton", null));
        Assert.Null(CommandIdResolver.Resolve(command, null, null));
    }

    /// <summary>
    ///     PART_IncreaseButton / PART_DecreaseButton are the same two names in every
    ///     spinner; the click is attributed to the spinner that owns them or skipped.
    /// </summary>
    [Fact]
    public void ASpinnerArrowIsCountedUnderItsOwnerOrNotAtAll()
    {
        Assert.Null(CommandIdResolver.Resolve(null, "PART_IncreaseButton", null));
        Assert.Null(CommandIdResolver.Resolve(null, "PART_DecreaseButton", new object()));

        Assert.Equal("CapoSpinner", UsageInstrumentation.OwnerName(TemplatedArrow("CapoSpinner")));

        var orphan = new Button { Name = "PART_DecreaseButton" };
        Assert.Null(CommandIdResolver.Resolve(null, UsageInstrumentation.OwnerName(orphan), null));

        Assert.Null(CommandIdResolver.Resolve(null, UsageInstrumentation.OwnerName(TemplatedArrow(null)), null));
    }

    /// <summary>A spinner arrow as a real template builds it: a PART_ button whose TemplatedParent is the owner.</summary>
    private static Button TemplatedArrow(string? ownerName)
    {
        var owner = new TemplatedControl
        {
            Name = ownerName,
            Template = new FuncControlTemplate<TemplatedControl>((_, _) => new Button { Name = "PART_IncreaseButton" })
        };
        owner.ApplyTemplate();
        return (Button)owner.GetVisualChildren().Single();
    }
}
