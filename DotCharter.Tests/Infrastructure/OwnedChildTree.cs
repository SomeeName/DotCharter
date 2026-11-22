using System.Diagnostics;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     A child process TREE a test can recognise as its own and watch die
///     (#607), without looking at the machine's process list.
///     <para>
///         The script starts a <c>ping</c> grandchild whose output is redirected
///         into a file in the test's own folder. The redirect's handle is
///         inherited down the tree, so the file cannot be opened exclusively while
///         any process of THIS tree lives, and can be the moment the last one is
///         gone. That is the start handshake and the proof of the kill in one:
///         no process names, no ids, nothing another test's or the user's
///         <c>ping</c> can satisfy or spoil.
///     </para>
/// </summary>
internal sealed class OwnedChildTree
{
    /// <summary>Every wait here is a deadlock detector. The grandchild is given several
    /// times this long to live, so "still alive at the cap" cannot be a tree that was
    /// about to end on its own.</summary>
    private static readonly TimeSpan Cap = TimeSpan.FromSeconds(60);

    private readonly string _lock;

    private OwnedChildTree(string script, string lockFile)
    {
        Script = script;
        _lock = lockFile;
    }

    /// <summary>The <c>.cmd</c> to run through <c>cmd.exe /c</c>. Never returns on its own
    /// within the caps: the ping runs for five minutes.</summary>
    public string Script { get; }

    /// <summary>Writes the script into <paramref name="directory" /> (the test's own
    /// temp folder). <paramref name="afterwards" /> runs if the ping is ever allowed to
    /// finish, e.g. a marker a killed tree can never write.</summary>
    public static OwnedChildTree Create(string directory, string afterwards = "")
    {
        var name = "tree-" + Guid.NewGuid().ToString("N");
        var script = Path.Combine(directory, name + ".cmd");
        var lockFile = Path.Combine(directory, name + ".lock");
        File.WriteAllText(script,
            $"@echo off\r\nping -n 300 127.0.0.1 > \"%~dp0{name}.lock\" 2>&1\r\n{afterwards}\r\nexit /b 0\r\n");
        return new OwnedChildTree(script, lockFile);
    }

    /// <summary>True while a process of this tree holds the redirect open.</summary>
    public bool IsAlive()
    {
        try
        {
            using var exclusive = new FileStream(_lock, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>Waits until the grandchild is running: the redirect is held AND the ping
    /// has written into it. Fails when that does not happen.</summary>
    public async Task WaitUntilRunningAsync(CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        while (!(IsAlive() && HasOutput()))
        {
            Assert.True(clock.Elapsed < Cap,
                $"the child tree never started within {Cap.TotalSeconds:0} s: its ping wrote nothing into '{_lock}'.");
            await Task.Delay(20, ct);
        }
    }

    /// <summary>Waits until no process of the tree is left. Fails with
    /// <paramref name="stillAlive" /> when one is.</summary>
    public async Task WaitUntilGoneAsync(string stillAlive, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        while (IsAlive())
        {
            Assert.True(clock.Elapsed < Cap, $"{stillAlive} (its ping still holds '{_lock}' after {Cap.TotalSeconds:0} s)");
            await Task.Delay(20, ct);
        }
    }

    private bool HasOutput()
    {
        try
        {
            using var shared = new FileStream(_lock, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return shared.Length > 0;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
