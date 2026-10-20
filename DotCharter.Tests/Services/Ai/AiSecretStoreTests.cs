using System.Text;
using DotCharter.Tone.Ai;

namespace DotCharter.Tests.Services.Ai;

/// <summary>
///     The DPAPI secret store. Every case runs against a TEMP path — the real one
///     is under the config root and a test must never touch the owner's keys.
/// </summary>
public sealed class AiSecretStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"dc-ai-secrets-{Guid.NewGuid():N}.bin");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public void Write_ThenRead_RoundTripsThroughTheFile()
    {
        if (!new DpapiAiSecretStore().IsPersistent) Assert.Skip("DPAPI is Windows-only.");

        var store = new DpapiAiSecretStore(filePath: _path);
        store.Write(AiProviderKind.Claude, "sk-ant-test-key");

        Assert.True(File.Exists(_path));

        // A SECOND instance, so the value came off disk and not out of a field.
        var reopened = new DpapiAiSecretStore(filePath: _path);
        Assert.Equal("sk-ant-test-key", reopened.Read(AiProviderKind.Claude));
    }

    [Fact]
    public void TheFileOnDisk_DoesNotContainThePlaintextKey()
    {
        if (!new DpapiAiSecretStore().IsPersistent) Assert.Skip("DPAPI is Windows-only.");

        var store = new DpapiAiSecretStore(filePath: _path);
        store.Write(AiProviderKind.Claude, "sk-ant-a-very-recognisable-secret");

        var bytes = File.ReadAllBytes(_path);
        Assert.DoesNotContain("sk-ant-a-very-recognisable-secret",
            Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        Assert.DoesNotContain("sk-ant-a-very-recognisable-secret",
            Encoding.Unicode.GetString(bytes), StringComparison.Ordinal);
    }

    [Fact]
    public void Kinds_DoNotOverwriteEachOther()
    {
        if (!new DpapiAiSecretStore().IsPersistent) Assert.Skip("DPAPI is Windows-only.");

        var store = new DpapiAiSecretStore(filePath: _path);
        store.Write(AiProviderKind.Claude, "claude-key");
        store.Write(AiProviderKind.OpenAiCompatible, "gateway-key");

        Assert.Equal("claude-key", store.Read(AiProviderKind.Claude));
        Assert.Equal("gateway-key", store.Read(AiProviderKind.OpenAiCompatible));
    }

    [Fact]
    public void WritingNull_ClearsTheKey_AndTheLastOneRemovesTheFile()
    {
        if (!new DpapiAiSecretStore().IsPersistent) Assert.Skip("DPAPI is Windows-only.");

        var store = new DpapiAiSecretStore(filePath: _path);
        store.Write(AiProviderKind.Claude, "claude-key");
        store.Write(AiProviderKind.Claude, null);

        Assert.Null(store.Read(AiProviderKind.Claude));
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void ACorruptedFile_ReadsAsNullAndIsLogged_NeverThrown()
    {
        if (!new DpapiAiSecretStore().IsPersistent) Assert.Skip("DPAPI is Windows-only.");

        File.WriteAllBytes(_path, "this is not a DPAPI blob"u8.ToArray());
        var logger = new RecordingLogger();
        var store = new DpapiAiSecretStore(logger, _path);

        Assert.Null(store.Read(AiProviderKind.Claude));
        Assert.Contains(logger.Warnings, w => w.Contains("treated as absent", StringComparison.Ordinal));
    }

    [Fact]
    public void AMissingFile_ReadsAsNull_WithNoWarning()
    {
        var logger = new RecordingLogger();
        var store = new DpapiAiSecretStore(logger, _path);

        Assert.Null(store.Read(AiProviderKind.Claude));
        Assert.Empty(logger.Warnings);
    }

    [Fact]
    public void TheInMemoryStore_HasTheSameSemantics()
    {
        var store = new InMemoryAiSecretStore();
        Assert.Null(store.Read(AiProviderKind.Claude));

        store.Write(AiProviderKind.Claude, "k");
        Assert.Equal("k", store.Read(AiProviderKind.Claude));

        store.Write(AiProviderKind.Claude, "  ");
        Assert.Null(store.Read(AiProviderKind.Claude));
    }

    [Fact]
    public void TheFileName_IsOnTheBackupAndPayloadCredentialLists()
    {
        // Not a spelling check: the whole point of the store is that the sealed
        // key never leaves this machine, and the two filters are what enforce it.
        Assert.True(DotCharter.Services.Infrastructure.Backup.ConfigBackupRules
            .IsCredential(DpapiAiSecretStore.FileName));
        Assert.False(DotCharter.Services.Infrastructure.Backup.ConfigBackupRules
            .ShouldInclude(DpapiAiSecretStore.FileName));
        Assert.True(DotCharter.Services.Update.PayloadReceiptFilter
            .IsLocalState(DpapiAiSecretStore.FileName));
    }
}

/// <summary>Minimal logger that keeps what it was told, so a "never throws, but
/// does say so" contract can be asserted on both halves.</summary>
internal sealed class RecordingLogger : IAppLogger
{
    public List<string> Warnings { get; } = [];
    public List<string> Infos { get; } = [];

    public string CurrentLogPath => "";
    public bool IsEnabled(AppLogCategory category) => true;
    public void Flush() { }
    public void ReloadSettings() { }
    public void Trace(AppLogCategory category, string message) { }
    public void Debug(AppLogCategory category, string message) { }
    public void Info(AppLogCategory category, string message) => Infos.Add(message);
    public void Warning(AppLogCategory category, string message) => Warnings.Add(message);

    public void Warning(AppLogCategory category, string message, Exception? exception) => Warnings.Add(message);

    public void Error(AppLogCategory category, string message, Exception? exception = null) => Warnings.Add(message);
    public void External(string source, string message) { }
    public event Action<LogEmittedArgs>? LogEmitted { add { } remove { } }
    public IDisposable BeginTimedOperation(AppLogCategory category, string operationName, string? details = null)
        => new Noop();

    public void LogUnhandledException(string source, Exception exception) => Warnings.Add(exception.Message);

    private sealed class Noop : IDisposable
    {
        public void Dispose() { }
    }
}
