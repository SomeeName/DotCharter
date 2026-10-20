using System.Security.Cryptography;
using System.Text.Json;

namespace DotCharter.Tone.Ai;

/// <summary>
///     DPAPI-backed API-key store for the AI tone designer, the same shape as
///     <see cref="Online.Tone3000.DpapiTone3000TokenStore" />: the secret map is
///     serialized, encrypted with <see cref="ProtectedData" /> (CurrentUser scope
///     + app entropy) and written atomically to <c>ai-secrets.bin</c> under the
///     config root. Never plaintext, never logged, never in <c>AppConfig</c>.
///
///     <para>
///         The file name is on
///         <c>Infrastructure.Backup.ConfigBackupRules</c>' credential list,
///         so it never travels in a backup or an update payload — a DPAPI blob is
///         sealed to this Windows user on this machine and is useless anywhere
///         else anyway.
///     </para>
///     <para>
///         On a non-Windows platform nothing persists: DPAPI does not exist there,
///         and writing a plaintext key instead would be a silent downgrade. The
///         user pastes the key again each session; <see cref="IsPersistent" /> says
///         so, so the Settings UI can explain it rather than look broken.
///     </para>
///     <para>
///         A file this build cannot decrypt (wrong user, wrong machine, truncated,
///         tampered) reads as "no key" and is logged at Warning — never thrown:
///         the caller is a settings page, and a corrupt blob must cost the user a
///         re-paste, not a crash.
///     </para>
/// </summary>
public sealed class DpapiAiSecretStore : IAiSecretStore
{
    /// <summary>Name of the encrypted blob under the config root.</summary>
    public const string FileName = "ai-secrets.bin";

    private static readonly byte[] Entropy = "DotCharter.Ai.Secrets.v1"u8.ToArray();

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly object _gate = new();
    private readonly IAppLogger? _logger;
    private readonly string _path;

    /// <summary>Creates a store over <paramref name="filePath" /> (tests pass a temp
    /// path); null means <c>ai-secrets.bin</c> under the config root.</summary>
    public DpapiAiSecretStore(IAppLogger? logger = null, string? filePath = null)
    {
        _logger = logger;
        _path = filePath ?? Path.Combine(WritablePaths.ConfigRoot, FileName);
    }

    /// <summary>True when this platform can seal a secret at rest (Windows/DPAPI).
    /// False elsewhere: <see cref="Write" /> is a no-op and <see cref="Read" />
    /// always answers null.
    /// <para>An INSTANCE property, because it is on
    /// <see cref="IAiSecretStore" /> — the Settings page asked
    /// <c>DpapiAiSecretStore.IsPersistent</c> statically, so with a test double
    /// injected the page still answered for the real store.</para></summary>
    public bool IsPersistent => OperatingSystem.IsWindows();

    /// <summary>The stored key for <paramref name="kind" />, or null when there is
    /// none / the blob cannot be decrypted. Never throws.</summary>
    public string? Read(AiProviderKind kind)
    {
        var all = LoadAll();
        return all is not null && all.TryGetValue(KeyOf(kind), out var secret) && !string.IsNullOrWhiteSpace(secret)
            ? secret
            : null;
    }

    /// <summary>Stores (or, with a null/blank <paramref name="secret" />, removes)
    /// the key for <paramref name="kind" />. Never throws.</summary>
    public void Write(AiProviderKind kind, string? secret)
    {
        if (!OperatingSystem.IsWindows())
        {
            _logger?.Debug(AppLogCategory.Application,
                "AI provider key not persisted: DPAPI is unavailable on this platform.");
            return;
        }

        lock (_gate)
        {
            var all = LoadAllLocked() ?? new Dictionary<string, string>(StringComparer.Ordinal);
            var key = KeyOf(kind);
            if (string.IsNullOrWhiteSpace(secret)) all.Remove(key);
            else all[key] = secret;

            if (all.Count == 0)
            {
                DeleteLocked();
                return;
            }

            try
            {
                var plain = JsonSerializer.SerializeToUtf8Bytes(all, Json);
                var cipher = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
                Array.Clear(plain);
                AtomicFile.WriteAllBytes(_path, cipher);
            }
            catch (Exception ex)
            {
                // Deliberately does NOT include the exception's message beyond its
                // type: a serialization failure can quote the value it choked on.
                _logger?.Warning(AppLogCategory.Application,
                    $"AI provider key could not be persisted: {ex.GetType().Name}.");
            }
        }
    }

    private static string KeyOf(AiProviderKind kind) => kind.ToString();

    private Dictionary<string, string>? LoadAll()
    {
        lock (_gate)
        {
            return LoadAllLocked();
        }
    }

    private Dictionary<string, string>? LoadAllLocked()
    {
        if (!OperatingSystem.IsWindows()) return null;

        try
        {
            if (!File.Exists(_path)) return null;
            var cipher = AtomicFile.ReadAllBytes(_path);
            if (cipher.Length == 0) return null;

            var plain = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
            try
            {
                return JsonSerializer.Deserialize<Dictionary<string, string>>(plain, Json);
            }
            finally
            {
                Array.Clear(plain);
            }
        }
        catch (Exception ex)
        {
            // Wrong user, wrong machine, truncated, tampered — every one of them
            // means "there is no usable key here", which is a state the settings
            // page already renders. The TYPE is logged and the content never is.
            _logger?.Warning(AppLogCategory.Application,
                $"Stored AI provider keys could not be read (treated as absent): {ex.GetType().Name}.");
            return null;
        }
    }

    private void DeleteLocked()
    {
        try
        {
            if (File.Exists(_path)) File.Delete(_path);
        }
        catch (Exception ex)
        {
            _logger?.Warning(AppLogCategory.Application,
                $"Stored AI provider keys could not be deleted: {ex.GetType().Name}.");
        }
    }
}

/// <summary>In-memory store for tests and non-DPAPI platforms.</summary>
public sealed class InMemoryAiSecretStore : IAiSecretStore
{
    private readonly Dictionary<AiProviderKind, string> _secrets = [];

    /// <summary>Always false: nothing here outlives the process.</summary>
    public bool IsPersistent => false;

    /// <inheritdoc />
    public string? Read(AiProviderKind kind) => _secrets.TryGetValue(kind, out var s) ? s : null;

    /// <inheritdoc />
    public void Write(AiProviderKind kind, string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret)) _secrets.Remove(kind);
        else _secrets[kind] = secret;
    }
}
