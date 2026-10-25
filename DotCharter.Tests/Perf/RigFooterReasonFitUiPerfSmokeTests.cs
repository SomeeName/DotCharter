using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace DotCharter.Tests.Perf;

/// <summary>
///     #188: the rig footer's two "why is this off" cells (the tuner's 86 px readout
///     cell and the input picker's 80 px reason) ellipsized their sentences to
///     "Audio engine …" / "Live input is …", which said nothing. They now show a
///     short form and carry the sentence as a tooltip. This measures every short
///     form, in all three languages, in the cell's own style: each must fit
///     UNTRUNCATED, or the cell is back to saying nothing. The strings are read from
///     the resx files directly, so no culture switch is needed.
/// </summary>
[Collection("HeadlessUI")]
public sealed class RigFooterReasonFitUiPerfSmokeTests
{
    // AmpFxRigView.axaml: RigTunerStrip's cents / reason column is 86 px wide, and
    // RigInputChannelReason has MaxWidth="80". Re-read the markup if either moves.
    private static readonly (string Key, double CellWidth)[] Cells =
    [
        ("Tuner_NoInput_Short", 86),
        ("Tuner_SeatNotLive_Short", 86),
        ("Tuner_Listening_Short", 86),
        ("Rig_Input_Unavailable_Off_Short", 80),
    ];

    [Fact]
    public Task EveryShortReason_FitsItsCell_InEveryLanguage() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var probe = new TextBlock { Classes = { "FireRowDesc" }, TextWrapping = TextWrapping.NoWrap }.Themed();
            var window = new Window { Width = 400, Height = 100, Content = probe };
            window.Show();
            try
            {
                var misfits = new List<string>();
                foreach (var file in new[] { "Resources.resx", "Resources.de-DE.resx", "Resources.ja-JP.resx" })
                {
                    var values = XDocument.Load(Path.Combine(TestPaths.RepoRoot(), "DotCharter.Localization", file))
                        .Root!.Elements("data")
                        .ToDictionary(d => (string)d.Attribute("name")!, d => (string?)d.Element("value") ?? "");
                    foreach (var (key, cell) in Cells)
                    {
                        Assert.True(values.TryGetValue(key, out var text) && text.Length > 0, $"{file} has no {key}");
                        probe.Text = text;
                        probe.Measure(Size.Infinity);
                        Assert.True(probe.DesiredSize.Width > 1, $"'{text}' measured nothing: the FireRowDesc style did not apply");
                        if (probe.DesiredSize.Width > cell)
                            misfits.Add($"{file} {key} '{text}' is {probe.DesiredSize.Width:0} px in a {cell} px cell");
                    }
                }

                Assert.True(misfits.Count == 0, "short reasons that would be ellipsized again:\n" + string.Join("\n", misfits));
            }
            finally
            {
                window.Close();
            }
        }, TestContext.Current.CancellationToken);
}
