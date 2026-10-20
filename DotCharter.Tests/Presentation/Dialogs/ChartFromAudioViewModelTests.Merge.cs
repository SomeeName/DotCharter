namespace DotCharter.Tests.Presentation.Dialogs;

/// <summary>
///     Chart from audio's second target (#647): "Merge into the current arrangement" holds the draft
///     on the merge card and writes nothing. It is not the default, it is offered only with a level
///     to merge into, and on a chart whose phrases already have notes it holds nothing until the
///     charter chooses "replace existing notes". The harness project has no phrase markers, so the
///     whole song is one span.
/// </summary>
public sealed partial class ChartFromAudioViewModelTests
{
    [Fact]
    public async Task The_merge_target_holds_the_draft_on_the_card_and_writes_nothing()
    {
        var h = new Harness();
        var project = h.LoadProject(WritePluckWav("mix.wav"));
        var before = ChartProjectCloner.Clone(project);
        var vm = h.Open();
        await Analyze(vm);
        Assert.True(vm.HasDraft);

        Assert.True(vm.CanMerge);
        Assert.False(vm.MergeIntoCurrent);
        Assert.True(vm.AddAsNewArrangement);
        vm.MergeIntoCurrent = true;
        Assert.False(vm.AddAsNewArrangement);
        Assert.Equal(FakeLocalization.English()["ChartFromAudio_ApplyMerge"], vm.ApplyText);
        Assert.Equal(FakeLocalization.English().Format("ChartFromAudio_TargetMerge", "Lead"), vm.MergeTargetText);
        vm.ApplyCommand.Execute(null);

        Assert.True(ChartEditHarness.Answer(vm.Result));
        var review = h.MergeHost.Review;
        Assert.NotNull(review);
        var phrase = Assert.Single(review.Phrases);
        Assert.Equal(vm.DraftNoteCount, phrase.Draft.Count);
        Assert.False(phrase.Replaces);
        Assert.Same(project.Arrangements[0], review.Arrangement);
        Assert.False(h.History.CanUndo);
        Assert.Empty(DeepCompare.Diff("project", before, project, new HashSet<string>()));
    }

    [Fact]
    public async Task Phrases_with_notes_are_held_only_once_replace_existing_notes_is_chosen()
    {
        var h = new Harness();
        var project = h.LoadProject(WritePluckWav("mix.wav"));
        project.Arrangements[0].Levels[0].Notes.Add(new GuitarNote { Position = new FractionalPosition(0), StringIndex = 5, Fret = 3 });
        var vm = h.Open();
        await Analyze(vm);
        vm.MergeIntoCurrent = true;

        vm.ApplyCommand.Execute(null);

        Assert.False(vm.Result.IsCompleted);
        Assert.Null(h.MergeHost.Review);
        Assert.Equal(FakeLocalization.English().Plural("ChartFromAudio_MergeAllOccupiedOne", "ChartFromAudio_MergeAllOccupied", 1),
            vm.Status);

        vm.ReplaceExisting = true;
        Assert.Equal("", vm.Status);
        vm.ApplyCommand.Execute(null);

        Assert.True(ChartEditHarness.Answer(vm.Result));
        Assert.True(Assert.Single(h.MergeHost.Review!.Phrases).Replaces);
        Assert.False(h.History.CanUndo);
    }

    [Fact]
    public void Without_a_level_to_merge_into_the_merge_target_is_not_offered()
    {
        var h = new Harness();
        var project = h.LoadProject(Path.Combine(_dir, "mix.ogg"));
        project.Arrangements[0].Levels.Clear();
        h.State.ActiveLevel = null;
        var vm = h.Open();

        vm.MergeIntoCurrent = true;

        Assert.False(vm.CanMerge);
        Assert.False(vm.MergeIntoCurrent);
        Assert.Equal(FakeLocalization.English()["ChartFromAudio_Apply"], vm.ApplyText);
    }

    [Fact]
    public async Task The_new_arrangement_target_is_unchanged_by_the_merge()
    {
        var h = new Harness();
        var project = h.LoadProject(WritePluckWav("mix.wav"));
        var vm = h.Open();
        await Analyze(vm);

        vm.ApplyCommand.Execute(null);

        Assert.Equal(2, project.Arrangements.Count);
        Assert.Null(h.MergeHost.Review);
    }
}
