using DotCharter.Services.State;

namespace DotCharter.Services.Ai.Chat;

/// <summary>
///     The chart the chat history serves: its identity (<c>ChartProject.ProjectId</c>)
///     and the folder its <c>.rscp</c> lives in, or null when it has none yet.
/// </summary>
public sealed record AiChatChart(string ChartId, string? ProjectFolder);

/// <summary>Which chart is open, asked at the START of every history call (on the
/// caller's thread) so the file work that follows cannot drift to another chart.</summary>
public interface IAiChatChartSource
{
    /// <summary>The open chart, or null when nothing is open.</summary>
    AiChatChart? Current { get; }
}

/// <summary>
///     The app's open chart: <see cref="ProjectState.ActiveProject" />'s id and the
///     folder of <see cref="ProjectState.CurrentProjectPath" />. A project with no
///     path (a library song, a fresh import never saved) has no folder.
/// </summary>
public sealed class ProjectStateAiChatChartSource : IAiChatChartSource
{
    private readonly ProjectState _state;

    public ProjectStateAiChatChartSource(ProjectState state) =>
        _state = state ?? throw new ArgumentNullException(nameof(state));

    public AiChatChart? Current =>
        _state.ActiveProject is { } project ? new AiChatChart(project.ProjectId, FolderOf(_state.CurrentProjectPath)) : null;

    internal static string? FolderOf(string? projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath)) return null;
        try
        {
            var folder = Path.GetDirectoryName(Path.GetFullPath(projectPath));
            return string.IsNullOrEmpty(folder) ? null : folder;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
