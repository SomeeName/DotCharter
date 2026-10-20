namespace DotCharter.Services.Infrastructure.Shell;

/// <summary>
///     Closes the application the way the user's own Close does — through the
///     main window, so shutdown takes the clean path. The detached updater
///     waits on the process either way.
/// </summary>
public interface IApplicationCloser
{
    void Close();
}
