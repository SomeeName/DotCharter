// .NET 10 file-based program. Signs a release's SHA256SUMS.txt with the
// release-signing Ed25519 key, using the REAL in-app type
// (ReleaseSignature from DotCharter.Services), so what the release signs and
// what GitHubUpdateService verifies cannot drift apart.
//
//   sign <SHA256SUMS.txt> <keyFile> <outSig> <tag>
//       Writes <outSig> (SHA256SUMS.txt.sig). Refuses a key whose public half is
//       not the one pinned in ReleaseSignature.PinnedPublicKeyBase64, and verifies
//       the result exactly as the app will before writing it.
//   keygen <keyFile>
//       Creates a NEW private key (never overwrites one) and prints ONLY its
//       public half. Run once; the private key never enters the repository.
//   pubkey <keyFile>
//       Prints the public half of an existing key.
//
// The key file holds the 32-byte Ed25519 private key (seed) as base64. It is
// never printed. Exit codes: 0 = ok, 1 = failure, 2 = usage.

#:project ../../DotCharter.Services/DotCharter.Services.csproj

using System.Security.Cryptography;
using DotCharter.Services.Update;

if (args.Length == 0) return Usage();

return args[0] switch
{
    "sign" when args.Length == 5 => Sign(args[1], args[2], args[3], args[4]),
    "keygen" when args.Length == 2 => KeyGen(args[1]),
    "pubkey" when args.Length == 2 => PrintPublicKey(args[1]),
    _ => Usage()
};

static int Usage()
{
    Console.Error.WriteLine("usage: dotnet run SignReleaseManifest.cs sign <SHA256SUMS.txt> <keyFile> <outSig> <tag>");
    Console.Error.WriteLine("       dotnet run SignReleaseManifest.cs keygen <keyFile>");
    Console.Error.WriteLine("       dotnet run SignReleaseManifest.cs pubkey <keyFile>");
    return 2;
}

static byte[]? ReadKey(string keyFile)
{
    if (!File.Exists(keyFile))
    {
        Console.Error.WriteLine($"ERROR: release-signing key not found: {keyFile}");
        return null;
    }

    try
    {
        var key = Convert.FromBase64String(File.ReadAllText(keyFile).Trim());
        if (key.Length == 32) return key;
    }
    catch (FormatException)
    {
    }

    // Never echo the file: it is the private key.
    Console.Error.WriteLine($"ERROR: {keyFile} is not a base64 32-byte Ed25519 private key.");
    return null;
}

static int Sign(string sumsPath, string keyFile, string outSig, string tag)
{
    var key = ReadKey(keyFile);
    if (key == null) return 1;

    var pinned = ReleaseSignature.PinnedPublicKey();
    if (!ReleaseSignature.PublicKeyOf(key).AsSpan().SequenceEqual(pinned))
    {
        Console.Error.WriteLine(
            "ERROR: this key is not the one pinned in ReleaseSignature.PinnedPublicKeyBase64; " +
            "every install would refuse a release signed with it.");
        return 1;
    }

    var manifest = File.ReadAllBytes(sumsPath);
    var signature = ReleaseSignature.Sign(manifest, key);
    var problem = ReleaseSignature.Check(manifest, System.Text.Encoding.ASCII.GetBytes(signature), tag, pinned);
    if (problem != null)
    {
        Console.Error.WriteLine($"ERROR: the signed manifest would be refused by the app: {problem}.");
        return 1;
    }

    File.WriteAllText(outSig, signature);
    Console.WriteLine($"signed {Path.GetFileName(sumsPath)} for {tag} -> {Path.GetFileName(outSig)}");
    return 0;
}

static int KeyGen(string keyFile)
{
    var directory = Path.GetDirectoryName(Path.GetFullPath(keyFile));
    if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

    var key = RandomNumberGenerator.GetBytes(32);
    try
    {
        // CreateNew: an existing key is NEVER replaced — losing it means no
        // install can take another update.
        using var stream = new FileStream(keyFile, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream);
        writer.Write(Convert.ToBase64String(key));
    }
    catch (IOException ex)
    {
        Console.Error.WriteLine($"ERROR: {keyFile} could not be created ({ex.Message}); an existing key is never replaced.");
        return 1;
    }

    Console.WriteLine(Convert.ToBase64String(ReleaseSignature.PublicKeyOf(key)));
    return 0;
}

static int PrintPublicKey(string keyFile)
{
    var key = ReadKey(keyFile);
    if (key == null) return 1;
    Console.WriteLine(Convert.ToBase64String(ReleaseSignature.PublicKeyOf(key)));
    return 0;
}
