using System.Security.Cryptography;
using System.Text;

namespace DotCharter.Services.Update;

/// <summary>
/// Persists the GitHub access token DPAPI-encrypted (CurrentUser scope) in
/// a file under the writable config root. The token is never written in
/// plaintext and never logged; a copied file cannot be decrypted by
/// another Windows user or on another machine.
/// </summary>
public sealed class GitHubTokenStore
{
    public const string FileName = "github-token.dat";

    // Additional entropy folded into DPAPI so a generic CurrentUser
    // Unprotect of the file still needs to know it came from DotCharter.
    private static readonly byte[] Entropy = "DotCharter.GitHubToken.v1"u8.ToArray();

    private readonly string _filePath;

    /// <param name="directory">Override for tests; defaults to
    /// <see cref="WritablePaths.ConfigRoot"/>.</param>
    public GitHubTokenStore(string? directory = null) => _filePath = Path.Combine(directory ?? WritablePaths.ConfigRoot, FileName);

    public bool HasToken => File.Exists(_filePath);

    /// <summary>Returns the decrypted token, or null when missing /
    /// undecryptable (wrong user, corrupted file) / non-Windows.</summary>
    public string? Load()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            if (!File.Exists(_filePath)) return null;
            var encrypted = AtomicFile.ReadAllBytes(_filePath);
            var raw = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(raw);
        }
        catch
        {
            // Corrupted or foreign file — treat as signed out.
            return null;
        }
    }

    public void Save(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("Token must not be empty.", nameof(token));
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("DPAPI token storage is Windows-only.");

        var encrypted = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(token), Entropy, DataProtectionScope.CurrentUser);
        AtomicFile.WriteAllBytes(_filePath, encrypted);
    }

    public void Delete()
    {
        try
        {
            if (File.Exists(_filePath)) File.Delete(_filePath);
        }
        catch
        {
            // best effort
        }
    }
}
