namespace DotCharter.Services.Audio.LivePlay;

internal static class PracticeHistoryTrim
{
    // Input order is oldest first. The new run and protected history outrank the cap.
    public static IReadOnlyList<Guid> SelectRemovals(
        IReadOnlyList<(Guid Id, bool Protected)> sessions, Guid newSessionId, int cap) =>
        sessions.Where(session => session.Id != newSessionId && !session.Protected)
            .Take(Math.Max(0, sessions.Count - cap)).Select(session => session.Id).ToArray();
}
