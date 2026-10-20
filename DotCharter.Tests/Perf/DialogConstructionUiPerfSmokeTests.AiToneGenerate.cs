// Generate tones with AI…: the charter's own words (#316). The catalog sweeps already build the view in every fake variant; this pins what only this dialog owes — a notes box that measured, capped, with its counter.

using Avalonia.Controls;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Fakes;
using DotCharter.Services.Ai.Generate;
using DotCharter.UI.Views.Dialogs;

namespace DotCharter.Tests.Perf;

public sealed partial class DialogConstructionUiPerfSmokeTests
{
    [Theory]
    [InlineData(FakeVariantNames.Default)]
    [InlineData(FakeVariantNames.Empty)]
    [InlineData(FakeVariantNames.LongText)]
    public async Task AiToneGenerate_LaysOutTheNotesBox_CappedAndWithItsCounter(string variant)
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var entry = HostedDialogs.Single(e => e.Contract == typeof(IAiToneGenerateScreen));
            Assert.Equal(typeof(AiToneGenerateDialogView), entry.ViewType);
            WithHostedDialog(entry, variant, (dialog, _) =>
            {
                var notes = Descendants<TextBox>(dialog).Single(t => t.Name == "NotesInput");
                Assert.True(notes.Bounds.Width > 100 && notes.Bounds.Height > 40, $"the notes box laid out {notes.Bounds}");
                Assert.Equal(AiToneContextText.MaxLength, notes.MaxLength);
                Assert.True(notes.AcceptsReturn);

                var counter = Descendants<TextBlock>(dialog).Single(t => t.Name == "LengthNote");
                Assert.True(counter.Bounds.Width > 1 && counter.Bounds.Height > 1, $"the counter laid out {counter.Bounds}");
                Assert.EndsWith($"/ {AiToneContextText.MaxLength}", counter.Text, StringComparison.Ordinal);
            });
        }, CancellationToken.None);
    }
}
