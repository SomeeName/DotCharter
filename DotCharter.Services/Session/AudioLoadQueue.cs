namespace DotCharter.Services.Session;

/// <summary>Reserves engine turns synchronously; no lock is held while work awaits.</summary>
internal sealed class AudioLoadQueue
{
    private readonly object _sync = new();
    private Task _tail = Task.CompletedTask;

    public Task RunAsync(Func<Task> work) => RunAsync(async () =>
    {
        await work();
        return true;
    });

    public Task<T> RunAsync<T>(Func<Task<T>> work)
    {
        Task previous;
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            previous = _tail;
            _tail = finished.Task;
        }

        return CompleteAsync();

        async Task<T> CompleteAsync()
        {
            await previous;
            try
            {
                return await work();
            }
            finally
            {
                finished.SetResult();
            }
        }
    }
}
