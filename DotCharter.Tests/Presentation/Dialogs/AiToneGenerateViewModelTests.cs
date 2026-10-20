using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Ai.Generate;

namespace DotCharter.Tests.Presentation.Dialogs;

/// <summary>
///     Tools ▸ Generate tones with AI…: Generate answers the cleaned, capped text, Cancel answers null
///     (nothing starts), and the text is kept per project between opens (#316).
/// </summary>
public sealed class AiToneGenerateViewModelTests
{
    private static ProjectState StateOf(ChartProject project) => new() { ActiveProject = project };

    [Fact]
    public async Task Generate_answers_the_cleaned_text_and_cancel_answers_null()
    {
        var state = StateOf(new ChartProject());

        var generate = new AiToneGenerateViewModel(new DialogMemory(), state) { Notes = "  dark \n and heavy " };
        generate.GenerateCommand.Execute(null);
        var cancel = new AiToneGenerateViewModel(new DialogMemory(), state) { Notes = "ignored" };
        cancel.CancelCommand.Execute(null);

        Assert.Equal("dark and heavy", await generate.Result);
        Assert.Null(await cancel.Result);
    }

    [Fact]
    public async Task An_empty_box_still_generates_with_an_empty_answer()
    {
        var vm = new AiToneGenerateViewModel(new DialogMemory(), StateOf(new ChartProject()));

        vm.GenerateCommand.Execute(null);

        Assert.Equal("", await vm.Result);
    }

    [Fact]
    public async Task The_text_is_kept_for_this_project_and_a_cancelled_open_does_not_overwrite_it()
    {
        var memory = new DialogMemory();
        var state = StateOf(new ChartProject());
        var first = new AiToneGenerateViewModel(memory, state) { Notes = "cranked Marshall" };
        first.GenerateCommand.Execute(null);
        await first.Result;

        var second = new AiToneGenerateViewModel(memory, state);
        Assert.Equal("cranked Marshall", second.Notes);
        second.Notes = "something else";
        second.CancelCommand.Execute(null);

        Assert.Equal("cranked Marshall", new AiToneGenerateViewModel(memory, state).Notes);
    }

    [Fact]
    public void Another_project_starts_with_an_empty_box()
    {
        var memory = new DialogMemory();
        var first = new AiToneGenerateViewModel(memory, StateOf(new ChartProject())) { Notes = "for the first song" };
        first.GenerateCommand.Execute(null);

        var other = new AiToneGenerateViewModel(memory, StateOf(new ChartProject()));

        Assert.Equal("", other.Notes);
    }

    [Fact]
    public void The_counter_follows_the_text_against_the_cap()
    {
        var vm = new AiToneGenerateViewModel(new DialogMemory(), StateOf(new ChartProject()));
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.Notes = "12345";

        Assert.Equal($"5 / {AiToneContextText.MaxLength}", vm.LengthText);
        Assert.Contains(nameof(vm.LengthText), changed);
        Assert.Equal(AiToneContextText.MaxLength, vm.MaxLength);
    }
}
