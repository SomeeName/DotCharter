using DotCharter.Services.Ai.Chart;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Tests.Ai.Chart;
using DotCharter.Tests.Ai.Chat;
using DotCharter.Tests.Ai.Generate;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Shell;

namespace DotCharter.Tests.UI.ViewModels.Shell;

/// <summary>
///     The review card over the chat's Tones proposals (#614): one row per tone with the proposed gear
///     chain beside the current one, Accept / Reject, Apply writing the accepted definitions as one undo
///     step, and Audition — the proposed body over the tone's own window through the rig, refused in
///     words where it cannot play, ended when the review closes.
/// </summary>
public sealed class AiChartReviewTonesTests
{
    private readonly AiChatToneFixture _f = new();
    private readonly AiChartPassRun _run;
    private readonly FakeAudition _audition = new();
    private readonly AiChartReviewViewModel _vm;

    public AiChartReviewTonesTests()
    {
        var logger = new FakeAppLogger();
        var selection = new SelectionService();
        var transport = new TransportViewModel(new FakeAudioPlaybackService(), _f.State, new FakeConfigurationService(), FakeLocalization.KeysOnly());
        var editor = new ChartEditorViewModel(_f.State, transport, _f.History, selection, new TempoService(logger),
            new TrackContextRegistry(_f.State, selection), FakeLocalization.KeysOnly());
        _run = _f.Run(new FakeChartModel((_, _) => GenerateFakes.ValidReply()));
        _vm = new AiChartReviewViewModel(_run, editor, _f.History, _f.State, _f.Loc, _audition);
    }

    private Task Ask() => _run.RunInstructionAsync(new AiChartInstructedRun(AiChartJob.Tones,
        new AiChartInstruction("darker"), ToneChanges: _f.All().ToneChanges));

    [Fact]
    public async Task Each_tone_is_a_row_with_its_proposed_chain_beside_the_current_one()
    {
        await Ask();

        Assert.True(_vm.IsOpen);
        Assert.Equal($"{_f.Loc["AiChartReview_JobTones"]} · Lead", _vm.Subtitle);
        Assert.Equal(["Lead Dist", "Clean"], _vm.Rows.Select(r => r.Name));
        Assert.Equal(["0:06.0", "0:10.0"], _vm.Rows.Select(r => r.Span));
        var review = _run.Review!;
        for (var i = 0; i < 2; i++)
        {
            var tone = Assert.IsType<AiToneRowViewModel>(_vm.Rows[i].Tone);
            Assert.Equal(AiToneText.Chain(review.Tones[i].Proposed), tone.ProposedText);
            Assert.Equal(AiToneText.Chain(review.Tones[i].Current!), tone.CurrentText);
            Assert.Contains(AiToneText.Gear(review.Tones[i].Proposed.Amp), tone.ProposedText, StringComparison.Ordinal);
            Assert.Contains(" → ", tone.ProposedText, StringComparison.Ordinal);
            Assert.True(tone.CanAudition);
            Assert.True(tone.AuditionCommand.CanExecute(null));
        }

        // The unnamed tone change was refused, and the card says how many.
        Assert.True(_vm.HasFailed);
        Assert.Equal(_f.Loc.Plural("AiTones_FailedOne", "AiTones_Failed", 1), _vm.FailedText);
        // No FHP or ribbon marks: tones mark nothing on the chart.
        Assert.True(_vm.Published.IsEmpty);
    }

    [Fact]
    public async Task Accept_then_Apply_writes_one_undo_step_and_closes_the_card()
    {
        await Ask();
        var proposed = _run.Review!.Tones[1].ProposedJson;
        Assert.False(_vm.ApplyCommand.CanExecute(null));

        _vm.Rows[1].AcceptCommand.Execute(null);
        _vm.Rows[0].RejectCommand.Execute(null);
        Assert.True(_vm.Rows[1].IsAccepted);
        Assert.True(_vm.Rows[0].IsRejected);
        _vm.ApplyCommand.Execute(null);

        Assert.False(_vm.IsOpen);
        Assert.Single(_f.History.GetUndoSteps());
        Assert.Equal(proposed, _f.Project.ToneDefinitions.Single(d => d.Key == "Clean").Json);
        Assert.Equal(AiChatToneFixture.Body("Lead_Dist"), _f.Project.ToneDefinitions.Single(d => d.Key == "Lead_Dist").Json);
        // Closing the review ended any audition.
        Assert.True(_audition.Stops > 0);
    }

    [Fact]
    public async Task Audition_plays_the_tones_own_window_with_the_proposed_body_and_a_refusal_goes_to_the_rail()
    {
        await Ask();
        var item = _run.Review!.Tones[1];

        _vm.Rows[1].Tone!.AuditionCommand.Execute(null);

        Assert.Equal(("Clean", item.ProposedJson, 8000.0, 13000.0), Assert.Single(_audition.Starts));
        _audition.Refusal = "no rig here";
        _vm.Rows[0].Tone!.AuditionCommand.Execute(null);
        Assert.Equal("no rig here", _f.State.MainDisplayMessage);
        // Auditioning wrote nothing.
        Assert.False(_f.History.CanUndo);
        Assert.Equal(AiChatToneFixture.Body("Clean"), _f.Project.ToneDefinitions.Single(d => d.Key == "Clean").Json);

        _run.DiscardReview();
        Assert.False(_vm.IsOpen);
        Assert.True(_audition.Stops > 0);
    }

    [Fact]
    public async Task A_tone_with_no_body_in_the_project_yet_cannot_be_auditioned_and_says_why()
    {
        _f.Project.ToneDefinitions.Remove(_f.Project.ToneDefinitions.Single(d => d.Key == "Lead_Dist"));
        await Ask();

        var tone = _vm.Rows[0].Tone!;
        // No definition, so no display name: the key the change names.
        Assert.Equal("Lead_Dist", _vm.Rows[0].Name);
        Assert.False(tone.CanAudition);
        Assert.False(tone.AuditionCommand.CanExecute(null));
        Assert.Equal(_f.Loc["AiChartReview_AuditionNeedsBody"], tone.AuditionTip);
        Assert.Equal(_f.Loc["AiChartReview_ToneNone"], tone.CurrentText);

        // Applied, it becomes a new definition — one undo step that undo takes away again.
        _vm.Rows[0].AcceptCommand.Execute(null);
        _vm.ApplyCommand.Execute(null);
        Assert.Contains(_f.Project.ToneDefinitions, d => d.Key == "Lead_Dist");
        _f.History.Undo();
        Assert.DoesNotContain(_f.Project.ToneDefinitions, d => d.Key == "Lead_Dist");
    }

    private sealed class FakeAudition : IAiToneAudition
    {
        public List<(string Key, string Json, double From, double To)> Starts { get; } = [];

        public string? Refusal { get; set; }

        public int Stops { get; private set; }

        public string? Start(string key, string json, double fromMs, double toMs)
        {
            Starts.Add((key, json, fromMs, toMs));
            return Refusal;
        }

        public void Stop() => Stops++;
    }
}
