using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     DotCharter ships no Rocksmith keys (#756): they come from the user's key file
///     (<c>RocksmithKeys</c>). This lint fails when a real key appears in any TRACKED
///     file (<c>git ls-files</c>; the submodule is its own repository) — as raw bytes,
///     as hex (contiguous, separated, or a <c>0x..</c> byte list) or as base64. It
///     knows the keys only by hashes: a rolling hash picks candidate 32-byte windows
///     out of every file and every byte run it decodes, and a candidate counts when
///     its SHA-256 is one of <see cref="TestRocksmithKeys.RealKeySha256" />.
/// </summary>
public sealed partial class RocksmithKeyLeakLintTests
{
    /// <summary>The rolling hashes (<see cref="Rolling" />) of the real keys, in the order of
    /// <see cref="TestRocksmithKeys.RealKeySha256" /> — the cheap first filter; 64 bits of a
    /// 256-bit key do not give it back.</summary>
    private static readonly ulong[] RealKeyRolling = [0xF2A0098D911F840E, 0x15350A7C081E9430, 0x3B8D715A7C7C5DED];

    [Fact]
    public void No_tracked_file_holds_a_rocksmith_key()
    {
        var root = TestPaths.RepoRoot();
        Assert.SkipUnless(Path.Exists(Path.Combine(root, ".git")),
            "Not a git checkout (no .git) — the tracked-file list is unknowable here.");

        var tracked = TrackedFiles(root);
        // The sentinel the scan must reach: a lint that lists no files passes vacuously.
        Assert.Contains("DotCharter.Services/Infrastructure/Psarc/RocksmithKeys.cs", tracked);
        Assert.True(tracked.Count > 1000, $"Only {tracked.Count} tracked files listed.");

        var fingerprints = new Fingerprints([.. RealKeyRolling], TestRocksmithKeys.RealKeySha256.ToHashSet(StringComparer.Ordinal));
        var hits = new List<string>();
        var scanned = 0;
        foreach (var relative in tracked)
        {
            var path = Path.Combine(root, relative);
            if (!File.Exists(path)) continue; // the submodule gitlink, or a file deleted in the work tree

            scanned++;
            if (HoldsKey(File.ReadAllBytes(path), fingerprints)) hits.Add(relative);
        }

        Assert.True(scanned > 1000, $"Only {scanned} tracked files scanned.");
        Assert.True(hits.Count == 0,
            "A Rocksmith key is in tracked files — DotCharter ships none; the keys come from the user's key file " +
            "(docs/agents/services/parsing-psarc.md):\n" + string.Join('\n', hits));
    }

    /// <summary>The probe twin: a synthetic key is found in every encoding the scan
    /// decodes, and text without it is clean.</summary>
    [Fact]
    public void The_scan_finds_a_key_in_every_encoding()
    {
        var key = TestRocksmithKeys.SyntheticSngPc;
        var fingerprints = new Fingerprints([Rolling(key)], [Convert.ToHexStringLower(SHA256.HashData(key))]);
        var hex = Convert.ToHexString(key);

        string[] encodings =
        [
            "static byte[] K = [\n    " + string.Join(", ", key.Select(b => $"0x{b:X2}")) + "\n];",
            "sng-pc = " + hex,
            "key: " + hex.ToLowerInvariant(),
            "key: " + string.Join(' ', key.Select(b => $"{b:x2}")),
            "key: " + string.Join(':', key.Select(b => $"{b:X2}")),
            "key = \"" + Convert.ToBase64String(key) + "\""
        ];
        foreach (var text in encodings)
            Assert.True(HoldsKey(Encoding.UTF8.GetBytes(text), fingerprints), $"Not found in: {text}");

        // Raw bytes inside a binary file, at an odd offset.
        byte[] binary = [0, 1, 2, .. key, 9, 9];
        Assert.True(HoldsKey(binary, fingerprints), "Not found as raw bytes.");

        // One byte changed: no hit.
        var other = (byte[])key.Clone();
        other[5] ^= 1;
        Assert.False(HoldsKey(Encoding.UTF8.GetBytes(Convert.ToHexString(other)), fingerprints));
        Assert.False(HoldsKey(other, fingerprints));
    }

    /// <summary>Owner-only: the rolling hashes this lint filters on are those of the real keys.</summary>
    [Fact]
    public void The_rolling_fingerprints_are_the_real_keys()
    {
        TestRocksmithKeys.RequireRealKeys();
        var keys = DotCharter.Services.Infrastructure.Psarc.RocksmithKeys.Current!;
        Assert.Equal(RealKeyRolling, new[] { Rolling(keys.TocKey), Rolling(keys.SngPcKey), Rolling(keys.SngMacKey) });
    }

    private static bool HoldsKey(byte[] content, Fingerprints fingerprints)
    {
        if (AnyWindow(content, fingerprints)) return true;

        var text = Encoding.Latin1.GetString(content);
        foreach (Match run in HexRun().Matches(text))
        {
            var digits = string.Concat(run.Value.Where(char.IsAsciiHexDigit));
            if (digits.Length % 2 == 1) digits = digits[1..];
            if (AnyWindow(Convert.FromHexString(digits), fingerprints)) return true;
        }

        foreach (Match list in ByteList().Matches(text))
        {
            var bytes = list.Groups["b"].Captures.Select(c => Convert.ToByte(c.Value, 16)).ToArray();
            if (AnyWindow(bytes, fingerprints)) return true;
        }

        foreach (Match b64 in Base64Run().Matches(text))
        {
            var value = b64.Value[..(b64.Value.Length / 4 * 4)];
            var decoded = new byte[value.Length / 4 * 3];
            if (Convert.TryFromBase64String(value, decoded, out var written) && AnyWindow(decoded[..written], fingerprints))
                return true;
        }

        return false;
    }

    /// <summary>A rolling (Rabin-Karp, base 257, mod 2^64) hash finds candidate
    /// 32-byte windows in one pass; only a candidate is SHA-256'd.</summary>
    private static bool AnyWindow(byte[] bytes, Fingerprints fingerprints)
    {
        if (bytes.Length < 32) return false;
        ulong h = 0;
        for (var i = 0; i < 32; i++) h = unchecked((h * RollBase) + bytes[i]);
        for (var i = 0; ; i++)
        {
            if (fingerprints.Rolling.Contains(h) && IsKey(bytes.AsSpan(i, 32), fingerprints)) return true;
            if (i + 32 >= bytes.Length) return false;
            h = unchecked((h * RollBase) + bytes[i + 32] - (bytes[i] * RollBase32));
        }
    }

    private static bool IsKey(ReadOnlySpan<byte> window, Fingerprints fingerprints) =>
        fingerprints.Sha256.Contains(Convert.ToHexStringLower(SHA256.HashData(window)));

    /// <summary>The rolling hash of one 32-byte key.</summary>
    internal static ulong Rolling(ReadOnlySpan<byte> key)
    {
        ulong h = 0;
        foreach (var b in key) h = unchecked((h * RollBase) + b);
        return h;
    }

    private const ulong RollBase = 257;
    private static readonly ulong RollBase32 = Enumerable.Range(0, 32).Aggregate(1UL, (acc, _) => unchecked(acc * RollBase));

    /// <summary>What the scan knows of a key: two hashes, never the key.</summary>
    private sealed record Fingerprints(HashSet<ulong> Rolling, HashSet<string> Sha256);

    /// <summary>64+ hex digits, optionally one space, colon or dash between byte pairs.</summary>
    [GeneratedRegex(@"(?:[0-9A-Fa-f]{2}[ :\-]?){32,}")]
    private static partial Regex HexRun();

    /// <summary>A list of 32+ <c>0xNN</c> bytes, with commas, casts and whitespace between.</summary>
    [GeneratedRegex(@"(?:0[xX](?<b>[0-9A-Fa-f]{2})\b[^0-9A-Za-z]{0,24}?){32,}")]
    private static partial Regex ByteList();

    [GeneratedRegex(@"[A-Za-z0-9+/]{43,}=?")]
    private static partial Regex Base64Run();

    private static List<string> TrackedFiles(string root)
    {
        var start = new ProcessStartInfo("git", "ls-files -z")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8
        };
        using var git = Process.Start(start) ?? throw new InvalidOperationException("git did not start.");
        var stderr = git.StandardError.ReadToEndAsync();
        var output = git.StandardOutput.ReadToEnd();
        git.WaitForExit();
        Assert.True(git.ExitCode == 0, $"git ls-files failed ({git.ExitCode}): {stderr.Result}");
        return [.. output.Split('\0', StringSplitOptions.RemoveEmptyEntries)];
    }
}
