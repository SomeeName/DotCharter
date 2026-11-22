using System.Security.Cryptography;

namespace DotCharter.Tests.Audio.Tonelab;

/// <summary>A kernel reservation keeps mock pipe and audio identities unique across test hosts.</summary>
internal sealed class TonelabMockIdentity(int processId, Mutex reservation) : IDisposable
{
    public int ProcessId { get; } = processId;

    public static TonelabMockIdentity Reserve(Func<int>? chooseId = null)
    {
        while (true)
        {
            var pid = chooseId?.Invoke() ?? RandomNumberGenerator.GetInt32(1_900_000_000, int.MaxValue);
            var reservation = new Mutex(false, @"Local\dotcharter-tonelab-mock-" + pid, out var created);
            if (created) return new TonelabMockIdentity(pid, reservation);
            reservation.Dispose();
        }
    }

    public void Dispose() => reservation.Dispose();
}
