using System.Text.RegularExpressions;
using Avalonia.Controls;
using DotCharter.UI.Controls;
using DotCharter.UI.Platform;

namespace DotCharter.Tests.UI;

/// <summary>
///     The 3D preview is the Avalonia-COMPOSED host and nothing else. The
///     native child-HWND swapchain host (<c>Preview3DCanvas</c>), its appconfig
///     opt-out (<c>Vulkan3DNativeSwapchainEnabled</c>) and the airspace
///     workarounds it needed (the dialog panel-collapse and the pedalboard
///     <c>PrintWindow</c> freeze-frame) were removed on purpose: a child HWND
///     paints over every popup and overlay. These facts fail if any of them
///     comes back, and keep the startup wiring that replaced them honest.
/// </summary>
public sealed class Preview3DComposedOnlyTests
{
    /// <summary>A <c>NativeControlHost</c> in the app assembly is a child HWND,
    /// i.e. airspace again. The 3D host was the last one (with the unused
    /// <c>AirspaceNativeControlHost</c>).</summary>
    [Fact]
    public void TheAppAssembly_HostsNoNativeChildWindow()
    {
        var hosts = typeof(Preview3DComposedCanvas).Assembly.GetTypes()
            .Where(t => typeof(NativeControlHost).IsAssignableFrom(t))
            .Select(t => t.FullName)
            .ToList();
        Assert.True(hosts.Count == 0,
            "A NativeControlHost is a child HWND that paints over every Avalonia overlay — " +
            "the 3D preview is composed-only: " + string.Join(", ", hosts));
    }

    [Theory]
    [InlineData("DotCharter.UI.Controls.Preview3DCanvas")]
    [InlineData("DotCharter.UI.Controls.Preview3DRenderPath")]
    [InlineData("DotCharter.UI.Platform.Preview3DPathPolicy")]
    [InlineData("DotCharter.UI.Platform.Preview3DAirspacePolicy")]
    public void TheRemovedPathSwitch_StaysRemoved(string typeName) => Assert.Null(typeof(Preview3DComposedCanvas).Assembly.GetType(typeName));

    /// <summary>No appconfig key may pick a 3D presentation path — neither the
    /// opt-out nor the pre-0.49.3 opt-in.</summary>
    [Theory]
    [InlineData("Vulkan3DNativeSwapchainEnabled")]
    [InlineData("Vulkan3DComposedEnabled")]
    public void TheConfig_HasNoPathSwitch(string property) => Assert.Null(typeof(AppConfig).GetProperty(property));

    /// <summary>The view builds the composed host unconditionally — no branch,
    /// no second factory.</summary>
    [Fact]
    public void Preview3DView_BuildsOnlyTheComposedHost()
    {
        var view = Code("DotCharter.UI", "Views", "Editor", "Preview3DView.axaml.cs");
        Assert.Single(Regex.Matches(view, @"\bnew\s+Preview3D\w*Canvas\b"));
        Assert.Contains("new Preview3DComposedCanvas", view);
        Assert.DoesNotContain("NativeControlHost", view);
        Assert.DoesNotContain("?", Body(view, "public Preview3DView()"));
    }

    /// <summary>Overlay popups are unconditional: with no child HWND there is
    /// nothing for an in-window popup to render behind.</summary>
    [Fact]
    public void OverlayPopups_AreAlwaysOn()
    {
        var program = Code("DotCharter.UI", "Program.cs");
        Assert.Contains("OverlayPopups = true,", program);
        Assert.DoesNotContain("OverlayPopupsEnabled", program);
    }

    /// <summary>The airspace workarounds read nothing about dialogs or the rig:
    /// the panel follows the preview toggle and the editor tab alone, and the
    /// window no longer swaps the panel for a still on <c>IsPedalboardOpen</c>.</summary>
    [Fact]
    public void TheAirspaceWorkarounds_StayGone()
    {
        var layout = Code("DotCharter.UI", "ViewModels", "Shell", "MainWindowViewModel.Layout.cs");
        Assert.Contains("public bool Is3DPreviewActive => IsPreviewVisible && IsChartEditorVisible;", layout);

        var window = MainWindowSource.ReadAllText();
        Assert.DoesNotContain("Preview3DFreezeFrame", window);
        var capture = Code("DotCharter.UI", "Views", "Shell", "MainWindowCapture.cs");
        Assert.DoesNotContain("FreezeFrame", capture);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("not json", null)]
    [InlineData("[1]", null)]
    [InlineData("{}", null)]
    [InlineData("""{ "Win32CompositionMode": 3 }""", null)]
    [InlineData("""{ "Win32CompositionMode": "LowLatencyDxgiSwapChain" }""", "LowLatencyDxgiSwapChain")]
    public void TheStartupProbe_ReadsAStringProperty_Tolerantly(string? json, string? expected) => Assert.Equal(expected, StartupConfigProbe.StringValue(json, "Win32CompositionMode"));

    [Fact]
    public void TheBuilder_ReadsTheRawConfigOnce()
    {
        var program = Code("DotCharter.UI", "Program.cs");
        Assert.Equal(1, Regex.Matches(program, @"ProbeRawAppConfig\(\)").Count - 1 /* the definition */);
        Assert.Contains("StartupConfigProbe.StringValue(rawConfig,", program);
    }

    /// <summary>There is no fallback, so an unavailable compositor must reach
    /// the status rail, not just the log — and the message may not point at a
    /// switch that no longer exists.</summary>
    [Fact]
    public void ComposedUnavailable_IsSaidOnTheStatusRail_InEveryLanguage()
    {
        var canvas = Code("DotCharter.UI", "Controls", "Preview3DComposedCanvas.cs");
        var body = canvas[canvas.IndexOf("void LogUnavailable(", StringComparison.Ordinal)..];
        Assert.Contains("ProjectState>()?.SetMessage(", body);
        Assert.Contains("\"Preview3D_ComposedUnavailable\"", body);
        Assert.DoesNotContain("Vulkan3DNativeSwapchainEnabled", canvas);
        foreach (var resx in new[] { "Resources.resx", "Resources.de-DE.resx", "Resources.ja-JP.resx" })
        {
            var text = UiSourceTree.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "DotCharter.Localization", resx));
            Assert.Contains("name=\"Preview3D_ComposedUnavailable\"", text);
            Assert.DoesNotContain("Vulkan3DNativeSwapchainEnabled", text);
        }
    }

    private static string Body(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{signature}' not found — repoint this lint.");
        var end = source.IndexOf("\n    }", start, StringComparison.Ordinal);
        return end < 0 ? source[start..] : source[start..end];
    }

    private static string Code(params string[] parts)
    {
        var text = UiSourceTree.ReadAllText(UiSourceTree.Resolve(
            Path.Combine(new[] { TestPaths.RepoRoot() }.Concat(parts).ToArray())));
        return Regex.Replace(Regex.Replace(text, @"/\*.*?\*/", "", RegexOptions.Singleline), @"//[^\r\n]*", "");
    }
}
