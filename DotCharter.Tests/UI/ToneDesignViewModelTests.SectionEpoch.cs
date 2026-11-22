using DotCharter.Tone.Ai;
using DotCharter.UI.ViewModels.PlayMode.ToneSections;

namespace DotCharter.Tests.UI;

public sealed partial class ToneDesignViewModelTests
{
    private static void PublishSection(Harness h, ProjectState state)
    {
        h.Vm.ConfigureSections(static () => null, static () => null,
            project: () => state.ActiveProject, ownerChanges: state);
        ((IToneSectionHost)h.Vm).PublishResult(ResultFor("Section tone"), "amp");
        h.Vm.TweakText = "less gain";
    }

    [Fact]
    public async Task Tweak_of_a_section_cannot_publish_after_the_song_changes()
    {
        var h = new Harness();
        var state = new ProjectState { ActiveProject = new ChartProject() };
        PublishSection(h, state);
        h.Service.Block = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = h.Vm.TweakCommand.ExecuteAsync(null);
        Assert.NotNull(Assert.Single(h.Service.Requests).CurrentTone);
        state.ActiveProject = new ChartProject();
        h.Service.Block.SetResult(true);
        await run;
        h.Vm.HearItCommand.Execute(null);
        h.Vm.SaveToneCommand.Execute(null);
        Assert.False(h.Vm.HasResult);
        Assert.Empty(h.Applied);
        Assert.Empty(h.Saved);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_section_refinement_keeps_its_owner_after_success(bool manual)
    {
        var h = new Harness();
        var state = new ProjectState { ActiveProject = new ChartProject() };
        PublishSection(h, state);
        if (manual)
        {
            await h.Vm.CopyPromptCommand.ExecuteAsync(null);
            h.Vm.ReplyText = "answer";
            await h.Vm.ImportReplyCommand.ExecuteAsync(null);
        }
        else
        {
            await h.Vm.TweakCommand.ExecuteAsync(null);
        }
        Assert.True(h.Vm.HasResult);
        state.ActiveProject = new ChartProject();
        h.Vm.HearItCommand.Execute(null);
        h.Vm.SaveToneCommand.Execute(null);
        Assert.False(h.Vm.HasResult);
        Assert.Empty(h.Applied);
        Assert.Empty(h.Saved);
    }

    [Fact]
    public async Task A_copied_section_prompt_cannot_be_imported_for_a_new_song()
    {
        var h = new Harness();
        var state = new ProjectState { ActiveProject = new ChartProject() };
        PublishSection(h, state);
        await h.Vm.CopyPromptCommand.ExecuteAsync(null);
        Assert.NotNull(Assert.Single(h.Service.ManualRequests).CurrentTone);
        state.ActiveProject = new ChartProject();
        h.Vm.ReplyText = "answer";
        await h.Vm.ImportReplyCommand.ExecuteAsync(null);
        Assert.False(h.Vm.HasResult);
        Assert.Empty(h.Service.Imports);
    }
    [Fact]
    public async Task A_section_prompt_finishing_after_a_song_switch_does_not_reach_the_clipboard()
    {
        var h = new Harness();
        var state = new ProjectState { ActiveProject = new ChartProject() };
        PublishSection(h, state);
        h.Service.PendingPrompt = new TaskCompletionSource<ToneDesignManualPrompt>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Service.ManualStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var copy = h.Vm.CopyPromptCommand.ExecuteAsync(null);
        await h.Service.ManualStarted.Task;
        state.ActiveProject = new ChartProject();
        h.Service.PendingPrompt.SetResult(new ToneDesignManualPrompt("old section", new ToneDesignPrompt("system", [], "{}", "tone_draft")));
        await copy;
        Assert.Null(h.Clipboard.Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_import_of_a_section_cannot_publish_after_a_song_switch(bool copied)
    {
        var h = new Harness();
        var state = new ProjectState { ActiveProject = new ChartProject() };
        PublishSection(h, state);
        if (copied) await h.Vm.CopyPromptCommand.ExecuteAsync(null);
        h.Service.PendingImport = new TaskCompletionSource<ToneDesignResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Service.ImportStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Vm.ReplyText = "answer";
        var import = h.Vm.ImportReplyCommand.ExecuteAsync(null);
        await h.Service.ImportStarted.Task;
        state.ActiveProject = new ChartProject();
        h.Service.PendingImport.SetResult(ResultFor("Expired import"));
        await import;
        h.Vm.HearItCommand.Execute(null);
        h.Vm.SaveToneCommand.Execute(null);
        Assert.False(h.Vm.HasResult);
        Assert.Empty(h.Applied);
        Assert.Empty(h.Saved);
    }

    [Fact]
    public async Task A_clipboard_write_finishing_after_a_song_switch_cannot_leave_an_importable_prompt()
    {
        var h = new Harness();
        var state = new ProjectState { ActiveProject = new ChartProject() };
        PublishSection(h, state);
        h.Clipboard.WriteBlock = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Clipboard.WriteStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var copy = h.Vm.CopyPromptCommand.ExecuteAsync(null);
        await h.Clipboard.WriteStarted.Task;
        state.ActiveProject = new ChartProject();
        h.Clipboard.WriteBlock.SetResult(true);
        await copy;
        h.Vm.ReplyText = "answer";
        await h.Vm.ImportReplyCommand.ExecuteAsync(null);
        Assert.Empty(h.Service.Imports);
        Assert.False(h.Vm.HasResult);
    }

}
