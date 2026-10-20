using DotCharter.Services.Diagnostics;

namespace DotCharter.Tests.Presentation.Dialogs;

/// <summary>
///     The app-wide message box as a dialog view model sees it: records every
///     message and answers a confirmation with <see cref="Answer" /> (a plain
///     message answers true, as the real one does when dismissed).
/// </summary>
internal sealed class ScriptedPrompt(bool answer = true) : IMessagePrompt
{
    public bool Answer { get; set; } = answer;

    public List<(string Title, string Message, MessageDialogKind Kind, bool Confirmation)> Shown { get; } = [];

    public Task<bool> ShowAsync(string title, string message, MessageDialogKind kind = MessageDialogKind.Info,
        string? details = null, bool confirmation = false)
    {
        Shown.Add((title, message, kind, confirmation));
        return Task.FromResult(!confirmation || Answer);
    }
}

/// <summary>A <see cref="RecordingProxy" /> that also RUNS the mutation handed to <c>RunAsSingleUndoStep</c>.</summary>
public class UndoStepProxy : RecordingProxy
{
    public static new (T Proxy, UndoStepProxy Recorder) Create<T>() where T : class
    {
        var proxy = Create<T, UndoStepProxy>();
        return (proxy, (UndoStepProxy)(object)proxy);
    }

    protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args)
    {
        var result = base.Invoke(method, args);
        if (method?.Name != "RunAsSingleUndoStep" || args is not [Action mutate]) return result;
        mutate();
        return true;
    }
}
