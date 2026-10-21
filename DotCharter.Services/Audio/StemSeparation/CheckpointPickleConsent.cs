using System.Security.Cryptography;

namespace DotCharter.Services.Audio.StemSeparation;

/// <summary>
///     The user's per-URL "this file may run code on my PC" consent for a pickled
///     PyTorch checkpoint (<c>.ckpt</c> / <c>.pt</c> / <c>.pth</c>), bound to the
///     SHA-256 of the file they accepted.
///     <para>
///         Loading a pickle runs whatever code it names. The converter always tries a
///         weights-only load first; only a URL with consent may fall back to a full
///         unpickle, and <see cref="StemModelManager.ImportCheckpointAsync" /> refuses
///         a pickled checkpoint without consent before downloading anything.
///         <c>.safetensors</c> is not a pickle and needs none.
///     </para>
///     <para>
///         Consent is granted for a URL before its first download and pinned to the
///         downloaded file's hash on first use. If the bytes behind the URL later
///         change, the consent is withdrawn and the user is asked again.
///     </para>
/// </summary>
public sealed class CheckpointPickleConsent(IConfigurationService? config)
{
    private readonly Dictionary<string, string> _volatile = new(StringComparer.Ordinal);

    /// <summary><c>true</c> for the checkpoint formats that are pickles.</summary>
    public static bool IsPickleFormat(string url)
    {
        var path = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? Uri.UnescapeDataString(uri.AbsolutePath) : url;
        return Path.GetExtension(path).ToLowerInvariant() is ".ckpt" or ".pt" or ".pth";
    }

    public bool IsGranted(string url) => Read().ContainsKey(url);

    /// <summary>Records the user's explicit consent for <paramref name="url" />; the hash is pinned on first use.</summary>
    public void Grant(string url) => Write(map => map.TryAdd(url, string.Empty));

    public void Revoke(string url) => Write(map => map.Remove(url));

    /// <summary>
    ///     Whether a full unpickle of the file downloaded from <paramref name="url" /> is
    ///     allowed: consent exists and was given for these exact bytes (a first use pins
    ///     them). A changed file withdraws the consent.
    /// </summary>
    public bool AllowsFile(string url, string sha256)
    {
        if (!Read().TryGetValue(url, out var pinned)) return false;
        if (pinned.Length == 0)
        {
            Write(map => map[url] = sha256);
            return true;
        }

        if (string.Equals(pinned, sha256, StringComparison.OrdinalIgnoreCase)) return true;
        Revoke(url);
        return false;
    }

    public static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
    }

    private IReadOnlyDictionary<string, string> Read() =>
        config?.LoadAppConfig().StemCheckpointPickleConsent ?? _volatile;

    private void Write(Action<Dictionary<string, string>> mutate)
    {
        if (config is null)
        {
            mutate(_volatile);
            return;
        }

        config.Update(cfg => mutate(cfg.StemCheckpointPickleConsent));
    }
}

/// <summary>A pickled checkpoint import without consent for the file it downloaded.</summary>
public sealed class CheckpointConsentException(string message) : InvalidOperationException(message);
