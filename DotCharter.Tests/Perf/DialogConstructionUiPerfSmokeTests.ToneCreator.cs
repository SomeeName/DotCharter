// The Tone designer's AI tone creator: the catalog sweeps already build the view in every fake variant; this pins what only this dialog owes — a description box, the greyed rig scope with its reason printed, the audio check (its button, or its reason, and the judge and round lines), and a proposal whose chain rows measured.

using Avalonia.Controls;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Fakes;
using DotCharter.UI.Views.Dialogs;

namespace DotCharter.Tests.Perf;

public sealed partial class DialogConstructionUiPerfSmokeTests
{
    [Theory]
    [InlineData(FakeVariantNames.Default, 4, 3)]
    [InlineData(FakeVariantNames.Empty, 0, 0)]
    [InlineData(FakeVariantNames.LongText, 2, 1)]
    [InlineData(FakeVariantNames.Full, 15, 3)]
    public async Task ToneCreator_LaysOutTheDescription_TheGreyedRigScope_TheAudioCheck_AndTheProposalRows(string variant, int rows, int rounds)
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var entry = HostedDialogs.Single(e => e.Contract == typeof(IToneCreatorScreen));
            Assert.Equal(typeof(ToneCreatorDialogView), entry.ViewType);
            WithHostedDialog(entry, variant, (dialog, _) =>
            {
                var description = Descendants<TextBox>(dialog).Single(t => t.Name == "DescriptionInput");
                Assert.True(description.Bounds.Width > 200 && description.Bounds.Height > 20, $"the description box laid out {description.Bounds}");

                var rig = Descendants<RadioButton>(dialog).Single(r => r.Name == "RigScope");
                Assert.False(rig.IsEnabled);
                var reason = Descendants<TextBlock>(dialog).Single(t => t.Name == "RigScopeReason");
                Assert.True(reason.IsEffectivelyVisible && reason.Bounds.Width > 50 && reason.Bounds.Height > 1,
                    $"the rig scope's reason laid out {reason.Bounds}");

                var check = Descendants<Button>(dialog).Single(b => b.Name == "CheckButton");
                Assert.True(check.Bounds.Width > 20 && check.Bounds.Height > 5, $"the check button laid out {check.Bounds}");
                var checkReason = Descendants<TextBlock>(dialog).Single(t => t.Name == "CheckReason");
                Assert.Equal(!check.IsEffectivelyEnabled, checkReason.IsEffectivelyVisible);
                if (checkReason.IsEffectivelyVisible)
                    Assert.True(checkReason.Bounds.Width > 50 && checkReason.Bounds.Height > 1, $"the check's reason laid out {checkReason.Bounds}");
                var checkPanel = Descendants<StackPanel>(dialog).Single(p => p.Name == "CheckPanel");
                Assert.Equal(rounds > 0, checkPanel.IsEffectivelyVisible);
                if (rounds > 0)
                {
                    var judge = Descendants<TextBlock>(dialog).Single(t => t.Name == "CheckJudge");
                    Assert.True(judge.Bounds.Width > 100 && judge.Bounds.Height > 5, $"the judge line laid out {judge.Bounds}");
                    var lines = Descendants<ItemsControl>(dialog).Single(i => i.Name == "CheckRoundList").ItemsPanelRoot!.Children.ToList();
                    Assert.Equal(rounds, lines.Count);
                    Assert.All(lines, l => Assert.True(l.Bounds.Width > 100 && l.Bounds.Height > 5, $"a round line laid out {l.Bounds}"));
                }

                var proposal = Descendants<StackPanel>(dialog).Single(p => p.Name == "ProposalPanel");
                Assert.Equal(rows > 0, proposal.IsEffectivelyVisible);
                if (rows == 0) return;
                var list = Descendants<ItemsControl>(dialog).Single(i => i.Name == "ProposalRowList");
                var realized = list.ItemsPanelRoot!.Children.ToList();
                Assert.Equal(rows, realized.Count);
                Assert.All(realized, r => Assert.True(r.Bounds.Width > 100 && r.Bounds.Height > 5, $"a proposal row laid out {r.Bounds}"));

                var accept = Descendants<Button>(dialog).Single(b => b.Name == "AcceptButton");
                Assert.True(accept.Bounds.Width > 1 && accept.Bounds.Height > 1, $"Accept laid out {accept.Bounds}");
            });
        }, CancellationToken.None);
    }
}
