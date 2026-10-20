using System.Text.RegularExpressions;
using Avalonia.Input;
using DotCharter.Services.Diagnostics;
using DotCharter.UI.Input;

namespace DotCharter.Tests.UI;

/// <summary>
///     The usage statistics key a keybind by the registration's LABEL, and until
///     2026-09-03 that label was <c>action.Method.Name</c> — which for the 140
///     lambdas in <c>AppKeybindRegistrar.RegisterAll</c> is the compiler's
///     <c>&lt;RegisterAll&gt;b__53</c>. The owner's real report read
///     "110 available, 3 used" over a list of those, so the one instrument that
///     answers "which of my shortcuts do I never press" could not name a single
///     one — and the number after <c>b__</c> churns whenever the file is edited,
///     so the counts were also silently split across releases.
///     <para>
///         The label now comes from the <c>KeybindConfig</c> property the
///         registration READS (<c>() => k.PlayPause</c> is an expression, and its
///         body names the member), which is the same name the palette's
///         <c>Keybind_*</c> resource keys and the settings page use.
///     </para>
/// </summary>
public sealed class KeybindUsageLabelTests
{
    private sealed class RecordingTracker : IUsageTracker
    {
        public List<(string Kind, string Id)> Records { get; } = [];
        public void Record(string kind, string id) => Records.Add((kind, id));
        public void RecordDuration(string kind, string id, long milliseconds) { }
        public UsageStatistics Snapshot { get; } = new();
        public void Flush() { }
    }

    private sealed class Config
    {
        public KeyChord PlayPause { get; } = new(Key.Space);
        public KeyChord SpeedUp { get; } = new(Key.OemPlus);
    }

    [Fact]
    public void ARebindableRegistrationIsLabelledByTheConfigPropertyItReads()
    {
        var tracker = new RecordingTracker();
        var handler = new ChartKeybindHandler(() => false, new FakeAppLogger(), tracker);
        var k = new Config();
        var fired = 0;

        // A lambda action, exactly the registrar's shape — the label must NOT
        // come from the delegate.
        handler.Register(() => k.PlayPause, () => fired++);
        handler.RefreshBinds();

        Assert.Equal(["PlayPause"], handler.KnownKeybindLabels);
        Assert.True(handler.HandleKeyPress(Key.Space, false, false, false));
        Assert.Equal(1, fired);
        Assert.Equal([(UsageKind.Keybind, "PlayPause")], tracker.Records);
    }

    [Fact]
    public void AFixedKeyRegistrationCarriesTheLabelItWasGiven()
    {
        var handler = new ChartKeybindHandler(() => false, new FakeAppLogger());
        handler.Register(() => Key.F12, () => { }, "CaptureHighwayScreenshot");
        handler.Register(new KeyChord(Key.F6), () => { }, "Something");
        handler.RefreshBinds();

        Assert.Equal(["CaptureHighwayScreenshot", "Something"], handler.KnownKeybindLabels.Order().ToList());
    }

    /// <summary>
    ///     A fixed key with a lambda and no label has nothing to be named after
    ///     but the chord itself; that is still a stable, readable id, and never a
    ///     compiler name.
    /// </summary>
    [Fact]
    public void NoRegistrationEverLandsAsACompilerGeneratedName()
    {
        var handler = new ChartKeybindHandler(() => false, new FakeAppLogger());
        handler.Register(() => Key.F11, () => { });
        handler.Register(new KeyChord(Key.F10, KeyModifiers.Control), () => { });
        handler.RefreshBinds();

        Assert.All(handler.KnownKeybindLabels, label => Assert.DoesNotContain('<', label));
        Assert.Contains("key:F11", handler.KnownKeybindLabels);
    }

    [Fact]
    public void AMethodGroupStillReadsAsItsOwnName()
    {
        var handler = new ChartKeybindHandler(() => false, new FakeAppLogger());
        handler.Register(new KeyChord(Key.F6), Shipped);
        handler.RefreshBinds();

        Assert.Equal([nameof(Shipped)], handler.KnownKeybindLabels);
    }

    private static void Shipped() { }

    /// <summary>
    ///     A verb reached by a fixed key AND a rebindable one is one verb.
    ///     <c>Key.Add</c> doubles <c>SpeedUp</c>; if the fixed site is unlabelled
    ///     it lands in a "key:Add" bucket and SpeedUp reads as half as used.
    /// </summary>
    [Fact]
    public void EveryFixedKeySiteInTheRegistrarNamesItsVerb()
    {
        var source = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Input", "AppKeybindRegistrar.cs")));
        var fixedSites = Regex.Matches(source, @"_registry\.Register\(\(\) => Key\.\w+,\s*\(\) =>[^;]*;", RegexOptions.Singleline);

        Assert.NotEmpty(fixedSites);
        foreach (Match site in fixedSites)
        {
            Assert.True(Regex.IsMatch(site.Value, @",\s*""[A-Za-z]+""\);\s*$"),
                $"Fixed-key registration without a verb label:\n{site.Value}");
        }
    }
}
