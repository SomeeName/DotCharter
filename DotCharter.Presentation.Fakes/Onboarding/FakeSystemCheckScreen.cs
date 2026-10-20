using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.Presentation.Contracts.Onboarding;

namespace DotCharter.Presentation.Fakes.Onboarding;

/// <summary>
///     A services-free <see cref="ISystemCheckScreen" />: the quiet all-ready state, the
///     check in flight, and each way a capability is off — the audio library missing,
///     the audio library from another build, and a GPU that cannot run the renderer.
/// </summary>
public sealed class FakeSystemCheckScreen : ObservableObject, ISystemCheckScreen
{
    private const string EngineName = "Audio engine";
    private const string GpuName = "Graphics";
    private const string Off = "Off";
    private const string Ready = "Ready";

    private const string EngineMissing =
        "The native audio library (juce_sharp.dll) is missing or Windows cannot load it, so playback, the Rig, " +
        "live input and latency calibration are off. Reinstall DotCharter.";

    private const string EngineAbi =
        "The native audio library is from a different build (this app needs version 42, the library is version 41), " +
        "so playback, the Rig, live input and latency calibration are off. Reinstall DotCharter.";

    private const string GpuNoVulkan =
        "No Vulkan graphics driver was found, so the 3D highway and the chart timeline cannot draw. " +
        "Install the current driver from your graphics card's maker.";

    private const string ProblemSummary =
        "Part of DotCharter cannot run on this computer. It stays switched off until this is fixed.";

    private FakeSystemCheckScreen(bool isBusy, string summary, params FakeSystemCapability[] rows)
    {
        IsBusy = isBusy;
        Summary = summary;
        Capabilities = rows;
        HasProblem = rows.Any(r => r.IsUnavailable);
    }

    // ── the named variants ────────────────────────────────────────────────

    public static IReadOnlyList<FakeVariant<ISystemCheckScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, AllReady),
        new(FakeVariantNames.Empty, Empty),
        new(FakeVariantNames.LongText, LongText),
        new(FakeVariantNames.Full, Full),
        new(FakeVariantNames.Busy, Busy),
        new("EngineMissing", EngineMissingOnly),
        new("AbiMismatch", AbiMismatch),
        new("GpuUnsupported", GpuUnsupported)
    ];

    /// <summary>Both probes passed: two quiet rows and nothing alarming.</summary>
    public static FakeSystemCheckScreen AllReady() =>
        new(false, "The audio engine and the graphics card are ready.",
            FakeSystemCapability.ReadyRow(EngineName, Ready), FakeSystemCapability.ReadyRow(GpuName, Ready));

    /// <summary>The least the panel ever says: two ready rows, no reasons and no summary line.</summary>
    public static FakeSystemCheckScreen Empty() =>
        new(false, string.Empty, FakeSystemCapability.ReadyRow(EngineName, Ready), FakeSystemCapability.ReadyRow(GpuName, Ready));

    /// <summary>The probes are still running.</summary>
    public static FakeSystemCheckScreen Busy() =>
        new(true, "Checking the audio engine and the graphics card…",
            FakeSystemCapability.CheckingRow(EngineName), FakeSystemCapability.CheckingRow(GpuName));

    /// <summary>Both capabilities off at once.</summary>
    public static FakeSystemCheckScreen Full() =>
        new(false, ProblemSummary,
            FakeSystemCapability.OffRow(EngineName, Off, EngineMissing), FakeSystemCapability.OffRow(GpuName, Off, GpuNoVulkan));

    public static FakeSystemCheckScreen EngineMissingOnly() =>
        new(false, ProblemSummary,
            FakeSystemCapability.OffRow(EngineName, Off, EngineMissing), FakeSystemCapability.ReadyRow(GpuName, Ready));

    public static FakeSystemCheckScreen AbiMismatch() =>
        new(false, ProblemSummary,
            FakeSystemCapability.OffRow(EngineName, Off, EngineAbi), FakeSystemCapability.ReadyRow(GpuName, Ready));

    public static FakeSystemCheckScreen GpuUnsupported() =>
        new(false, ProblemSummary,
            FakeSystemCapability.ReadyRow(EngineName, Ready),
            FakeSystemCapability.OffRow(GpuName, Off,
                "No graphics card here can hand Vulkan frames to the window (D3D11 shared textures are not supported), " +
                "so the 3D highway and the chart timeline cannot draw. A driver update may add it."));

    /// <summary>The German copy, the longest the panel has to wrap, with both rows off.</summary>
    public static FakeSystemCheckScreen LongText() =>
        new(false, "Ein Teil von DotCharter kann auf diesem Computer nicht laufen. Er bleibt ausgeschaltet, bis das behoben ist.",
            FakeSystemCapability.OffRow("Audio-Engine", "Aus",
                "Die native Audiobibliothek stammt aus einem anderen Build (diese App braucht Version 42, die Bibliothek " +
                "hat Version 41). Wiedergabe, das Rig, Live-Eingang und Latenzkalibrierung sind deshalb aus. " +
                "Installiere DotCharter neu."),
            FakeSystemCapability.OffRow("グラフィック", "オフ",
                "Vulkanの画像をウィンドウに渡せるグラフィックカードがありません（D3D11共有テクスチャに未対応）。" +
                "そのため3Dハイウェイとチャートのタイムラインを描画できません。ドライバーを更新すると対応する場合があります。"));

    // ── ISystemCheckScreen ────────────────────────────────────────────────

    public bool IsBusy { get; }

    public bool HasProblem { get; }

    public string Summary { get; }

    public IReadOnlyList<ISystemCapability> Capabilities { get; }
}

/// <summary>One row of the fake check.</summary>
public sealed class FakeSystemCapability : ObservableObject, ISystemCapability
{
    private FakeSystemCapability(string name, string statusText, bool isAvailable, bool isUnavailable, string reason)
    {
        Name = name;
        StatusText = statusText;
        IsAvailable = isAvailable;
        IsUnavailable = isUnavailable;
        Reason = reason;
    }

    public string Name { get; }

    public string StatusText { get; }

    public bool IsAvailable { get; }

    public bool IsUnavailable { get; }

    public string Reason { get; }

    public bool HasReason => Reason.Length > 0;

    public static FakeSystemCapability ReadyRow(string name, string status) => new(name, status, true, false, string.Empty);

    public static FakeSystemCapability CheckingRow(string name) => new(name, "Checking…", false, false, string.Empty);

    public static FakeSystemCapability OffRow(string name, string status, string reason) => new(name, status, false, true, reason);
}
