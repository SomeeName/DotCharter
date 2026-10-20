using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using DotCharter.Localization;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.UI;

/// <summary>
///     RTL smoke check (0.13.0). The shell binds its root
///     <c>FlowDirection</c> to <see cref="ILocalizationService.IsRightToLeft" />
///     through <see cref="DotCharter.UI.Converters.BoolConverters.ToFlowDirection" />, so a right-to-left
///     community language pack flips the whole layout. These pure tests verify
///     the detection + mapping the binding relies on (no app/window boot, so
///     they run in the default gate).
///
///     Manual visual check (documented in docs/agents/ui/misc-surfaces.md): drop an
///     <c>ar.json</c> pack into &lt;ConfigRoot&gt;/languages, select it, and
///     confirm the shell mirrors right-to-left without clipping.
/// </summary>
[Collection("LocalizationSingleton")]
public sealed class RtlSupportTests
{
    [Theory]
    [InlineData("ar", true)]   // Arabic — right-to-left
    [InlineData("he", true)]   // Hebrew — right-to-left
    [InlineData("fr-FR", false)]
    public void CommunityCulture_ReportsCorrectDirectionality(string code, bool expectedRtl)
    {
        var dir = Path.Combine(Path.GetTempPath(), "dotcharter-rtl-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, code + ".json"),
                "{ \"Test_Rtl_Key\": \"x\" }");

            LocalizationService.Instance.LoadCommunityLanguages(dir);
            LocalizationService.Instance.SetConfiguredCulture(code);

            Assert.Equal(expectedRtl, LocalizationService.Instance.IsRightToLeft);
        }
        finally
        {
            LocalizationService.Instance.LoadCommunityLanguages(
                Path.Combine(Path.GetTempPath(), "dotcharter-rtl-cleanup-" + Guid.NewGuid().ToString("N")));
            LocalizationService.Instance.SetConfiguredCulture("en-US");
            BestEffortDelete.Directory(dir);
        }
    }

    [Fact]
    public void BuiltInLanguages_AreLeftToRight()
    {
        // SetConfiguredCulture writes CultureInfo.DefaultThreadCurrentUICulture —
        // PROCESS-WIDE, so it is read by every thread that never set its own.
        // (It wrote DefaultThreadCurrentCulture too until 2026-08-29; the
        // FORMATTING culture is the user's Windows setting and is left alone
        // now — see DotCharter.Localization/AGENTS.md. The hazard this comment
        // describes is unchanged: the UI culture is just as process-wide.)
        // Restoring it on the happy path only is not restoring it: one failed
        // assert below used to strand the whole run on German, and the classes
        // that read LocalizationService.Instance for display strings are not in
        // this collection, so nothing would have put it back.
        try
        {
            LocalizationService.Instance.SetConfiguredCulture("de-DE");
            Assert.False(LocalizationService.Instance.IsRightToLeft);
        }
        finally
        {
            LocalizationService.Instance.SetConfiguredCulture("en-US");
        }

        Assert.False(LocalizationService.Instance.IsRightToLeft);
    }

    [Fact]
    public void ToFlowDirection_MapsBoolToFlowDirection()
    {
        var converter = (IValueConverter)DotCharter.UI.Converters.BoolConverters.ToFlowDirection;
        Assert.Equal(FlowDirection.RightToLeft,
            converter.Convert(true, typeof(FlowDirection), null, CultureInfo.InvariantCulture));
        Assert.Equal(FlowDirection.LeftToRight,
            converter.Convert(false, typeof(FlowDirection), null, CultureInfo.InvariantCulture));
    }
}
