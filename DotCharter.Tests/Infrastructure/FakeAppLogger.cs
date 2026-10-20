namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     Test double for <see cref="IAppLogger" /> that records every call so tests
///     can assert on what was logged without spinning up Serilog.
/// </summary>
public sealed class FakeAppLogger : IAppLogger
{
    public List<Entry> Entries { get; } = [];
    public bool FlushCalled { get; private set; }
    public bool ReloadSettingsCalled { get; private set; }
    public int TimedOperationsStarted { get; private set; }
    public int TimedOperationsCompleted { get; private set; }
    public string CurrentLogPath => "<fake>";

#pragma warning disable CS0067 // Event is part of the IAppLogger contract; tests don't fire it.
    public event Action<LogEmittedArgs>? LogEmitted;
#pragma warning restore CS0067

    public bool IsEnabled(AppLogCategory category) => true;

    public void Flush() => FlushCalled = true;

    public void ReloadSettings() => ReloadSettingsCalled = true;

    public void Trace(AppLogCategory c, string m) => Add(new Entry("TRACE", c, m));

    public void Debug(AppLogCategory c, string m) => Add(new Entry("DEBUG", c, m));

    public void Info(AppLogCategory c, string m) => Add(new Entry("INFO", c, m));

    public void Warning(AppLogCategory c, string m) => Add(new Entry("WARN", c, m));

    public void Warning(AppLogCategory c, string m, Exception? ex) => Add(new Entry("WARN", c, m, ex));

    public void Error(AppLogCategory c, string m, Exception? ex = null) => Add(new Entry("ERROR", c, m, ex));

    public void External(string source, string message) => Add(new Entry("EXT", AppLogCategory.Application, $"[{source}] {message}"));

    public IDisposable BeginTimedOperation(AppLogCategory category, string operationName, string? details = null)
    {
        TimedOperationsStarted++;
        Add(new Entry("TRACE", category, $"[start] {operationName} {details}"));
        return new Scope(this, category, operationName);
    }

    public void LogUnhandledException(string source, Exception exception)
    {
        Add(new Entry("ERROR", AppLogCategory.Application, $"[unhandled:{source}]", exception));
        FlushCalled = true;
    }

    /// <summary>A copy taken under the lock. Enumerating <see cref="Entries" />
    /// while a pool thread logs throws "Collection was modified" (#862).</summary>
    public Entry[] Snapshot()
    {
        lock (Entries)
            return Entries.ToArray();
    }

    public bool HasEntryContaining(string level, string fragment)
    {
        lock (Entries)
            return Entries.Any(e => e.Level == level && e.Message.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Locked: a service may log from several threads at once (the cold
    /// library scan's parsers, #330), and an unlocked List.Add drops entries.</summary>
    private void Add(Entry entry)
    {
        lock (Entries)
            Entries.Add(entry);
    }

    public sealed record Entry(string Level, AppLogCategory Category, string Message, Exception? Exception = null);

    private sealed class Scope(FakeAppLogger owner, AppLogCategory category, string operationName) : IDisposable
    {
        private readonly AppLogCategory _category = category;
        private readonly string _operationName = operationName;
        private readonly FakeAppLogger _owner = owner;

        public void Dispose()
        {
            _owner.TimedOperationsCompleted++;
            _owner.Add(new Entry("INFO", _category, $"[end] {_operationName}"));
        }
    }
}
