using System.Globalization;
using System.Xml.Linq;

namespace DotCharter.Tests.UI;

/// <summary>
///     Every overlay dialog is a child of ONE <c>DialogHost</c> panel, so which
///     one the user can actually see is decided by the <c>ZIndex</c> on its
///     <c>Grid.DialogScrim</c> — and a dialog that opens a prompt and AWAITS the
///     answer is stuck forever if that prompt renders behind it.
///     <para>
///         <c>MessageDialogView</c> is the app-wide prompt and its own comment
///         called it "topmost, hence ZIndex 120" — which was false: nine dialogs
///         sit at 200. Nothing had raised a prompt from one of them until batch
///         lint's bulk-overwrite confirmation, which is why the claim survived.
///     </para>
/// </summary>
public sealed class DialogZOrderTests
{
    [Fact]
    public void TheAppWidePrompt_SitsAboveEveryOtherDialog()
    {
        var scrims = DialogScrimZIndexes();

        Assert.True(scrims.TryGetValue("MessageDialogView.axaml", out var prompt),
            "MessageDialogView's scrim carries no explicit ZIndex — it must, it is the topmost surface");

        var above = scrims
            .Where(entry => entry.Key != "MessageDialogView.axaml" && entry.Value >= prompt)
            .Select(entry => $"{entry.Key} (ZIndex {entry.Value})")
            .ToList();

        Assert.True(above.Count == 0,
            $"MessageDialogView is at ZIndex {prompt}, but these render at or above it — a confirmation " +
            "raised from one of them would be covered by its own caller and the awaited answer would " +
            "never arrive:\n  " + string.Join("\n  ", above));

        // The scan really saw the catalogue. The floor is the LEGACY dialogs left
        // (2 after the Songsterr import moved): the ones on the dialog host have no scrim of their
        // own and are counted by HostedDialogs_LeaveStackingToTheHost instead.
        Assert.True(scrims.Count >= 2, $"only {scrims.Count} dialog scrims were found — the scan is broken");
    }

    /// <summary>
    ///     A dialog on <c>Views/Shell/DialogHost</c> (root <c>Grid.DialogFrame</c>)
    ///     has NO stacking of its own: the host stacks its dialogs top-last and
    ///     sits, as one child, above every legacy dialog and under the app-wide
    ///     prompt (<c>DialogContainerView</c>; proved at runtime by
    ///     <c>Perf/DialogConstructionUiPerfSmokeTests.TheDialogHost_SitsAboveEveryLegacyDialog_AndUnderTheAppWidePrompt</c>).
    ///     A <c>ZIndex</c> or an <c>IsOpen</c> binding on one would be a second,
    ///     competing answer to "which dialog is on top". The unsaved-changes gate
    ///     is one of them.
    /// </summary>
    [Fact]
    public void HostedDialogs_LeaveStackingToTheHost()
    {
        var dir = UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Dialogs"));
        var hosted = new List<string>();
        var offenders = new List<string>();

        foreach (var file in UiSourceTree.EnumerateFiles(dir, "*.axaml", SearchOption.AllDirectories))
        {
            var frame = XDocument.Load(file).Descendants()
                .FirstOrDefault(e => (e.Attribute("Classes")?.Value ?? string.Empty)
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Contains("DialogFrame", StringComparer.Ordinal));
            if (frame is null) continue;

            hosted.Add(Path.GetFileName(file));
            if (frame.Attribute("ZIndex") is not null || frame.Attribute("IsVisible") is not null)
                offenders.Add(Path.GetFileName(file));
        }

        Assert.True(offenders.Count == 0,
            "These hosted dialogs carry their own ZIndex / IsVisible — the host owns both:\n  " +
            string.Join("\n  ", offenders));
        Assert.Contains("UnsavedChangesDialogView.axaml", hosted);
        Assert.True(hosted.Count >= 7, $"only {hosted.Count} hosted dialogs were found — the scan is broken");
    }

    /// <summary>File name → the ZIndex on that view's <c>Grid.DialogScrim</c>.</summary>
    private static Dictionary<string, int> DialogScrimZIndexes()
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        var dir = UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Dialogs"));

        foreach (var file in UiSourceTree.EnumerateFiles(dir, "*.axaml", SearchOption.AllDirectories))
        {
            var scrim = XDocument.Load(file).Descendants()
                .FirstOrDefault(e => (e.Attribute("Classes")?.Value ?? string.Empty)
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Contains("DialogScrim", StringComparer.Ordinal));
            var raw = scrim?.Attribute("ZIndex")?.Value;
            if (raw != null && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var z))
                result[Path.GetFileName(file)] = z;
        }

        return result;
    }
}
