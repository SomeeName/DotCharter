using System.Security.Cryptography;
using System.Text.Json;

namespace DotCharter.Tone.Online.Tone3000;

/// <summary>
///     What survives a restart of a TONE3000 sign-in: the token pair plus
///     enough identity to show "signed in as …" without a network round-trip.
///     Persisted DPAPI-encrypted only — never plaintext, never logged.
/// </summary>
public sealed record Tone3000SessionData(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset AccessTokenExpiresUtc,
    string UserId,
    string UserName);

/// <summary>Persistence for the TONE3000 OAuth session.</summary>
public interface ITone3000TokenStore
{
    void Save(Tone3000SessionData session);

    /// <summary>The persisted session, or null when absent/undecryptable.</summary>
    Tone3000SessionData? Load();

    void Clear();
}

/// <summary>
///     DPAPI-backed store, mirroring <c>Update.GitHubTokenStore</c>:
///     the session JSON is encrypted
///     with <see cref="ProtectedData"/> (CurrentUser scope + app entropy)
///     before touching disk (<c>tone3000-token.dat</c> under the config root).
///     On non-Windows platforms nothing persists — the user signs in again.
///     The file name is on <c>Infrastructure.Backup.ConfigBackupRules</c>'
///     credential list, so it never travels in a backup or LAN update.
/// </summary>
public sealed class DpapiTone3000TokenStore : ITone3000TokenStore
{
    public const string FileName = "tone3000-token.dat";

    private static readonly byte[] Entropy = "DotCharter.Tone3000.v1"u8.ToArray();

    private readonly object _gate = new();
    private readonly IAppLogger? _logger;
    private readonly string _path;

    public DpapiTone3000TokenStore(IAppLogger? logger = null, string? filePath = null)
    {
        _logger = logger;
        _path = filePath ?? Path.Combine(WritablePaths.ConfigRoot, FileName);
    }

    public void Save(Tone3000SessionData session)
    {
        if (!OperatingSystem.IsWindows())
        {
            _logger?.Debug(AppLogCategory.Application,
                "TONE3000 session not persisted: DPAPI is unavailable on this platform.");
            return;
        }

        try
        {
            var plain = JsonSerializer.SerializeToUtf8Bytes(session);
            var cipher = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            Array.Clear(plain);
            lock (_gate)
            {
                AtomicFile.WriteAllBytes(_path, cipher);
            }
        }
        catch (Exception ex)
        {
            _logger?.Warning(AppLogCategory.Application,
                $"TONE3000 session could not be persisted: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public Tone3000SessionData? Load()
    {
        if (!OperatingSystem.IsWindows()) return null;

        try
        {
            byte[] cipher;
            lock (_gate)
            {
                if (!File.Exists(_path)) return null;
                cipher = AtomicFile.ReadAllBytes(_path);
            }

            if (cipher.Length == 0) return null;
            var plain = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
            try
            {
                return JsonSerializer.Deserialize<Tone3000SessionData>(plain);
            }
            finally
            {
                Array.Clear(plain);
            }
        }
        catch (Exception ex)
        {
            // Wrong user, corrupted file, tampering — treat all as signed out.
            _logger?.Warning(AppLogCategory.Application,
                $"Persisted TONE3000 session could not be read (treated as signed out): {ex.GetType().Name}.");
            return null;
        }
    }

    public void Clear()
    {
        try
        {
            lock (_gate)
            {
                if (File.Exists(_path)) File.Delete(_path);
            }
        }
        catch (Exception ex)
        {
            _logger?.Warning(AppLogCategory.Application,
                $"Persisted TONE3000 session could not be deleted: {ex.GetType().Name}: {ex.Message}");
        }
    }
}

/// <summary>In-memory store for tests and non-DPAPI platforms.</summary>
public sealed class InMemoryTone3000TokenStore : ITone3000TokenStore
{
    private Tone3000SessionData? _session;

    public void Save(Tone3000SessionData session) => _session = session;

    public Tone3000SessionData? Load() => _session;

    public void Clear() => _session = null;
}
