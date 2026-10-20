using System.ComponentModel;

namespace DotCharter.Presentation.Contracts.Onboarding;

/// <summary>
///     The first-run wizard's check of this computer, on its first page: can the
///     native audio engine load, and can the GPU run the Vulkan renderer the
///     highway and the chart views draw with.
///     <para>
///         A capability the app cannot honour is drawn DISABLED with the reason
///         beside it; when everything is fine the panel is two quiet "Ready" rows.
///         Both probes run off the UI thread and neither opens an audio device
///         or creates a GPU device.
///     </para>
/// </summary>
public interface ISystemCheckScreen : INotifyPropertyChanged
{
    /// <summary>True while the probes run.</summary>
    bool IsBusy { get; }

    /// <summary>True when at least one capability cannot run here.</summary>
    bool HasProblem { get; }

    /// <summary>The one line over the rows: checking, all ready, or what is off.</summary>
    string Summary { get; }

    /// <summary>The audio engine, then graphics.</summary>
    IReadOnlyList<ISystemCapability> Capabilities { get; }
}

/// <summary>One capability's row. Immutable: a finished check replaces the list.</summary>
public interface ISystemCapability : INotifyPropertyChanged
{
    /// <summary>"Audio engine", "Graphics".</summary>
    string Name { get; }

    /// <summary>"Checking…", "Ready" or "Off".</summary>
    string StatusText { get; }

    /// <summary>The probe said it works.</summary>
    bool IsAvailable { get; }

    /// <summary>The probe said it cannot work — the row is drawn disabled.</summary>
    bool IsUnavailable { get; }

    /// <summary>What is off and why, and what to do; empty when available or still checking.</summary>
    string Reason { get; }

    bool HasReason { get; }
}
