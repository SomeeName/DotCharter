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
}
