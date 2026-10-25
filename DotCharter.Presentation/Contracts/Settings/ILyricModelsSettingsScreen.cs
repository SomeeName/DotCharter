using System.ComponentModel;
using System.Windows.Input;

namespace DotCharter.Presentation.Contracts.Settings;

/// <summary>
///     Settings ▸ AI &amp; models ▸ Lyric auto-sync: the acoustic models that line
///     pasted lyrics up with the singing, whether each is installed, what it ran
///     on in this session, and the download, repair and retry-GPU verbs. It holds
///     no setting of its own; it is a section of the AI &amp; models page.
/// </summary>
public interface ILyricModelsSettingsScreen : INotifyPropertyChanged
{
    IReadOnlyList<ILyricModelRow> Models { get; }

    /// <summary>Row and group keys the current search filters OUT. Empty when there is no query.</summary>
    IReadOnlySet<string> HiddenKeys { get; }
}

/// <summary>One lyric model.</summary>
public interface ILyricModelRow : INotifyPropertyChanged
{
    /// <summary>The row's search key (<c>lyricModels.&lt;model id&gt;</c>).</summary>
    string Key { get; }

    /// <summary>False while the current search filters the row out.</summary>
    bool IsShown { get; }

    string Name { get; }

    string Description { get; }

    bool IsInstalled { get; }

    /// <summary>"Installed", or what a download costs.</summary>
    string StatusText { get; }

    /// <summary>What the model runs on: the GPU provider, the CPU, or the CPU after a GPU failure.</summary>
    string ProviderText { get; }

    /// <summary>True while it is on the CPU only because a GPU run failed in this session.</summary>
    bool IsCpuFallback { get; }

    bool IsBusy { get; }

    /// <summary>The download's progress line, or the failure; empty otherwise.</summary>
    string ProgressText { get; }

    ICommand DownloadCommand { get; }

    ICommand RepairCommand { get; }

    ICommand RetryGpuCommand { get; }
}
